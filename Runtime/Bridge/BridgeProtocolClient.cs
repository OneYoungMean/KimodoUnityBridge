using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    internal sealed class BridgeProtocolResponse
    {
        public JObject Header { get; set; }
        public byte[] BinaryPayload { get; set; }
        public string TaskId { get; set; }
        public string RequestId { get; set; }
    }

    internal sealed class BridgeProtocolClient : IDisposable
    {
        private sealed class PendingRequest
        {
            internal PendingRequest(string requestId, string taskId, Action<string> progress, int loadingTimeoutMs)
            {
                RequestId = requestId;
                TaskId = taskId;
                Progress = progress;
                LoadingTimeoutMs = loadingTimeoutMs;
                CreatedAtUtc = DateTime.UtcNow;
                Completion = new TaskCompletionSource<BridgeProtocolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            internal string RequestId { get; }
            internal string TaskId { get; }
            internal Action<string> Progress { get; }
            internal int LoadingTimeoutMs { get; }
            internal DateTime CreatedAtUtc { get; }
            internal TaskCompletionSource<BridgeProtocolResponse> Completion { get; }
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, PendingRequest> pending =
            new ConcurrentDictionary<string, PendingRequest>(StringComparer.Ordinal);
        private readonly object connectionLock = new object();
        private readonly int connectTimeoutMs;
        private readonly int ioTimeoutMs;
        private readonly int modelLoadingTimeoutMs;

        private TcpClient sharedClient;
        private NetworkStream sharedStream;
        private string sharedHost = string.Empty;
        private int sharedPort = -1;
        private CancellationTokenSource readerCts;
        private bool disposed;
        private int disposeStarted;

        public BridgeProtocolClient(
            int connectTimeoutMs = BridgeRuntimeDefaults.ConnectTimeoutMs,
            int ioTimeoutMs = BridgeRuntimeDefaults.IoTimeoutMs,
            int modelLoadingTimeoutMs = BridgeRuntimeDefaults.ModelLoadingTimeoutMs)
        {
            this.connectTimeoutMs = Math.Max(500, connectTimeoutMs);
            this.ioTimeoutMs = Math.Max(1000, ioTimeoutMs);
            this.modelLoadingTimeoutMs = Math.Max(10000, modelLoadingTimeoutMs);
        }

        public bool IsConnected
        {
            get
            {
                lock (connectionLock)
                {
                    return sharedClient != null && sharedClient.Connected && sharedStream != null;
                }
            }
        }

        public async Task ConnectAsync(string host, int port, CancellationToken token)
        {
            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        internal async Task<string> OpenSessionAsync(string host, int port, CancellationToken token)
        {
            BridgeProtocolResponse response = await SendRequestAsync(
                host,
                port,
                new JObject { ["cmd"] = "session.open" },
                null,
                null,
                token,
                reconnect: true).ConfigureAwait(false);
            return response?.Header?.Value<string>("session_id") ?? string.Empty;
        }

        internal Task<BridgeProtocolResponse> CloseSessionAsync(
            string host,
            int port,
            CancellationToken token)
        {
            return SendRequestAsync(
                host,
                port,
                new JObject { ["cmd"] = "session.close" },
                null,
                null,
                token,
                reconnect: false);
        }

        internal Task<BridgeProtocolResponse> GetHelpAsync(
            string host,
            int port,
            CancellationToken token)
        {
            return SendRequestAsync(
                host,
                port,
                new JObject { ["cmd"] = "help" },
                null,
                null,
                token,
                reconnect: true);
        }

        internal Task<BridgeProtocolResponse> ListModelConfigurationsAsync(
            string host,
            int port,
            string model,
            string textEncoderMode,
            string modelsRoot,
            CancellationToken token)
        {
            return SendRequestAsync(
                host,
                port,
                new JObject
                {
                    ["cmd"] = "runtime.list_models",
                    ["model"] = string.IsNullOrWhiteSpace(model) ? null : model,
                    ["text_encoder_mode"] = string.IsNullOrWhiteSpace(textEncoderMode)
                        ? KimodoTextEncoderModeProtocol.HighPrecision
                        : textEncoderMode,
                    ["models_root"] = modelsRoot ?? string.Empty
                },
                null,
                null,
                token,
                reconnect: true);
        }

        internal Task<BridgeProtocolResponse> GenerateAsync(
            string host,
            int port,
            KimodoGenerationRequestDto request,
            Action<string> progress,
            CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string taskId = string.IsNullOrWhiteSpace(request.task_id) ? Guid.NewGuid().ToString("N") : request.task_id.Trim();
            request.task_id = taskId;
            var attachments = new List<byte[]>();
            string baseConstraintsJson = request.constraints?.Serialize(request.model, attachments) ?? string.Empty;
            var constraints = string.IsNullOrWhiteSpace(baseConstraintsJson)
                ? new JArray()
                : JArray.Parse(baseConstraintsJson);
            if (request.analysis_clip_constraints != null && request.analysis_clip_constraints.Count > 0)
            {
                var clips = new JArray();
                for (int index = 0; index < request.analysis_clip_constraints.Count; index++)
                {
                    KimodoKmbClipConstraint clip = request.analysis_clip_constraints[index]
                        ?? throw new InvalidOperationException("Analysis KMB clip is null.");
                    byte[] bytes = clip.motionBytes;
                    if (bytes == null || bytes.Length == 0)
                    {
                        throw new InvalidOperationException("Analysis KMB clip is empty.");
                    }
                    if (clip.startFrame < 0 || clip.endFrameExclusive <= clip.startFrame)
                    {
                        throw new InvalidOperationException("Analysis KMB clip frame range must be non-empty and non-negative.");
                    }
                    int attachment = attachments.Count;
                    attachments.Add(bytes);
                    clips.Add(new JObject
                    {
                        ["type"] = "clip",
                        ["format"] = "kmb_attachment_v1",
                        ["attachment"] = attachment,
                        ["start_frame"] = clip.startFrame,
                        ["end_frame_exclusive"] = clip.endFrameExclusive
                    });
                }
                foreach (JToken clip in clips) constraints.Add(clip.DeepClone());
            }
            string constraintsJson = constraints.Count > 0
                ? constraints.ToString(Formatting.None)
                : baseConstraintsJson;
            var payload = new JObject
            {
                ["cmd"] = "generate",
                ["task_id"] = taskId,
                ["time_as_double"] = request.time_as_double
            };
            if (!request.ardy_session_update_only)
            {
                payload["output_format"] = string.IsNullOrWhiteSpace(request.output_format)
                    ? "kmb_v1"
                    : request.output_format.Trim();
                if (request.duration.HasValue)
                {
                    float duration = request.duration.Value;
                    if (float.IsNaN(duration) || float.IsInfinity(duration) || duration <= 0f)
                    {
                        throw new InvalidOperationException("duration must be a finite positive number when provided.");
                    }
                    payload["duration"] = duration;
                }
                payload["diffusion_steps"] = request.steps;
                payload["seed"] = request.seed.HasValue ? request.seed.Value : null;
                payload["transition_duration"] = request.transition_duration;
                payload["model"] = string.IsNullOrWhiteSpace(request.model) ? null : request.model;
                payload["text_encoder_mode"] = string.IsNullOrWhiteSpace(request.text_encoder_mode)
                    ? KimodoTextEncoderModeProtocol.HighPrecision
                    : request.text_encoder_mode;
                payload["models_root"] = request.models_root ?? string.Empty;
                payload["force_hf_download"] = request.force_hf_download;
                payload["owner_pid"] = request.owner_pid;
                if (request.ardy_timeline_segments != null && request.ardy_timeline_segments.Count > 0)
                {
                    var timelineSegments = new JArray();
                    for (int i = 0; i < request.ardy_timeline_segments.Count; i++)
                    {
                        KimodoArdyTimelineSegmentDto segment = request.ardy_timeline_segments[i];
                        if (segment == null)
                        {
                            throw new InvalidOperationException("ARDY timeline segment is null.");
                        }
                        if (float.IsNaN(segment.duration) || float.IsInfinity(segment.duration) || segment.duration <= 0f)
                        {
                            throw new InvalidOperationException("ARDY timeline segment duration must be finite and positive.");
                        }
                        timelineSegments.Add(new JObject
                        {
                            ["prompt"] = segment.prompt ?? string.Empty,
                            ["duration"] = segment.duration
                        });
                    }
                    payload["ardy_timeline_segments"] = timelineSegments;
                }
            }
            if (request.prompt != null)
            {
                payload["prompt"] = request.prompt;
            }
            if (constraintsJson != null)
            {
                payload["constraints_json"] = constraintsJson;
            }
            if (!string.IsNullOrWhiteSpace(request.analysis_option_json))
            {
                try
                {
                    payload["analysis_option"] = JToken.Parse(request.analysis_option_json);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"analysis_option must be a JSON object: {ex.Message}");
                }
            }
            if (!request.ardy_session_update_only && request.simulate_free_vram_gb.HasValue)
            {
                payload["simulate_free_vram_gb"] = Math.Max(0, request.simulate_free_vram_gb.Value);
            }
            if (!request.ardy_session_update_only)
            {
                AddOptional(payload, "ardy_history_weight", request.ardy_history_weight);
                AddOptional(payload, "ardy_max_speed", request.ardy_max_speed);
                AddOptional(payload, "ardy_max_acceleration", request.ardy_max_acceleration);
                AddOptional(payload, "ardy_playback_reserve_seconds", request.ardy_playback_reserve_seconds);
            }

            byte[] binaryPayload = null;
            if (attachments.Count > 0)
            {
                var manifest = new JArray();
                using var stream = new MemoryStream();
                for (int index = 0; index < attachments.Count; index++)
                {
                    byte[] attachment = attachments[index] ?? Array.Empty<byte>();
                    if (attachment.Length == 0)
                    {
                        throw new InvalidOperationException("KMB attachment is empty.");
                    }
                    manifest.Add(new JObject
                    {
                        ["index"] = index,
                        ["offset"] = stream.Length,
                        ["byte_length"] = attachment.Length
                    });
                    stream.Write(attachment, 0, attachment.Length);
                }
                binaryPayload = stream.ToArray();
                payload["kmb_attachments"] = manifest;
                payload["attachment_byte_length"] = binaryPayload.Length;
            }

            UnityEngine.Debug.Log($"[KimodoBridge] Generate JSON: {payload.ToString(Formatting.None)}");
            return SendRequestAsync(host, port, payload, binaryPayload, progress, token, reconnect: true);
        }

        private static void AddOptional(JObject payload, string name, double? value)
        {
            if (value.HasValue)
            {
                payload[name] = value.Value;
            }
        }

        private static void AddOptional(JObject payload, string name, bool? value)
        {
            if (value.HasValue)
            {
                payload[name] = value.Value;
            }
        }

        internal async Task<bool> TryCancelGenerateAsync(
            string host,
            int port,
            string taskId,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || !IsConnected)
            {
                return false;
            }
            try
            {
                var request = new JObject { ["cmd"] = "cancel" };
                if (!string.IsNullOrWhiteSpace(taskId))
                {
                    request["task_id"] = taskId.Trim();
                }
                await SendRequestAsync(host, port, request, null, null, token, reconnect: false).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<BridgeProtocolResponse> SendRequestAsync(
            string host,
            int port,
            JObject request,
            byte[] binaryPayload,
            Action<string> progress,
            CancellationToken token,
            bool reconnect)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            string requestId = Guid.NewGuid().ToString("N");
            request["request_id"] = requestId;
            string taskId = request.Value<string>("task_id") ?? string.Empty;
            var item = new PendingRequest(requestId, taskId, progress, modelLoadingTimeoutMs);
            if (!pending.TryAdd(requestId, item))
            {
                throw new InvalidOperationException("Bridge request id collision.");
            }

            try
            {
                await writeLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    ThrowIfDisposed();
                    if (reconnect)
                    {
                        await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
                    }
                    else if (!IsConnected || !string.Equals(sharedHost, host, StringComparison.OrdinalIgnoreCase) || sharedPort != port)
                    {
                        throw new IOException("Bridge persistent connection is not available.");
                    }
                    await WriteJsonLineAsync(sharedStream, request, token).ConfigureAwait(false);
                    if (binaryPayload != null && binaryPayload.Length > 0)
                    {
                        await WithIoTimeoutAsync(
                            sharedStream.WriteAsync(binaryPayload, 0, binaryPayload.Length, token),
                            token,
                            "Bridge binary write timeout.").ConfigureAwait(false);
                        await WithIoTimeoutAsync(sharedStream.FlushAsync(), token, "Bridge flush timeout.").ConfigureAwait(false);
                    }
                }
                finally
                {
                    writeLock.Release();
                }

                using (token.Register(() =>
                {
                    if (pending.TryRemove(requestId, out PendingRequest cancelled))
                    {
                        cancelled.Completion.TrySetCanceled();
                    }
                }))
                {
                    return await item.Completion.Task.ConfigureAwait(false);
                }
            }
            catch
            {
                pending.TryRemove(requestId, out _);
                throw;
            }
        }

        private async Task EnsureSharedConnectionAsync(string host, int port, CancellationToken token)
        {
            if (IsConnected && string.Equals(sharedHost, host, StringComparison.OrdinalIgnoreCase) && sharedPort == port)
            {
                return;
            }
            CloseSharedConnectionSync(new IOException("Bridge endpoint changed."));
            var client = new TcpClient { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(connectTimeoutMs);
            Task connectTask = client.ConnectAsync(host, port);
            Task timeoutTask = Task.Delay(Timeout.Infinite, connectCts.Token);
            if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) != connectTask)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"Bridge connect timeout: {host}:{port}");
            }
            await connectTask.ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            client.SendTimeout = ioTimeoutMs;
            lock (connectionLock)
            {
                sharedClient = client;
                sharedStream = stream;
                sharedHost = host;
                sharedPort = port;
                readerCts = new CancellationTokenSource();
                _ = Task.Run(() => ReaderLoopAsync(stream, readerCts.Token));
            }
        }

        private async Task ReaderLoopAsync(NetworkStream stream, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    DispatchResponse(await ReadResponseAsync(stream, token).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!disposed)
                {
                    CloseSharedConnectionSync(exception);
                }
            }
        }

        private void DispatchResponse(BridgeProtocolResponse response)
        {
            string requestId = response?.RequestId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(requestId) || !pending.TryGetValue(requestId, out PendingRequest item))
            {
                return;
            }
            JObject header = response.Header;
            string status = header?.Value<string>("status") ?? string.Empty;
            string message = header?.Value<string>("message") ?? string.Empty;
            if (status.Equals("loading", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("initializing", StringComparison.OrdinalIgnoreCase))
            {
                if ((DateTime.UtcNow - item.CreatedAtUtc).TotalMilliseconds > item.LoadingTimeoutMs)
                {
                    if (pending.TryRemove(requestId, out _))
                    {
                        item.Completion.TrySetException(new TimeoutException(
                            $"Bridge model loading timeout (>{item.LoadingTimeoutMs}ms)."));
                    }
                }
                else
                {
                    SafeReportProgress(item, message);
                }
                return;
            }
            if (status.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("progress", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("cancelling", StringComparison.OrdinalIgnoreCase))
            {
                SafeReportProgress(item, message);
                return;
            }
            if (!pending.TryRemove(requestId, out _))
            {
                return;
            }
            if (status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("busy", StringComparison.OrdinalIgnoreCase))
            {
                item.Completion.TrySetException(new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message) ? $"Bridge request failed: {status}." : message));
            }
            else if (status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                item.Completion.TrySetException(new OperationCanceledException(
                    string.IsNullOrWhiteSpace(message) ? "Bridge request cancelled." : message));
            }
            else
            {
                item.Completion.TrySetResult(response);
            }
        }

        private static void SafeReportProgress(PendingRequest item, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try { item.Progress?.Invoke(message); } catch { }
        }

        private async Task WriteJsonLineAsync(NetworkStream stream, JObject request, CancellationToken token)
        {
            byte[] bytes = Utf8NoBom.GetBytes(request.ToString(Formatting.None) + "\n");
            await WithIoTimeoutAsync(stream.WriteAsync(bytes, 0, bytes.Length, token), token, "Bridge write timeout.").ConfigureAwait(false);
            await WithIoTimeoutAsync(stream.FlushAsync(), token, "Bridge flush timeout.").ConfigureAwait(false);
        }

        private async Task<BridgeProtocolResponse> ReadResponseAsync(NetworkStream stream, CancellationToken token)
        {
            JObject header = await ReadJsonLineAsync(stream, token).ConfigureAwait(false);
            int byteLength = Math.Max(0, header.Value<int?>("byte_length") ?? 0);
            return new BridgeProtocolResponse
            {
                Header = header,
                BinaryPayload = byteLength > 0 ? await ReadExactBytesAsync(stream, byteLength, token).ConfigureAwait(false) : null,
                TaskId = header.Value<string>("task_id") ?? string.Empty,
                RequestId = header.Value<string>("request_id") ?? string.Empty
            };
        }

        private static async Task<JObject> ReadJsonLineAsync(NetworkStream stream, CancellationToken token)
        {
            using var buffer = new MemoryStream(256);
            byte[] one = new byte[1];
            while (true)
            {
                int read = await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false);
                if (read <= 0) throw new IOException("Bridge connection closed while reading a response.");
                if (one[0] == (byte)'\n') break;
                buffer.WriteByte(one[0]);
            }
            string line = Utf8NoBom.GetString(buffer.ToArray()).Trim();
            if (!(JToken.Parse(line) is JObject result))
            {
                throw new IOException("Bridge response is not a JSON object.");
            }
            return result;
        }

        private async Task<byte[]> ReadExactBytesAsync(NetworkStream stream, int byteLength, CancellationToken token)
        {
            byte[] result = new byte[byteLength];
            int offset = 0;
            while (offset < byteLength)
            {
                int read = await WithIoTimeoutAsync(
                    stream.ReadAsync(result, offset, byteLength - offset, token),
                    token,
                    "Bridge binary read timeout.").ConfigureAwait(false);
                if (read <= 0) throw new IOException("Bridge connection closed while reading binary data.");
                offset += read;
            }
            return result;
        }

        private async Task WithIoTimeoutAsync(Task task, CancellationToken token, string message)
        {
            if (await Task.WhenAny(task, Task.Delay(ioTimeoutMs, token)).ConfigureAwait(false) != task)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException(message);
            }
            await task.ConfigureAwait(false);
        }

        private async Task<T> WithIoTimeoutAsync<T>(Task<T> task, CancellationToken token, string message)
        {
            if (await Task.WhenAny(task, Task.Delay(ioTimeoutMs, token)).ConfigureAwait(false) != task)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException(message);
            }
            return await task.ConfigureAwait(false);
        }

        public async Task DetachAsync()
        {
            await writeLock.WaitAsync().ConfigureAwait(false);
            try { CloseSharedConnectionSync(null); }
            finally { writeLock.Release(); }
        }

        private void CloseSharedConnectionSync(Exception reason)
        {
            CancellationTokenSource cts;
            lock (connectionLock)
            {
                cts = readerCts;
                readerCts = null;
                try { sharedStream?.Dispose(); } catch { }
                try { sharedClient?.Dispose(); } catch { }
                sharedStream = null;
                sharedClient = null;
                sharedHost = string.Empty;
                sharedPort = -1;
            }
            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }
            if (reason != null)
            {
                foreach (var pair in pending)
                {
                    if (pending.TryRemove(pair.Key, out PendingRequest item))
                    {
                        item.Completion.TrySetException(reason);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
            disposed = true;
            CloseSharedConnectionSync(new OperationCanceledException("Bridge protocol client is closing."));
        }

        public async Task DisposeAsync(int timeoutMs = 300)
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
            disposed = true;
            await Task.Yield();
            CloseSharedConnectionSync(new OperationCanceledException("Bridge protocol client is closing."));
            writeLock.Dispose();
            _ = timeoutMs;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(BridgeProtocolClient));
        }
    }
}
