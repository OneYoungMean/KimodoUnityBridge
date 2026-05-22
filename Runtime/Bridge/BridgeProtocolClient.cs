using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KimodoUnityMotionTools.Bridge
{
    public sealed class BridgeProtocolClient : IDisposable
    {
        private readonly SemaphoreSlim ioLock = new SemaphoreSlim(1, 1);
        private readonly int connectTimeoutMs;
        private readonly int ioTimeoutMs;

        private TcpClient sharedClient;
        private StreamReader sharedReader;
        private StreamWriter sharedWriter;
        private string sharedHost = string.Empty;
        private int sharedPort = -1;
        private bool disposed;

        public BridgeProtocolClient(int connectTimeoutMs = 3000, int ioTimeoutMs = 120000)
        {
            this.connectTimeoutMs = Math.Max(500, connectTimeoutMs);
            this.ioTimeoutMs = Math.Max(1000, ioTimeoutMs);
        }

        public async Task<bool> PingAsync(string host, int port, CancellationToken token, bool acceptLoading)
        {
            try
            {
                JObject response = await SendAsync(host, port, new JObject { ["cmd"] = "ping" }, token);
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
            string prompt,
            float durationSeconds,
            int? seed,
            int diffusionSteps,
            string constraintsJson,
            Action<string> progress,
            CancellationToken token)
        {
            var request = new JObject
            {
                ["cmd"] = "generate",
                ["prompt"] = prompt ?? string.Empty,
                ["duration"] = durationSeconds,
                ["output_format"] = "json_compact",
                ["diffusion_steps"] = diffusionSteps,
                ["constraints_json"] = constraintsJson ?? string.Empty
            };
            request["seed"] = seed.HasValue ? seed.Value : null;
            progress?.Invoke(
                $"Bridge generate request sent: duration={durationSeconds:F3}s, steps={diffusionSteps}, seed={(seed.HasValue ? seed.Value.ToString() : "null")}.");

            DateTime waitStart = DateTime.UtcNow;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                JObject response = await SendAsync(host, port, request, token);
                string status = response?.Value<string>("status") ?? string.Empty;
                string message = response?.Value<string>("message") ?? string.Empty;
                progress?.Invoke($"Bridge generate response status={status}{(string.IsNullOrWhiteSpace(message) ? string.Empty : $", message={message}")}");

                if (string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase))
                {
                    string msg = response.Value<string>("message") ?? "Model is loading.";
                    progress?.Invoke($"Bridge loading model... {msg}");
                    if ((DateTime.UtcNow - waitStart).TotalSeconds > 600d)
                    {
                        throw new TimeoutException("Bridge model loading timeout (>600s).");
                    }

                    await Task.Delay(1000, token);
                    continue;
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
        }

        public async Task<bool> TrySendQuitAsync(string host, int port, CancellationToken token)
        {
            try
            {
                _ = await SendAsync(host, port, new JObject { ["cmd"] = "quit" }, token);
                return true;
            }
            catch
            {
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

            await ioLock.WaitAsync(token);
            try
            {
                ThrowIfDisposed();
                await EnsureSharedConnectionAsync(host, port, token);
                string line = request.ToString(Formatting.None);
                await sharedWriter.WriteLineAsync(line);

                Task<string> readTask = sharedReader.ReadLineAsync();
                Task timeoutTask = Task.Delay(ioTimeoutMs, token);
                Task completed = await Task.WhenAny(readTask, timeoutTask);
                if (completed != readTask)
                {
                    throw new TimeoutException("Bridge read timeout.");
                }

                string responseLine = await readTask;
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
            catch
            {
                CloseSharedConnectionSync();
                throw;
            }
            finally
            {
                ioLock.Release();
            }
        }

        public async Task DetachAsync()
        {
            await ioLock.WaitAsync();
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
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                ioLock.Wait();
                CloseSharedConnectionSync();
            }
            catch
            {
                // ignore dispose errors
            }
            finally
            {
                try { ioLock.Release(); } catch { }
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
            Task completed = await Task.WhenAny(connectTask, timeoutTask);
            if (completed != connectTask)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"Bridge connect timeout: {host}:{port}");
            }

            await connectTask;
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
    }
}
