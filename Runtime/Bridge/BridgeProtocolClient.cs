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
    public sealed class BridgeProtocolClient : IDisposable
    {
        private readonly SemaphoreSlim ioLock = new SemaphoreSlim(1, 1);
        private readonly object disposeGate = new object();
        private readonly int connectTimeoutMs;
        private readonly int ioTimeoutMs;
        private readonly int modelLoadingTimeoutMs;
        private readonly int modelLoadingPollIntervalMs;

        private TcpClient sharedClient;
        private StreamReader sharedReader;
        private StreamWriter sharedWriter;
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

        public async Task<JObject> GenerateAsync(
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
                ["output_format"] = "json_compact",
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
            JObject response = await SendAsync(host, port, payload, token).ConfigureAwait(false);
            string status = response?.Value<string>("status") ?? string.Empty;
            string message = response?.Value<string>("message") ?? string.Empty;
            progress?.Invoke($"Bridge generate response status={status}{(string.IsNullOrWhiteSpace(message) ? string.Empty : $", message={message}")}");

            if (string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Bridge returned loading after ready check.");
            }

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                string errorMessage = response.Value<string>("message") ?? "Bridge generation failed.";
                string traceback = response.Value<string>("traceback");
                if (!string.IsNullOrWhiteSpace(traceback))
                {
                    throw new Exception($"{errorMessage}\n{traceback}");
                }

                throw new Exception(errorMessage);
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

        public async Task<JObject> SendAsync(string host, int port, JObject request, CancellationToken token)
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
                string line = request.ToString(Formatting.None);
                await sharedWriter.WriteLineAsync(line).ConfigureAwait(false);

                Task<string> readTask = sharedReader.ReadLineAsync();
                Task timeoutTask = Task.Delay(ioTimeoutMs, token);
                Task completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                if (completed != readTask)
                {
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Bridge read timeout.");
                }

                token.ThrowIfCancellationRequested();
                string responseLine = await readTask.ConfigureAwait(false);
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
                string line = request.ToString(Formatting.None);
                await sharedWriter.WriteLineAsync(line).ConfigureAwait(false);
                await sharedWriter.FlushAsync().ConfigureAwait(false);
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
            NetworkStream ns = client.GetStream();
            ns.ReadTimeout = ioTimeoutMs;
            ns.WriteTimeout = ioTimeoutMs;
            client.ReceiveTimeout = ioTimeoutMs;
            client.SendTimeout = ioTimeoutMs;

            sharedWriter = new StreamWriter(ns, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            sharedReader = new StreamReader(ns, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            sharedClient = client;
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

        private void CloseSharedConnectionSync()
        {
            try { sharedReader?.Dispose(); } catch { }
            try { sharedWriter?.Dispose(); } catch { }
            try { sharedClient?.Dispose(); } catch { }
            sharedReader = null;
            sharedWriter = null;
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
