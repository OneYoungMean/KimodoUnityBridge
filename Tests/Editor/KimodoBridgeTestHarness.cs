using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.ProjectEditor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KimodoUnityMotionTools.Tests
{
    internal sealed class KimodoRuntimeScope : IDisposable
    {
        private readonly List<string> timeline = new List<string>();
        private bool disposed;

        internal KimodoRuntimeScope(string testName, string workingRoot, string runtimeRoot)
        {
            TestName = testName;
            WorkingRoot = workingRoot;
            RuntimeRoot = runtimeRoot;
            DiagnosticsPath = Path.Combine(workingRoot, "diagnostics.log");
            Log($"Created scope. runtimeRoot={RuntimeRoot}");
        }

        internal string TestName { get; }
        internal string WorkingRoot { get; }
        internal string RuntimeRoot { get; }
        internal string DiagnosticsPath { get; }

        internal void Log(string message)
        {
            string line = $"[{DateTime.UtcNow:O}] {message}";
            timeline.Add(line);
            TestContext.WriteLine(line);
        }

        internal void FlushDiagnostics()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticsPath) ?? WorkingRoot);
                File.WriteAllLines(DiagnosticsPath, timeline);
                TestContext.AddTestAttachment(DiagnosticsPath, "Kimodo runtime diagnostics");
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            FlushDiagnostics();
        }
    }

    internal static class KimodoBridgeTestHarness
    {
        private const string DefaultModel = "Kimodo-SOMA-RP-v1";

        internal static KimodoRuntimeScope CreateRuntimeScope(string scenarioName)
        {
            string packageRoot = ResolvePackageRoot();
            string templateRoot = Path.Combine(packageRoot, "NvlabKimodoQuickServer~");
            if (!Directory.Exists(templateRoot))
            {
                throw new DirectoryNotFoundException($"Runtime template not found: {templateRoot}");
            }

            string safeScenario = MakeSafePathFragment(scenarioName);
            string workingRoot = Path.Combine(Path.GetTempPath(), "KimodoUnityBridgeTests", safeScenario + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
            string runtimeRoot = Path.Combine(workingRoot, "NvlabKimodoQuickServer");
            Directory.CreateDirectory(workingRoot);
            CopyDirectoryRecursive(templateRoot, runtimeRoot);

            var scope = new KimodoRuntimeScope(scenarioName, workingRoot, runtimeRoot);
            KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = runtimeRoot;
            scope.Log("Runtime copied from template.");
            return scope;
        }

        internal static async Task EnsureSetupOrIgnoreAsync(KimodoRuntimeScope scope, int timeoutSeconds = 180)
        {
            string setupScript = Path.Combine(scope.RuntimeRoot, "bash", "setup.bat");
            if (!File.Exists(setupScript))
            {
                Assert.Ignore($"setup script missing: {setupScript}");
            }

            scope.Log("Running setup script.");
            int code = await RunScriptAndWaitAsync(setupScript, "--output file", timeoutSeconds * 1000);
            scope.Log($"Setup exit code: {code}");

            if (code != 0)
            {
                string setupLog = Path.Combine(scope.RuntimeRoot, "log", "setup.log");
                string message = File.Exists(setupLog)
                    ? ReadLastLines(setupLog, 60)
                    : "setup log missing";
                Assert.Ignore($"setup failed in environment. ExitCode={code}. {message}");
            }
        }

        internal static async Task<KimodoBridgeClient> StartClientOrIgnoreAsync(KimodoRuntimeScope scope)
        {
            string launcher = KimodoServerRuntimeUtil.ResolveStartScript(scope.RuntimeRoot);
            if (string.IsNullOrWhiteSpace(launcher) || !File.Exists(launcher))
            {
                Assert.Ignore("start script not found in runtime root.");
            }

            var client = new KimodoBridgeClient();
            try
            {
                await client.StartAsync(
                    launcher,
                    DefaultModel,
                    false,
                    scope.RuntimeRoot,
                    600f,
                    progress => scope.Log("StartAsync: " + progress),
                    CancellationToken.None);

                scope.Log("Client started.");
                return client;
            }
            catch (Exception ex)
            {
                scope.Log("Client start failed: " + ex.Message);
                string downloadLog = Path.Combine(scope.RuntimeRoot, "log", "download_model.log");
                string setupLog = Path.Combine(scope.RuntimeRoot, "log", "setup.log");
                string hint = BuildCommonFailureHint(downloadLog, setupLog);
                Assert.Ignore("Runtime start unavailable in current environment. " + hint);
                throw;
            }
        }

        internal static async Task CleanupScopeAsync(KimodoRuntimeScope scope)
        {
            if (scope == null)
            {
                return;
            }

            try
            {
                await KimodoServerLifecycleManager.CloseServerAsync();
            }
            catch (Exception ex)
            {
                scope.Log("CloseServerAsync failed: " + ex.Message);
            }

            try
            {
                KillRuntimeProcesses(scope.RuntimeRoot, scope);
            }
            catch (Exception ex)
            {
                scope.Log("KillRuntimeProcesses failed: " + ex.Message);
            }

            await WaitForNoRuntimeProcessesAsync(scope.RuntimeRoot, TimeSpan.FromSeconds(10), scope);
            await WaitForPortFileUnreachableOrMissingAsync(scope.RuntimeRoot, TimeSpan.FromSeconds(10), scope);

            if (string.Equals(KimodoServerRuntimeUtil.RuntimeRootOverrideForTests, scope.RuntimeRoot, StringComparison.OrdinalIgnoreCase))
            {
                KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = null;
            }

            scope.Log("Cleanup completed.");
        }

        internal static async Task AssertNoOrphanProcessAndRecoverableAsync(KimodoRuntimeScope scope)
        {
            await WaitForNoRuntimeProcessesAsync(scope.RuntimeRoot, TimeSpan.FromSeconds(10), scope);
            await WaitForPortFileUnreachableOrMissingAsync(scope.RuntimeRoot, TimeSpan.FromSeconds(10), scope);

            string start = KimodoServerRuntimeUtil.ResolveStartScript(scope.RuntimeRoot);
            Assert.IsFalse(string.IsNullOrWhiteSpace(start), "Start script should still be resolvable after cleanup.");
        }

        internal static async Task<int> RunScriptAndWaitAsync(string scriptPath, string args, int timeoutMs)
        {
            using Process process = StartScript(scriptPath, args, useShellExecute: false, keepWindowOpen: false);
            if (process == null)
            {
                return -1;
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            while (!process.HasExited)
            {
                if (cts.IsCancellationRequested)
                {
                    TryKillTree(process.Id);
                    return -2;
                }

                await Task.Delay(200);
            }

            return process.ExitCode;
        }

        internal static Process StartScript(string scriptPath, string args, bool useShellExecute, bool keepWindowOpen)
        {
            string fullPath = Path.GetFullPath(scriptPath);
            string ext = Path.GetExtension(fullPath)?.ToLowerInvariant() ?? string.Empty;
            string workingDir = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            string safeArgs = string.IsNullOrWhiteSpace(args) ? string.Empty : " " + args.Trim();

            ProcessStartInfo psi;
            if (ext == ".bat" || ext == ".cmd")
            {
                string mode = keepWindowOpen ? "/k" : "/c";
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s {mode} \"\"{fullPath}\"{safeArgs}\"",
                    WorkingDirectory = workingDir,
                    UseShellExecute = useShellExecute,
                    CreateNoWindow = !keepWindowOpen
                };
            }
            else if (ext == ".sh")
            {
                psi = new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"\"{fullPath}\"{safeArgs}",
                    WorkingDirectory = workingDir,
                    UseShellExecute = useShellExecute,
                    CreateNoWindow = !keepWindowOpen
                };
            }
            else
            {
                throw new NotSupportedException($"Unsupported script extension: {ext}");
            }

            return Process.Start(psi);
        }

        internal static async Task WaitForNoRuntimeProcessesAsync(string runtimeRoot, TimeSpan timeout, KimodoRuntimeScope scope)
        {
            _ = scope;
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                var snapshots = GetRuntimeProcessSnapshots(runtimeRoot);
                if (snapshots.Count == 0)
                {
                    return;
                }

                await Task.Delay(250);
            }

            var remain = GetRuntimeProcessSnapshots(runtimeRoot);
            Assert.Fail("Orphan process detected: "
                + string.Join(" | ", remain.Select(r => r.ToDisplayString()))
                + " | " + BuildFailureDiagnostics(runtimeRoot));
        }

        internal static async Task WaitForPortFileUnreachableOrMissingAsync(string runtimeRoot, TimeSpan timeout, KimodoRuntimeScope scope)
        {
            _ = scope;
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            string portFile = Path.Combine(runtimeRoot, "serverport");
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(portFile))
                {
                    return;
                }

                if (!KimodoServerRuntimeUtil.TryReadServerPort(runtimeRoot, out string host, out int port))
                {
                    return;
                }

                if (!IsTcpReachable(host, port, 1200))
                {
                    return;
                }

                await Task.Delay(250);
            }

            if (!KimodoServerRuntimeUtil.TryReadServerPort(runtimeRoot, out string finalHost, out int finalPort))
            {
                return;
            }

            Assert.Fail($"Port still reachable after timeout: {finalHost}:{finalPort}. {BuildFailureDiagnostics(runtimeRoot)}");
        }

        internal static void KillRuntimeProcesses(string runtimeRoot, KimodoRuntimeScope scope)
        {
            foreach (ProcessSnapshot snapshot in GetRuntimeProcessSnapshots(runtimeRoot))
            {
                scope.Log("Killing runtime process: " + snapshot.ToDisplayString());
                TryKillTree(snapshot.ProcessId);
            }
        }

        internal static int GetClientPidForTests(KimodoBridgeClient client)
        {
            var field = typeof(KimodoBridgeClient).GetField("processId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
            {
                return -1;
            }

            object value = field.GetValue(client);
            return value is int pid ? pid : -1;
        }

        internal static List<ProcessSnapshot> GetRuntimeProcessSnapshots(string runtimeRoot)
        {
            string normalized = NormalizeForContains(runtimeRoot);
            string ps = "$ErrorActionPreference='SilentlyContinue';"
                + "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine | ConvertTo-Json -Compress";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return new List<ProcessSnapshot>();
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(8000);
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return new List<ProcessSnapshot>();
            }

            var result = new List<ProcessSnapshot>();
            JToken token;
            try
            {
                token = JToken.Parse(stdout);
            }
            catch
            {
                return result;
            }

            if (token is JObject single)
            {
                TryAddSnapshot(single, normalized, result);
                return result;
            }

            if (token is JArray arr)
            {
                foreach (JToken item in arr)
                {
                    if (item is JObject obj)
                    {
                        TryAddSnapshot(obj, normalized, result);
                    }
                }
            }

            return result;
        }

        internal static string ReadLastLines(string filePath, int lineCount)
        {
            if (!File.Exists(filePath))
            {
                return string.Empty;
            }

            string[] lines = File.ReadAllLines(filePath);
            int start = Math.Max(0, lines.Length - lineCount);
            return string.Join("\n", lines.Skip(start));
        }

        private static string BuildCommonFailureHint(string downloadLog, string setupLog)
        {
            var sb = new StringBuilder();
            if (File.Exists(downloadLog))
            {
                sb.Append("download_model.log tail: ").Append(ReadLastLines(downloadLog, 30));
            }

            if (File.Exists(setupLog))
            {
                if (sb.Length > 0)
                {
                    sb.Append(" | ");
                }
                sb.Append("setup.log tail: ").Append(ReadLastLines(setupLog, 30));
            }

            if (sb.Length == 0)
            {
                sb.Append("No setup/download logs found.");
            }

            return sb.ToString();
        }

        private static bool IsTcpReachable(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                Task connect = client.ConnectAsync(host, port);
                bool done = connect.Wait(timeoutMs);
                return done && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static void TryAddSnapshot(JObject obj, string normalizedRuntime, List<ProcessSnapshot> list)
        {
            int pid = obj.Value<int?>("ProcessId") ?? -1;
            if (pid <= 0)
            {
                return;
            }

            string name = obj.Value<string>("Name") ?? string.Empty;
            string cmd = obj.Value<string>("CommandLine") ?? string.Empty;
            string normalizedCmd = NormalizeForContains(cmd);
            if (!string.IsNullOrWhiteSpace(normalizedRuntime) && normalizedCmd.IndexOf(normalizedRuntime, StringComparison.Ordinal) >= 0)
            {
                list.Add(new ProcessSnapshot
                {
                    ProcessId = pid,
                    ParentProcessId = obj.Value<int?>("ParentProcessId") ?? -1,
                    Name = name,
                    CommandLine = cmd
                });
            }
        }

        private static void TryKillTree(int pid)
        {
            if (pid <= 0)
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            using Process killer = Process.Start(psi);
            killer?.WaitForExit(5000);
        }

        private static string ResolvePackageRoot()
        {
            try
            {
                PackageInfo info = PackageInfo.FindForAssembly(typeof(KimodoBridgeTestHarness).Assembly);
                if (info != null && !string.IsNullOrWhiteSpace(info.resolvedPath))
                {
                    return Path.GetFullPath(info.resolvedPath);
                }
            }
            catch
            {
                // ignore and fallback
            }

            string cwd = Path.GetFullPath(Environment.CurrentDirectory);
            string probe = cwd;
            for (int i = 0; i < 8; i++)
            {
                if (File.Exists(Path.Combine(probe, "package.json"))
                    && Directory.Exists(Path.Combine(probe, "Editor"))
                    && Directory.Exists(Path.Combine(probe, "Runtime")))
                {
                    return probe;
                }

                DirectoryInfo parent = Directory.GetParent(probe);
                if (parent == null)
                {
                    break;
                }
                probe = parent.FullName;
            }

            throw new DirectoryNotFoundException($"Cannot resolve package root from cwd={cwd}");
        }

        private static string MakeSafePathFragment(string value)
        {
            string safe = value;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(safe) ? "scenario" : safe;
        }

        private static string BuildFailureDiagnostics(string runtimeRoot)
        {
            var sb = new StringBuilder();
            try
            {
                var procs = GetRuntimeProcessSnapshots(runtimeRoot);
                if (procs.Count > 0)
                {
                    sb.Append("active_pids=").Append(string.Join(",", procs.Select(p => p.ProcessId)));
                    sb.Append("; ");
                }

                string portFile = Path.Combine(runtimeRoot, "serverport");
                if (File.Exists(portFile))
                {
                    sb.Append("serverport=").Append(File.ReadAllText(portFile).Trim()).Append("; ");
                }

                string runServerLog = Path.Combine(runtimeRoot, "log", "run_server.log");
                string setupLog = Path.Combine(runtimeRoot, "log", "setup.log");
                string downloadLog = Path.Combine(runtimeRoot, "log", "download_model.log");

                if (File.Exists(runServerLog))
                {
                    sb.Append("run_server_tail=").Append(ReadLastLines(runServerLog, 100)).Append("; ");
                }
                if (File.Exists(setupLog))
                {
                    sb.Append("setup_tail=").Append(ReadLastLines(setupLog, 100)).Append("; ");
                }
                if (File.Exists(downloadLog))
                {
                    sb.Append("download_tail=").Append(ReadLastLines(downloadLog, 100)).Append("; ");
                }
            }
            catch (Exception e)
            {
                sb.Append("diag_error=").Append(e.Message);
            }

            return sb.ToString();
        }

        private static string NormalizeForContains(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Replace('/', '\\').Trim().ToLowerInvariant();
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(file);
                string target = Path.Combine(destinationDir, fileName);
                File.Copy(file, target, true);
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(dir);
                if (string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CopyDirectoryRecursive(dir, Path.Combine(destinationDir, dirName));
            }
        }

        internal sealed class ProcessSnapshot
        {
            internal int ProcessId;
            internal int ParentProcessId;
            internal string Name;
            internal string CommandLine;

            internal string ToDisplayString()
            {
                return $"pid={ProcessId}, ppid={ParentProcessId}, name={Name}, cmd={CommandLine}";
            }
        }
    }
}
