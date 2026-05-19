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
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal sealed class KimodoBridgeClient : IDisposable
    {
        private Process process;
        private CancellationTokenSource stderrCts;
        private string currentPortFilePath;
        private string currentHost = "127.0.0.1";
        private int currentPort = -1;
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
                if (TryReadPortFile(portFile, out string host, out int port) && await PingTcpAsync(host, port, token))
                {
                    currentHost = host;
                    currentPort = port;
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

            ProcessStartInfo psi = BuildLauncherStartInfo(resolvedLauncher, modelName, root);

            Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
            {
                throw new Exception("Failed to start bridge process.");
            }

            process = proc;
            stderrCts = new CancellationTokenSource();
            _ = DrainStderrAsync(proc, stderrCts.Token);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Mathf.Max(30f, startupTimeoutSeconds)));

                progress?.Invoke("Starting bridge...");
                while (true)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    if (proc.HasExited)
                    {
                        throw new Exception($"Bridge exited with code {proc.ExitCode}.");
                    }

                    if (TryReadPortFile(portFile, out string host, out int port))
                    {
                        if (await PingTcpAsync(host, port, timeoutCts.Token))
                        {
                            currentHost = host;
                            currentPort = port;
                            return $"Ready - {modelName} on {host}:{port}";
                        }
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

            JObject msg = await SendRequestAsync(req, token);
            string status = msg?.Value<string>("status") ?? string.Empty;
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

        public async Task StopAsync(CancellationToken token)
        {
            Process proc = process;
            CancellationTokenSource cts = stderrCts;
            string portFilePath = currentPortFilePath;
            process = null;
            stderrCts = null;
            currentHost = "127.0.0.1";
            currentPort = -1;

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }

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
                        proc.Kill();
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
            CancellationTokenSource cts = stderrCts;
            string portFilePath = currentPortFilePath;
            process = null;
            stderrCts = null;
            currentHost = "127.0.0.1";
            currentPort = -1;

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }

            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
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
            return await PingTcpAsync(host, port, token);
        }

        private async Task EnsureHealthyOrThrowAsync(CancellationToken token)
        {
            if (!await PingAsync(token))
            {
                throw new Exception("Bridge port is unreachable.");
            }
        }

        private static async Task<bool> PingTcpAsync(string host, int port, CancellationToken token)
        {
            try
            {
                var req = new JObject { ["cmd"] = "ping" };
                JObject resp = await SendRequestAsync(req, token, host, port);
                return string.Equals(resp?.Value<string>("status"), "pong", StringComparison.OrdinalIgnoreCase);
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

        private static async Task<JObject> SendRequestAsync(JObject req, CancellationToken token, string host, int port)
        {
            if (port <= 0)
            {
                throw new Exception("Bridge port is invalid.");
            }

            using var client = new TcpClient();
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

            using NetworkStream ns = client.GetStream();
            using var sw = new StreamWriter(ns, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            using var sr = new StreamReader(ns, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            string line = req.ToString(Formatting.None);
            await sw.WriteLineAsync(line);
            string resp = await sr.ReadLineAsync();
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

        private static async Task DrainStderrAsync(Process proc, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !proc.HasExited)
                {
                    string line = await proc.StandardError.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        UnityEngine.Debug.Log($"[Kimodo Bridge] {line}");
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static ProcessStartInfo BuildLauncherStartInfo(string launcherPath, string modelName, string kimodoRootPath)
        {
            string ext = Path.GetExtension(launcherPath)?.ToLowerInvariant() ?? string.Empty;
            string modelArg = $" --model \"{(string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim())}\"";
            string kimodoRootArg = string.IsNullOrWhiteSpace(kimodoRootPath)
                ? string.Empty
                : $" --kimodo-root \"{Path.GetFullPath(kimodoRootPath.Trim())}\"";
            string args = modelArg + kimodoRootArg;

            if (ext == ".bat" || ext == ".cmd")
            {
                return new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /c \"\"{launcherPath}\"{args}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8,
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
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(launcherPath)
                };
            }

            throw new Exception($"Unsupported launcher extension: {ext}. Expected .bat/.cmd/.sh");
        }
    }
}
