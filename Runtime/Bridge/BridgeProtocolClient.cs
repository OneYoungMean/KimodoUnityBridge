using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
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
    }

    internal sealed class BridgeProtocolClient : IDisposable
    {
        private sealed class PendingGenerateRequest
        {
            private int completed;

            internal PendingGenerateRequest(string taskId, Action<string> progress, int modelLoadingTimeoutMs)
            {
                TaskId = taskId;
                Progress = progress;
                ModelLoadingTimeoutMs = modelLoadingTimeoutMs;
                CreatedAtUtc = DateTime.UtcNow;
                Completion = new TaskCompletionSource<BridgeProtocolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            internal string TaskId { get; }
            internal Action<string> Progress { get; }
            internal int ModelLoadingTimeoutMs { get; }
            internal DateTime CreatedAtUtc { get; }
            internal TaskCompletionSource<BridgeProtocolResponse> Completion { get; }

            internal bool TrySetResult(BridgeProtocolResponse response)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0)
                {
                    return false;
                }

                return Completion.TrySetResult(response);
            }

            internal bool TrySetException(Exception exception)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0)
                {
                    return false;
                }

                return Completion.TrySetException(exception);
            }

            internal bool TrySetCanceled(string message)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0)
                {
                    return false;
                }

                return Completion.TrySetException(new OperationCanceledException(
                    string.IsNullOrWhiteSpace(message) ? "Bridge generation cancelled." : message));
            }
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        private readonly object disposeGate = new object();
        private readonly object pendingGate = new object();
        private readonly int connectTimeoutMs;
        private readonly int ioTimeoutMs;
        private readonly int modelLoadingTimeoutMs;
        private readonly int modelLoadingPollIntervalMs;

        private readonly Dictionary<string, PendingGenerateRequest> pendingRequests =
            new Dictionary<string, PendingGenerateRequest>(StringComparer.Ordinal);

        private TcpClient sharedClient;
        private NetworkStream sharedStream;
        private string sharedHost = string.Empty;
        private int sharedPort = -1;
        private bool disposed;
        private int disposeStarted;
        private CancellationTokenSource readerCts;
        private Task readerTask;

        public BridgeProtocolClient(
            int connectTimeoutMs = BridgeRuntimeDefaults.ConnectTimeoutMs,
            int ioTimeoutMs = BridgeRuntimeDefaults.IoTimeoutMs,
            int modelLoadingTimeoutMs = BridgeRuntimeDefaults.ModelLoadingTimeoutMs,
            int modelLoadingPollIntervalMs = BridgeRuntimeDefaults.ModelLoadingPollIntervalMs)
        {
            this.connectTimeoutMs = Math.Max(500, connectTimeoutMs);
            this.ioTimeoutMs = Math.Max(1000, ioTimeoutMs);
            this.modelLoadingTimeoutMs = Math.Max(10000, modelLoadingTimeoutMs);
            this.modelLoadingPollIntervalMs = Math.Max(100, modelLoadingPollIntervalMs);
        }

        public bool IsConnected =>
            sharedClient != null &&
            sharedClient.Connected &&
            sharedStream != null;

        public async Task ConnectAsync(string host, int port, CancellationToken token)
        {
            bool lockTaken = false;
            try
            {
                await writeLock.WaitAsync(token).ConfigureAwait(false);
                lockTaken = true;
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
            }
            finally
            {
                if (lockTaken)
                {
                    writeLock.Release();
                }
            }
        }

        internal async Task<BridgeProtocolResponse> GenerateAsync(
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

            var payload = new JObject
            {
                ["cmd"] = "generate",
                ["task_id"] = taskId,
                ["id"] = taskId,
                ["prompt"] = request.prompt ?? string.Empty,
                ["duration"] = request.duration,
                ["output_format"] = "flatbuf_motion_v1",
                ["diffusion_steps"] = request.steps,
                ["constraints_json"] = request.constraints_json ?? string.Empty
            };
            payload["seed"] = request.seed.HasValue ? request.seed.Value : null;
            payload["loop_hint"] = request.loop_hint;
            payload["segment_index"] = request.segment_index;
            payload["transition_duration"] = request.transition_duration;
            payload["model"] = string.IsNullOrWhiteSpace(request.model) ? null : request.model;
            payload["highvram"] = request.highvram;
            payload["force_cpu"] = request.force_cpu;
            payload["models_root"] = request.models_root ?? string.Empty;
            payload["force_hf_download"] = request.force_hf_download;
            payload["owner_pid"] = request.owner_pid;

            UnityEngine.Debug.Log($"[KimodoBridge] Generate JSON: {payload.ToString(Formatting.None)}");

            var pending = new PendingGenerateRequest(taskId, progress, modelLoadingTimeoutMs);
            RegisterPendingRequest(pending);

            bool lockTaken = false;
            try
            {
                await writeLock.WaitAsync(token).ConfigureAwait(false);
                lockTaken = true;
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                progress?.Invoke(
                    $"Bridge generate request sent: duration={request.duration:F3}s, steps={request.steps}, seed={(request.seed.HasValue ? request.seed.Value.ToString() : "null")}.");
                await WriteJsonLineAsync(sharedStream, payload, token).ConfigureAwait(false);
            }
            catch
            {
                RemovePendingRequest(taskId, pending);
                FailPendingRequest(pending, new IOException($"Bridge generate send failed for task '{taskId}'."));
                CloseSharedConnectionSync();
                throw;
            }
            finally
            {
                if (lockTaken)
                {
                    writeLock.Release();
                }
            }

            return await pending.Completion.Task.ConfigureAwait(false);
        }

        public async Task<bool> TrySendQuitAsync(string host, int port, CancellationToken token)
        {
            bool lockTaken = false;
            try
            {
                await writeLock.WaitAsync(token).ConfigureAwait(false);
                lockTaken = true;
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await WriteJsonLineAsync(sharedStream, new JObject { ["cmd"] = "quit" }, token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                CloseSharedConnectionSync();
                return false;
            }
            finally
            {
                if (lockTaken)
                {
                    writeLock.Release();
                }
            }
        }

        public async Task<bool> TryCancelGenerateAsync(string host, int port, string taskId, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            bool lockTaken = false;
            try
            {
                await writeLock.WaitAsync(token).ConfigureAwait(false);
                lockTaken = true;
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await WriteJsonLineAsync(
                    sharedStream,
                    new JObject
                    {
                        ["cmd"] = "cancel",
                        ["task_id"] = taskId,
                        ["id"] = taskId
                    },
                    token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                CloseSharedConnectionSync();
                return false;
            }
            finally
            {
                if (lockTaken)
                {
                    writeLock.Release();
                }
            }
        }

        public Task<JObject> SendAsync(string host, int port, JObject request, CancellationToken token)
        {
            throw new NotSupportedException("Generic shared bridge requests are not supported by this client.");
        }

        public async Task DetachAsync()
        {
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CloseSharedConnectionSync();
            }
            finally
            {
                writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (!TryBeginDispose())
            {
                return;
            }

            try
            {
                CloseSharedConnectionSync();
            }
            catch
            {
                // ignore dispose errors
            }
            finally
            {
                writeLock.Dispose();
            }
        }

        public async Task DisposeAsync(int timeoutMs = 300)
        {
            if (!TryBeginDispose())
            {
                return;
            }

            try
            {
                Task waitTask = writeLock.WaitAsync();
                Task completed = await Task.WhenAny(waitTask, Task.Delay(Math.Max(10, timeoutMs))).ConfigureAwait(false);
                if (completed == waitTask)
                {
                    try
                    {
                        await waitTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        try { writeLock.Release(); } catch { }
                    }
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try
                {
                    CloseSharedConnectionSync();
                }
                catch
                {
                    // ignore
                }

                writeLock.Dispose();
            }
        }

        private void RegisterPendingRequest(PendingGenerateRequest pending)
        {
            lock (pendingGate)
            {
                if (pendingRequests.ContainsKey(pending.TaskId))
                {
                    throw new InvalidOperationException($"Bridge task id is already pending: {pending.TaskId}");
                }

                pendingRequests[pending.TaskId] = pending;
            }
        }

        private void RemovePendingRequest(string taskId, PendingGenerateRequest pending)
        {
            lock (pendingGate)
            {
                if (pendingRequests.TryGetValue(taskId, out PendingGenerateRequest existing) && ReferenceEquals(existing, pending))
                {
                    pendingRequests.Remove(taskId);
                }
            }
        }

        private PendingGenerateRequest GetPendingRequest(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return null;
            }

            lock (pendingGate)
            {
                pendingRequests.TryGetValue(taskId, out PendingGenerateRequest pending);
                return pending;
            }
        }

        private void FailPendingRequest(PendingGenerateRequest pending, Exception exception)
        {
            if (pending == null || exception == null)
            {
                return;
            }

            RemovePendingRequest(pending.TaskId, pending);
            pending.TrySetException(exception);
        }

        private void FailAllPendingRequests(Exception exception)
        {
            PendingGenerateRequest[] pending;
            lock (pendingGate)
            {
                if (pendingRequests.Count == 0)
                {
                    return;
                }

                pending = new PendingGenerateRequest[pendingRequests.Count];
                pendingRequests.Values.CopyTo(pending, 0);
                pendingRequests.Clear();
            }

            for (int i = 0; i < pending.Length; i++)
            {
                pending[i].TrySetException(exception);
            }
        }

        private async Task EnsureSharedConnectionAsync(string host, int port, CancellationToken token)
        {
            if (sharedClient != null &&
                sharedClient.Connected &&
                sharedStream != null &&
                string.Equals(sharedHost, host, StringComparison.OrdinalIgnoreCase) &&
                sharedPort == port)
            {
                return;
            }

            CloseSharedConnectionSync();
            var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(connectTimeoutMs);
            Task connectTask = client.ConnectAsync(host, port);
            Task timeoutTask = Task.Delay(Timeout.Infinite, connectCts.Token);
            Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            if (completed != connectTask)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"Bridge connect timeout: {host}:{port}");
            }

            await connectTask.ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            client.ReceiveTimeout = ioTimeoutMs;
            client.SendTimeout = ioTimeoutMs;

            sharedClient = client;
            sharedStream = stream;
            sharedHost = host;
            sharedPort = port;

            var newReaderCts = new CancellationTokenSource();
            readerCts = newReaderCts;
            readerTask = Task.Run(() => ReaderLoopAsync(stream, newReaderCts.Token));
        }

        private async Task ReaderLoopAsync(NetworkStream stream, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    BridgeProtocolResponse response = await ReadResponseAsync(stream, token).ConfigureAwait(false);
                    DispatchResponse(response);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception exception)
            {
                if (!disposed)
                {
                    FailAllPendingRequests(exception);
                    CloseSharedConnectionSync();
                }
            }
        }

        private void DispatchResponse(BridgeProtocolResponse response)
        {
            JObject header = response?.Header;
            string taskId = response?.TaskId ?? ExtractTaskId(header);
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return;
            }

            PendingGenerateRequest pending = GetPendingRequest(taskId);
            if (pending == null)
            {
                return;
            }

            string status = header?.Value<string>("status") ?? string.Empty;
            string message = header?.Value<string>("message") ?? string.Empty;
            string outputFormat = header?.Value<string>("output_format") ?? string.Empty;

            if (string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "initializing", StringComparison.OrdinalIgnoreCase))
            {
                if ((DateTime.UtcNow - pending.CreatedAtUtc).TotalMilliseconds > pending.ModelLoadingTimeoutMs)
                {
                    FailPendingRequest(
                        pending,
                        new TimeoutException($"Bridge model loading timeout (>{pending.ModelLoadingTimeoutMs}ms)."));
                    return;
                }

                SafeReportProgress(
                    pending,
                    string.IsNullOrWhiteSpace(message)
                        ? "Bridge is still loading model assets..."
                        : $"Bridge is still loading model assets... {message}");
                return;
            }

            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "progress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase))
            {
                SafeReportProgress(
                    pending,
                    string.IsNullOrWhiteSpace(message)
                        ? $"Bridge generate response status={status}, format={outputFormat}"
                        : message);
                return;
            }

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                string errorMessage = header?.Value<string>("message") ?? "Bridge generation failed.";
                string traceback = header?.Value<string>("traceback");
                if (!string.IsNullOrWhiteSpace(traceback))
                {
                    errorMessage = errorMessage + "\n" + traceback;
                }

                FailPendingRequest(pending, new Exception(errorMessage));
                return;
            }

            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                RemovePendingRequest(taskId, pending);
                pending.TrySetCanceled(message);
                return;
            }

            if (string.Equals(status, "busy", StringComparison.OrdinalIgnoreCase))
            {
                FailPendingRequest(pending, new Exception(string.IsNullOrWhiteSpace(message) ? "Bridge is busy." : message));
                return;
            }

            RemovePendingRequest(taskId, pending);
            pending.TrySetResult(response);
        }

        private static void SafeReportProgress(PendingGenerateRequest pending, string message)
        {
            if (pending == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                pending.Progress?.Invoke(message);
            }
            catch
            {
                // ignore callback failures
            }
        }

        private async Task WriteJsonLineAsync(NetworkStream stream, JObject request, CancellationToken token)
        {
            string line = request.ToString(Formatting.None) + "\n";
            byte[] bytes = Utf8NoBom.GetBytes(line);
            await WithIoTimeoutAsync(stream.WriteAsync(bytes, 0, bytes.Length, token), token, "Bridge write timeout.").ConfigureAwait(false);
            await WithIoTimeoutAsync(stream.FlushAsync(), token, "Bridge flush timeout.").ConfigureAwait(false);
        }

        private async Task<BridgeProtocolResponse> ReadResponseAsync(NetworkStream stream, CancellationToken token)
        {
            JObject header = await ReadJsonLineAsync(stream, token).ConfigureAwait(false);
            int byteLength = Math.Max(0, header?.Value<int?>("byte_length") ?? 0);
            byte[] binaryPayload = byteLength > 0
                ? await ReadExactBytesAsync(stream, byteLength, token).ConfigureAwait(false)
                : null;
            return new BridgeProtocolResponse
            {
                Header = header,
                BinaryPayload = binaryPayload,
                TaskId = ExtractTaskId(header)
            };
        }

        private static string ExtractTaskId(JObject header)
        {
            if (header == null)
            {
                return string.Empty;
            }

            string taskId = header.Value<string>("task_id");
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                return taskId.Trim();
            }

            string id = header.Value<string>("id");
            return string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
        }

        private async Task<JObject> ReadJsonLineAsync(NetworkStream stream, CancellationToken token)
        {
            using var buffer = new MemoryStream(256);
            byte[] singleByte = new byte[1];
            while (true)
            {
                int read = await WithIoTimeoutAsync(
                    stream.ReadAsync(singleByte, 0, 1, token),
                    token,
                    "Bridge read timeout.").ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new IOException("Bridge connection closed while reading a response line.");
                }

                if (singleByte[0] == (byte)'\n')
                {
                    break;
                }

                buffer.WriteByte(singleByte[0]);
            }

            string responseLine = Utf8NoBom.GetString(buffer.ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new Exception("Empty bridge response.");
            }

            JToken parsed = JToken.Parse(responseLine);
            if (parsed is not JObject obj)
            {
                throw new Exception("Bridge response is not a JSON object.");
            }

            return obj;
        }

        private async Task<byte[]> ReadExactBytesAsync(NetworkStream stream, int byteLength, CancellationToken token)
        {
            if (byteLength < 0)
            {
                throw new InvalidOperationException($"Bridge payload length is invalid: {byteLength}.");
            }

            byte[] buffer = new byte[byteLength];
            int totalRead = 0;
            while (totalRead < byteLength)
            {
                int read = await WithIoTimeoutAsync(
                    stream.ReadAsync(buffer, totalRead, byteLength - totalRead, token),
                    token,
                    "Bridge binary read timeout.").ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new IOException(
                        $"Bridge connection closed while reading binary payload. Received {totalRead} of {byteLength} bytes.");
                }

                totalRead += read;
            }

            return buffer;
        }

        private async Task WithIoTimeoutAsync(Task task, CancellationToken token, string timeoutMessage)
        {
            Task timeoutTask = Task.Delay(ioTimeoutMs, token);
            Task completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completed != task)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException(timeoutMessage);
            }

            await task.ConfigureAwait(false);
        }

        private async Task<T> WithIoTimeoutAsync<T>(Task<T> task, CancellationToken token, string timeoutMessage)
        {
            Task timeoutTask = Task.Delay(ioTimeoutMs, token);
            Task completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completed != task)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException(timeoutMessage);
            }

            return await task.ConfigureAwait(false);
        }

        private void CloseSharedConnectionSync()
        {
            if (!disposed)
            {
                FailAllPendingRequests(new IOException("Bridge connection closed."));
            }

            CancellationTokenSource currentReaderCts = readerCts;
            readerCts = null;

            if (currentReaderCts != null)
            {
                try { currentReaderCts.Cancel(); } catch { }
                try { currentReaderCts.Dispose(); } catch { }
            }

            try { sharedStream?.Dispose(); } catch { }
            try { sharedClient?.Dispose(); } catch { }
            sharedStream = null;
            sharedClient = null;
            sharedHost = string.Empty;
            sharedPort = -1;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BridgeProtocolClient));
            }
        }

        private bool TryBeginDispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            {
                return false;
            }

            lock (disposeGate)
            {
                disposed = true;
            }

            return true;
        }
    }
}
