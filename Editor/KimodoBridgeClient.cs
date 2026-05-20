using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal sealed class KimodoBridgeClient : IDisposable
    {
        private Process process;
        private int processId = -1;
        private TcpClient sharedClient;
        private StreamReader sharedReader;
        private StreamWriter sharedWriter;
        private string sharedHost = string.Empty;
        private int sharedPort = -1;
        private readonly SemaphoreSlim ioLock = new SemaphoreSlim(1, 1);
        private string currentPortFilePath;
        private string currentHost = "127.0.0.1";
        private int currentPort = -1;
        private string currentBridgeLogPath = string.Empty;
        private CancellationTokenSource logPumpCts;
        private Task logPumpTask;
        private bool disposed;

        public bool IsRunning
        {
            get
            {
                try
                {
                    return process != null && !process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public async Task<string> StartAsync(
            string launcherPath,
            string modelName,
            bool highVram,
            string kimodoRootPath,
            float startupTimeoutSeconds,
            Action<string> progress,
            CancellationToken token)
        {
            if (process != null && !process.HasExited)
            {
                return "already_running";
            }

            if (string.IsNullOrWhiteSpace(kimodoRootPath))
            {
                throw new Exception("kimodoRootPath is empty.");
            }

            string root = Path.GetFullPath(kimodoRootPath.Trim());
            string portFile = Path.Combine(root, "serverport");
            currentPortFilePath = portFile;

            if (File.Exists(portFile))
            {
                if (TryReadPortFile(portFile, out string host, out int port) && await PingTcpAsync(host, port, token, acceptLoading: true))
                {
                    currentHost = host;
                    currentPort = port;
                    TryStartBridgeLogPump(Path.Combine(root, "log", "run_server.log"), progress);
                    return $"Ready - {modelName} on {host}:{port}";
                }

                TryDeletePortFile(portFile);
            }

            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new Exception("Bridge launcher path is empty.");
            }

            string resolvedLauncher = Path.GetFullPath(launcherPath.Trim());
            if (!File.Exists(resolvedLauncher))
            {
                throw new FileNotFoundException($"Bridge launcher not found: {resolvedLauncher}");
            }

            string bridgeLogPath = BuildBridgeLogPath(root);
            ProcessStartInfo psi = BuildLauncherStartInfo(resolvedLauncher, modelName, highVram, root, bridgeLogPath);

            Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
            {
                throw new Exception("Failed to start bridge process.");
            }

            process = proc;
            processId = proc.Id;
            TryStartBridgeLogPump(bridgeLogPath, progress);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30f, startupTimeoutSeconds)));

                progress?.Invoke("Starting bridge...");
                while (true)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    if (TryReadPortFile(portFile, out string host, out int port))
                    {
                        if (await PingTcpAsync(host, port, timeoutCts.Token, acceptLoading: true))
                        {
                            currentHost = host;
                            currentPort = port;
                            return $"Ready - {modelName} on {host}:{port}";
                        }
                    }

                    if (proc.HasExited)
                    {
                        // Compatibility: some launchers may spawn a detached bridge process and exit quickly.
                        // Re-check port file one more time before treating this as startup failure.
                        if (TryReadPortFile(portFile, out host, out port) && await PingTcpAsync(host, port, timeoutCts.Token, acceptLoading: true))
                        {
                            currentHost = host;
                            currentPort = port;
                            return $"Ready - {modelName} on {host}:{port}";
                        }
                        throw new Exception($"Bridge exited with code {proc.ExitCode}.");
                    }
                    await Task.Delay(250, timeoutCts.Token);
                }
            }
            catch
            {
                await StopAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task<string> GenerateAsync(
            string prompt,
            float durationSeconds,
            int? seed,
            int diffusionSteps,
            string constraintsJson,
            Action<string> progress,
            CancellationToken token)
        {
            await EnsureHealthyOrThrowAsync(token);

            var req = new JObject
            {
                ["cmd"] = "generate",
                ["prompt"] = prompt ?? string.Empty,
                ["duration"] = durationSeconds,
                ["output_format"] = "json_compact",
                ["diffusion_steps"] = diffusionSteps,
                ["constraints_json"] = constraintsJson ?? string.Empty
            };
            req["seed"] = seed.HasValue ? seed.Value : null;

            var waitSw = Stopwatch.StartNew();
            const double maxLoadingWaitSeconds = 600.0;
            while (true)
            {
                token.ThrowIfCancellationRequested();

                JObject msg = await SendRequestAsync(req, token);
                string status = msg?.Value<string>("status") ?? string.Empty;
                if (string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase))
                {
                    string loadingMessage = msg.Value<string>("message") ?? "Model is loading.";
                    progress?.Invoke($"Bridge loading model... {loadingMessage}");
                    if (waitSw.Elapsed.TotalSeconds > maxLoadingWaitSeconds)
                    {
                        throw new Exception($"Bridge model still loading after {maxLoadingWaitSeconds:0}s.");
                    }
                    await Task.Delay(1000, token);
                    continue;
                }
                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    string message = msg.Value<string>("message") ?? "Bridge generation failed.";
                    string traceback = msg.Value<string>("traceback");
                    if (!string.IsNullOrWhiteSpace(traceback))
                    {
                        throw new Exception($"{message}\n{traceback}");
                    }
                    throw new Exception(message);
                }

                if (!string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Unexpected bridge response status: {status}");
                }

                string motionJson = msg.Value<string>("motion_json_compact");
                if (string.IsNullOrWhiteSpace(motionJson))
                {
                    throw new Exception("Bridge completed without motion_json_compact.");
                }

                progress?.Invoke("Bridge generation complete.");
                return motionJson;
            }
        }

        public async Task StopAsync(CancellationToken token)
        {
            Process proc = process;
            string portFilePath = currentPortFilePath;
            int pid = processId;
            process = null;
            processId = -1;
            currentHost = "127.0.0.1";
            currentPort = -1;
            StopBridgeLogPump();
            await CloseSharedConnectionAsync();

            if (!string.IsNullOrWhiteSpace(portFilePath) && TryReadPortFile(portFilePath, out string host, out int port))
            {
                try
                {
                    await SendRequestAsync(new JObject { ["cmd"] = "quit" }, token, host, port);
                }
                catch
                {
                    // ignore
                }
            }

            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited && !proc.WaitForExit(1500))
                    {
                        KillProcessTreeByPid(pid > 0 ? pid : proc.Id);
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    proc.Dispose();
                }
            }

            if (!string.IsNullOrWhiteSpace(portFilePath))
            {
                TryDeletePortFile(portFilePath);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            // Never block Unity's main thread in Dispose.
            // StopAsync performs async I/O and can hang when networking/process
            // teardown is slow. Here we do a best-effort immediate cleanup.
            Process proc = process;
            string portFilePath = currentPortFilePath;
            int pid = processId;
            process = null;
            processId = -1;
            currentHost = "127.0.0.1";
            currentPort = -1;
            StopBridgeLogPump();
            CloseSharedConnectionSync();

            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        KillProcessTreeByPid(pid > 0 ? pid : proc.Id);
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }

            if (!string.IsNullOrWhiteSpace(portFilePath))
            {
                TryDeletePortFile(portFilePath);
            }
        }

        public async Task<bool> PingAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(currentPortFilePath))
            {
                return false;
            }

            if (!TryReadPortFile(currentPortFilePath, out string host, out int port))
            {
                return false;
            }

            currentHost = host;
            currentPort = port;
            return await PingTcpAsync(host, port, token, acceptLoading: true);
        }

        public async Task DetachAsync()
        {
            process = null;
            processId = -1;
            currentHost = "127.0.0.1";
            currentPort = -1;
            StopBridgeLogPump();
            await CloseSharedConnectionAsync();
            await Task.CompletedTask;
        }

        public async Task KillServerTreeAsync(CancellationToken token)
        {
            string portFilePath = currentPortFilePath;
            int pid = processId;
            Process proc = process;

            process = null;
            processId = -1;
            currentHost = "127.0.0.1";
            currentPort = -1;
            StopBridgeLogPump();
            await CloseSharedConnectionAsync();

            try
            {
                if (pid <= 0 && proc != null)
                {
                    try { pid = proc.Id; } catch { pid = -1; }
                }
                if (pid > 0)
                {
                    KillProcessTreeByPid(pid);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                if (proc != null)
                {
                    try { proc.Dispose(); } catch { }
                }
                if (!string.IsNullOrWhiteSpace(portFilePath))
                {
                    TryDeletePortFile(portFilePath);
                }
            }

            await Task.CompletedTask;
        }

        private async Task EnsureHealthyOrThrowAsync(CancellationToken token)
        {
            if (!await PingAsync(token))
            {
                throw new Exception("Bridge port is unreachable.");
            }
        }

        private async Task<bool> PingTcpAsync(string host, int port, CancellationToken token, bool acceptLoading = false)
        {
            try
            {
                var req = new JObject { ["cmd"] = "ping" };
                JObject resp = await SendRequestAsync(req, token, host, port);
                string status = resp?.Value<string>("status") ?? string.Empty;
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

        private async Task<JObject> SendRequestAsync(JObject req, CancellationToken token)
        {
            return await SendRequestAsync(req, token, currentHost, currentPort);
        }

        private async Task<JObject> SendRequestAsync(JObject req, CancellationToken token, string host, int port)
        {
            if (port <= 0)
            {
                throw new Exception("Bridge port is invalid.");
            }

            await ioLock.WaitAsync(token);
            try
            {
                await EnsureSharedConnectionAsync(host, port, token);

                string line = req.ToString(Formatting.None);
                await sharedWriter.WriteLineAsync(line);
                string resp = await sharedReader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(resp))
                {
                    throw new Exception("Empty bridge response.");
                }

                JToken parsed = JToken.Parse(resp);
                if (parsed is not JObject obj)
                {
                    throw new Exception("Bridge response is not an object.");
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

        private static bool TryReadPortFile(string portFilePath, out string host, out int port)
        {
            host = "127.0.0.1";
            port = -1;
            try
            {
                if (!File.Exists(portFilePath))
                {
                    return false;
                }

                string text = File.ReadAllText(portFilePath).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                if (text.Contains(":"))
                {
                    string[] parts = text.Split(':');
                    if (parts.Length != 2)
                    {
                        return false;
                    }
                    host = parts[0].Trim();
                    return int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0;
                }

                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeletePortFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }

        private static ProcessStartInfo BuildLauncherStartInfo(string launcherPath, string modelName, bool highVram, string kimodoRootPath, string bridgeLogPath)
        {
            string ext = Path.GetExtension(launcherPath)?.ToLowerInvariant() ?? string.Empty;
            string modelArg = $" --model \"{(string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim())}\"";
            string vramArg = highVram ? " --highvram" : string.Empty;
            string outputArg = $" --output file --log \"{bridgeLogPath}\"";
            string args = modelArg + vramArg + outputArg;

            if (ext == ".bat" || ext == ".cmd")
            {
                return new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /c \"\"{launcherPath}\"{args}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(launcherPath)
                };
            }

            if (ext == ".sh")
            {
                return new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"\"{launcherPath}\"{args}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(launcherPath)
                };
            }

            throw new Exception($"Unsupported launcher extension: {ext}. Expected .bat/.cmd/.sh");
        }

        private static void KillProcessTreeByPid(int pid)
        {
            if (pid <= 0)
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {pid} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                };
                using var killer = Process.Start(psi);
                killer?.WaitForExit(5000);
            }
            catch
            {
                // ignore
            }
        }

        private async Task EnsureSharedConnectionAsync(string host, int port, CancellationToken token)
        {
            if (sharedClient != null && sharedClient.Connected && string.Equals(sharedHost, host, StringComparison.OrdinalIgnoreCase) && sharedPort == port)
            {
                return;
            }

            CloseSharedConnectionSync();

            var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(3));
            Task connectTask = client.ConnectAsync(host, port);
            Task cancelOrTimeoutTask = Task.Delay(Timeout.Infinite, connectCts.Token);
            Task completed = await Task.WhenAny(connectTask, cancelOrTimeoutTask);
            if (completed != connectTask)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"Bridge connect timeout: {host}:{port}");
            }
            await connectTask;

            NetworkStream ns = client.GetStream();
            sharedWriter = new StreamWriter(ns, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            sharedReader = new StreamReader(ns, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            sharedClient = client;
            sharedHost = host;
            sharedPort = port;
        }

        private async Task CloseSharedConnectionAsync()
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

        private static string BuildBridgeLogPath(string kimodoRootPath)
        {
            string logDir = Path.Combine(kimodoRootPath, "log");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            return Path.Combine(logDir, $"unity_bridge_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
        }

        private void TryStartBridgeLogPump(string logPath, Action<string> progress)
        {
            StopBridgeLogPump();
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            currentBridgeLogPath = Path.GetFullPath(logPath);
            var cts = new CancellationTokenSource();
            logPumpCts = cts;
            logPumpTask = Task.Run(() => PumpBridgeLogAsync(currentBridgeLogPath, progress, cts.Token));
        }

        private void StopBridgeLogPump()
        {
            CancellationTokenSource cts = logPumpCts;
            logPumpCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
            logPumpTask = null;
            currentBridgeLogPath = string.Empty;
        }

        private static async Task PumpBridgeLogAsync(string logPath, Action<string> progress, CancellationToken token)
        {
            try
            {
                DateTime waitUntil = DateTime.UtcNow.AddSeconds(120);
                while (!token.IsCancellationRequested && !File.Exists(logPath))
                {
                    if (DateTime.UtcNow >= waitUntil)
                    {
                        return;
                    }
                    await Task.Delay(200, token);
                }

                if (!File.Exists(logPath))
                {
                    return;
                }

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (!token.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null)
                    {
                        await Task.Delay(120, token);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string msg = $"[Bridge] {line}";
                    progress?.Invoke(msg);
                    EditorApplication.delayCall += () => UnityEngine.Debug.Log(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception e)
            {
                EditorApplication.delayCall += () => UnityEngine.Debug.LogWarning($"[KimodoBridge] log pump stopped: {e.Message}");
            }
        }
    }
}
