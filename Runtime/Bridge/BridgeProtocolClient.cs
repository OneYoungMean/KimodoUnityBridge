using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
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
    }

    public sealed class BridgeProtocolClient : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly SemaphoreSlim ioLock = new SemaphoreSlim(1, 1);
        private readonly object disposeGate = new object();
        private readonly int connectTimeoutMs;
        private readonly int ioTimeoutMs;
        private readonly int modelLoadingTimeoutMs;
        private readonly int modelLoadingPollIntervalMs;

        private TcpClient sharedClient;
        private NetworkStream sharedStream;
        private string sharedHost = string.Empty;
        private int sharedPort = -1;
        private bool disposed;
        private int disposeStarted;

        public BridgeProtocolClient(
            int connectTimeoutMs = BridgeRuntimeSettings.DefaultConnectTimeoutMs,
            int ioTimeoutMs = BridgeRuntimeSettings.DefaultIoTimeoutMs,
            int modelLoadingTimeoutMs = BridgeRuntimeSettings.DefaultModelLoadingTimeoutMs,
            int modelLoadingPollIntervalMs = BridgeRuntimeSettings.DefaultModelLoadingPollIntervalMs)
        {
            this.connectTimeoutMs = Math.Max(500, connectTimeoutMs);
            this.ioTimeoutMs = Math.Max(1000, ioTimeoutMs);
            this.modelLoadingTimeoutMs = Math.Max(10000, modelLoadingTimeoutMs);
            this.modelLoadingPollIntervalMs = Math.Max(100, modelLoadingPollIntervalMs);
        }

        public async Task<bool> PingAsync(string host, int port, CancellationToken token, bool acceptLoading)
        {
            try
            {
                JObject response = await SendAsync(host, port, new JObject { ["cmd"] = "ping" }, token).ConfigureAwait(false);
                string status = response?.Value<string>("status") ?? string.Empty;
                if (string.Equals(status, "pong", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (acceptLoading && string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
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

            var payload = new JObject
            {
                ["cmd"] = "generate",
                ["prompt"] = request.prompt ?? string.Empty,
                ["duration"] = request.duration,
                ["output_format"] = "flatbuf_motion_v1",
                ["diffusion_steps"] = request.steps,
                ["constraints_json"] = request.constraints_json ?? string.Empty
            };
            payload["seed"] = request.seed.HasValue ? request.seed.Value : null;
            payload["boundary_pose_json"] = request.boundary_pose_json ?? string.Empty;
            payload["loop_hint"] = request.loop_hint;
            payload["segment_index"] = request.segment_index;
            payload["transition_duration"] = request.transition_duration;

            await WaitUntilModelReadyAsync(host, port, progress, token).ConfigureAwait(false);
            progress?.Invoke(
                $"Bridge generate request sent: duration={request.duration:F3}s, steps={request.steps}, seed={(request.seed.HasValue ? request.seed.Value.ToString() : "null")}.");
            BridgeProtocolResponse response = await SendRequestAsync(host, port, payload, token).ConfigureAwait(false);
            JObject header = response?.Header;
            string status = header?.Value<string>("status") ?? string.Empty;
            string message = header?.Value<string>("message") ?? string.Empty;
            string outputFormat = header?.Value<string>("output_format") ?? string.Empty;
            progress?.Invoke(
                $"Bridge generate response status={status}, format={outputFormat}{(string.IsNullOrWhiteSpace(message) ? string.Empty : $", message={message}")}");

            if (string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Bridge returned loading after ready check.");
            }

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                string errorMessage = header?.Value<string>("message") ?? "Bridge generation failed.";
                string traceback = header?.Value<string>("traceback");
                if (!string.IsNullOrWhiteSpace(traceback))
                {
                    throw new Exception($"{errorMessage}\n{traceback}");
                }

                throw new Exception(errorMessage);
            }

            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException(
                    string.IsNullOrWhiteSpace(message) ? "Bridge generation cancelled." : message);
            }

            if (string.Equals(status, "busy", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(string.IsNullOrWhiteSpace(message) ? "Bridge is busy." : message);
            }

            return response;
        }

        public async Task<bool> TrySendQuitAsync(string host, int port, CancellationToken token)
        {
            try
            {
                await SendWithoutReplyAsync(host, port, new JObject { ["cmd"] = "quit" }, token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                CloseSharedConnectionSync();
                return false;
            }
        }

        public async Task<bool> TryCancelGenerateAsync(string host, int port, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0)
            {
                return false;
            }

            try
            {
                using var client = new TcpClient();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(connectTimeoutMs);
                Task connectTask = client.ConnectAsync(host, port);
                Task timeoutTask = Task.Delay(Timeout.Infinite, connectCts.Token);
                Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    token.ThrowIfCancellationRequested();
                    return false;
                }

                await connectTask.ConfigureAwait(false);
                client.ReceiveTimeout = ioTimeoutMs;
                client.SendTimeout = ioTimeoutMs;

                using NetworkStream stream = client.GetStream();
                await WriteJsonLineAsync(stream, new JObject { ["cmd"] = "cancel" }, token).ConfigureAwait(false);
                JObject response = await ReadJsonLineAsync(stream, token).ConfigureAwait(false);
                string status = response?.Value<string>("status") ?? string.Empty;
                return string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public async Task<JObject> SendAsync(string host, int port, JObject request, CancellationToken token)
        {
            BridgeProtocolResponse response = await SendRequestAsync(host, port, request, token).ConfigureAwait(false);
            return response?.Header;
        }

        private async Task<BridgeProtocolResponse> SendRequestAsync(string host, int port, JObject request, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new InvalidOperationException("Bridge host is empty.");
            }

            if (port <= 0)
            {
                throw new InvalidOperationException("Bridge port is invalid.");
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            bool lockTaken = false;
            try
            {
                await ioLock.WaitAsync(token).ConfigureAwait(false);
                lockTaken = true;
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await WriteJsonLineAsync(sharedStream, request, token).ConfigureAwait(false);
                return await ReadResponseAsync(sharedStream, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (lockTaken)
                {
                    CloseSharedConnectionSync();
                }
                throw;
            }
            catch
            {
                if (lockTaken)
                {
                    CloseSharedConnectionSync();
                }
                throw;
            }
            finally
            {
                if (lockTaken)
                {
                    ioLock.Release();
                }
            }
        }

        private async Task SendWithoutReplyAsync(string host, int port, JObject request, CancellationToken token)
        {
            bool lockTaken = false;
            try
            {
                await ioLock.WaitAsync(token).ConfigureAwait(false);
                lockTaken = true;
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await WriteJsonLineAsync(sharedStream, request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (lockTaken)
                {
                    CloseSharedConnectionSync();
                }
                throw;
            }
            catch
            {
                if (lockTaken)
                {
                    CloseSharedConnectionSync();
                }
                throw;
            }
            finally
            {
                if (lockTaken)
                {
                    CloseSharedConnectionSync();
                    ioLock.Release();
                }
            }
        }

        public async Task DetachAsync()
        {
            await ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CloseSharedConnectionSync();
            }
            finally
            {
                ioLock.Release();
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
                ioLock.Dispose();
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
                Task waitTask = ioLock.WaitAsync();
                Task completed = await Task.WhenAny(waitTask, Task.Delay(Math.Max(10, timeoutMs))).ConfigureAwait(false);
                if (completed == waitTask)
                {
                    try
                    {
                        await waitTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        try { ioLock.Release(); } catch { }
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

                ioLock.Dispose();
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
        }

        private async Task WaitUntilModelReadyAsync(string host, int port, Action<string> progress, CancellationToken token)
        {
            DateTime waitStart = DateTime.UtcNow;
            string lastLoadingMessage = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                JObject response = await SendAsync(host, port, new JObject { ["cmd"] = "ping" }, token).ConfigureAwait(false);
                string status = response?.Value<string>("status") ?? string.Empty;
                string message = response?.Value<string>("message") ?? string.Empty;

                if (string.Equals(status, "pong", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    if (lastLoadingMessage != null)
                    {
                        progress?.Invoke("Bridge model ready.");
                    }

                    return;
                }

                if (string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "initializing", StringComparison.OrdinalIgnoreCase))
                {
                    string loadingMessage = string.IsNullOrWhiteSpace(message) ? "Model is loading." : message;
                    if (!string.Equals(lastLoadingMessage, loadingMessage, StringComparison.Ordinal))
                    {
                        progress?.Invoke($"Bridge waiting for model ready... {loadingMessage}");
                        lastLoadingMessage = loadingMessage;
                    }

                    if ((DateTime.UtcNow - waitStart).TotalMilliseconds > modelLoadingTimeoutMs)
                    {
                        throw new TimeoutException($"Bridge model loading timeout (>{modelLoadingTimeoutMs}ms).");
                    }

                    await Task.Delay(modelLoadingPollIntervalMs, token).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    string errorMessage = response.Value<string>("message") ?? "Bridge readiness check failed.";
                    string traceback = response.Value<string>("traceback");
                    if (!string.IsNullOrWhiteSpace(traceback))
                    {
                        throw new Exception($"{errorMessage}\n{traceback}");
                    }

                    throw new Exception(errorMessage);
                }

                throw new Exception(
                    $"Unexpected bridge ping status while waiting for ready: {status}" +
                    $"{(string.IsNullOrWhiteSpace(message) ? string.Empty : $". message={message}")}");
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
                BinaryPayload = binaryPayload
            };
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
