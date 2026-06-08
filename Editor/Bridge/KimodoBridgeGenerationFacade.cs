using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoBridgeGenerationFacade : IDisposable
    {
        internal enum ShutdownMode
        {
            DetachOnly = 0,
            KillAndDispose = 1
        }

        private KimodoRuntimeGenerationService sharedRuntimeGenerationService;
        private string currentServiceRuntimeRoot = string.Empty;
        private string currentServiceLauncherPath = string.Empty;
        private string currentServiceModelName = string.Empty;
        private string currentServiceModelsRoot = string.Empty;
        private bool currentServiceHighVram;
        private bool currentServiceForceSetup;
        private bool isClosing;
        private int shutdownTicket;

        internal async Task<string> StartServerAsync(
            string launcherPath,
            string modelName,
            bool highVram,
            string kimodoRootPath,
            string modelsRoot,
            bool forceSetup,
            Action<string> progress,
            CancellationToken token)
        {
            float startupTimeoutSeconds = BridgeRuntimeSettings.DefaultStartupTimeoutMs / 1000f;
            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(kimodoRootPath, highVram, modelName, modelsRoot);
            if (points > 0)
            {
                int minutes = Math.Max(3, points * 3);
                startupTimeoutSeconds = Math.Max(BridgeRuntimeSettings.DefaultStartupTimeoutMs / 1000f, minutes * 60f);
            }

            KimodoRuntimeGenerationService runtimeService = GetOrCreateRuntimeGenerationService(
                kimodoRootPath,
                launcherPath,
                modelName,
                highVram,
                modelsRoot,
                forceSetup,
                startupTimeoutMs: (int)Math.Round(startupTimeoutSeconds * 1000f));
            return await runtimeService.StartAsync(KimodoBackendType.Bridge, progress, token);
        }

        internal async Task<KimodoGenerationResultDto> GenerateBridgeAsync(
            string launcherPath,
            string modelName,
            bool highVram,
            string kimodoRootPath,
            string modelsRoot,
            KimodoGenerationRequestDto request,
            Action<string> progress,
            CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            int startupTimeoutMs = BridgeRuntimeSettings.DefaultStartupTimeoutMs;
            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(kimodoRootPath, highVram, modelName, modelsRoot);
            if (points > 0)
            {
                int minutes = Math.Max(3, points * 3);
                startupTimeoutMs = Math.Max(startupTimeoutMs, (int)Math.Round(Math.Max(BridgeRuntimeSettings.DefaultStartupTimeoutMs / 1000f, minutes * 60f) * 1000f));
            }

            KimodoRuntimeGenerationService runtimeService = GetOrCreateRuntimeGenerationService(
                kimodoRootPath,
                launcherPath,
                modelName,
                highVram,
                modelsRoot,
                forceSetup: false,
                startupTimeoutMs: startupTimeoutMs);

            await runtimeService.StartAsync(KimodoBackendType.Bridge, progress, token);
            return await runtimeService.GenerateAsync(request, KimodoBackendType.Bridge, progress, token);
        }

        internal async Task CloseServerAsync(Func<Task<(bool hasEndpoint, string host, int port)>> tryGetEndpointAsync)
        {
            await ShutdownAsync(ShutdownMode.KillAndDispose, tryGetEndpointAsync, CancellationToken.None);
        }

        internal void DetachSharedRuntimeGenerationService()
        {
            _ = ShutdownAsync(ShutdownMode.DetachOnly, null, CancellationToken.None);
        }

        internal void DisposeSharedRuntimeGenerationService()
        {
            _ = ShutdownAsync(ShutdownMode.KillAndDispose, null, CancellationToken.None);
        }

        internal bool TryGetAttachedServiceRuntimeRoot(out string runtimeRoot)
        {
            runtimeRoot = currentServiceRuntimeRoot;
            return !string.IsNullOrWhiteSpace(runtimeRoot);
        }

        private KimodoRuntimeGenerationService GetOrCreateRuntimeGenerationService(
            string runtimeRoot,
            string launcherPath,
            string modelName,
            bool highVram,
            string modelsRoot,
            bool forceSetup = false,
            int startupTimeoutMs = BridgeRuntimeSettings.DefaultStartupTimeoutMs)
        {
            string resolvedRuntimeRoot = Path.GetFullPath(runtimeRoot ?? string.Empty);
            string resolvedLauncherPath = Path.GetFullPath(launcherPath ?? string.Empty);
            string resolvedModelName = string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim();
            string resolvedModelsRoot = string.IsNullOrWhiteSpace(modelsRoot) ? string.Empty : Path.GetFullPath(modelsRoot.Trim());

            bool reusable =
                sharedRuntimeGenerationService != null &&
                string.Equals(currentServiceRuntimeRoot, resolvedRuntimeRoot, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentServiceLauncherPath, resolvedLauncherPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentServiceModelName, resolvedModelName, StringComparison.Ordinal) &&
                currentServiceHighVram == highVram &&
                currentServiceForceSetup == forceSetup &&
                string.Equals(currentServiceModelsRoot, resolvedModelsRoot, StringComparison.OrdinalIgnoreCase);

            if (reusable)
            {
                return sharedRuntimeGenerationService;
            }

            try
            {
                sharedRuntimeGenerationService?.Dispose();
            }
            catch
            {
                // ignore disposal failure
            }

            var settings = new KimodoRuntimeGenerationSettings
            {
                bridgeSettings = CreateBridgeSettings(
                    runtimeRoot: resolvedRuntimeRoot,
                    launcherPath: resolvedLauncherPath,
                    modelName: resolvedModelName,
                    highVram: highVram,
                    forceSetup: forceSetup,
                    modelsRoot: resolvedModelsRoot,
                    startupTimeoutMs: Math.Max(30000, startupTimeoutMs)),
                comfyWorkflowResourceName = "kimodo-unity-workflow"
            };

            sharedRuntimeGenerationService = new KimodoRuntimeGenerationService(settings);
            currentServiceRuntimeRoot = resolvedRuntimeRoot;
            currentServiceLauncherPath = resolvedLauncherPath;
            currentServiceModelName = resolvedModelName;
            currentServiceHighVram = highVram;
            currentServiceForceSetup = forceSetup;
            currentServiceModelsRoot = resolvedModelsRoot;
            return sharedRuntimeGenerationService;
        }

        private static BridgeRuntimeSettings CreateBridgeSettings(
            string runtimeRoot,
            string launcherPath,
            string modelName,
            bool highVram,
            bool forceSetup,
            string modelsRoot,
            int startupTimeoutMs)
        {
            KimodoPlayableClipGenerationSettings editorSettings = KimodoPlayableClipGenerationSettings.instance;
            return new BridgeRuntimeSettings
            {
                runtimeRoot = runtimeRoot,
                launcherPath = launcherPath,
                modelName = modelName,
                highVram = highVram,
                forceSetup = forceSetup,
                modelsRoot = modelsRoot,
                startupTimeoutMs = startupTimeoutMs,
                connectTimeoutMs = BridgeRuntimeSettings.DefaultConnectTimeoutMs,
                ioTimeoutMs = BridgeRuntimeSettings.DefaultIoTimeoutMs,
                modelLoadingTimeoutMs = BridgeRuntimeSettings.DefaultModelLoadingTimeoutMs,
                modelLoadingPollIntervalMs = BridgeRuntimeSettings.DefaultModelLoadingPollIntervalMs,
                statusConnectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                statusIoTimeoutMs = BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                idleTimeoutSeconds = editorSettings != null ? editorSettings.ServerIdleShutdownSeconds : 0,
                preserveProcessOnCancellation = editorSettings != null && editorSettings.AlwaysKeepServerExperimental
            };
        }

        private void ResetSharedServiceState()
        {
            currentServiceRuntimeRoot = string.Empty;
            currentServiceLauncherPath = string.Empty;
            currentServiceModelName = string.Empty;
            currentServiceModelsRoot = string.Empty;
            currentServiceHighVram = false;
            currentServiceForceSetup = false;
        }

        internal async Task ShutdownAsync(
            ShutdownMode mode,
            Func<Task<(bool hasEndpoint, string host, int port)>> tryGetEndpointAsync,
            CancellationToken token)
        {
            int ticket = Interlocked.Increment(ref shutdownTicket);
            if (isClosing)
            {
                UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] skip duplicate shutdown, mode={mode}, ticket={ticket}");
                return;
            }

            isClosing = true;
            UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] begin mode={mode}, ticket={ticket}");

            bool endpointKnown = false;
            string endpointHost = string.Empty;
            int endpointPort = -1;
            if (mode == ShutdownMode.KillAndDispose && tryGetEndpointAsync != null)
            {
                try
                {
                    (endpointKnown, endpointHost, endpointPort) = await tryGetEndpointAsync();
                }
                catch
                {
                    endpointKnown = false;
                    endpointHost = string.Empty;
                    endpointPort = -1;
                }
            }

            try
            {
                KimodoRuntimeGenerationService runtimeService = sharedRuntimeGenerationService;
                sharedRuntimeGenerationService = null;
                ResetSharedServiceState();

                if (runtimeService != null)
                {
                    try
                    {
                        if (mode == ShutdownMode.DetachOnly)
                        {
                            await runtimeService.DetachAsync(KimodoBackendType.Bridge, token);
                            UnityEngine.Debug.Log("[Kimodo][BridgeShutdown] detached shared runtime service.");
                        }
                        else
                        {
                            await runtimeService.KillAsync(KimodoBackendType.Bridge, token);
                            UnityEngine.Debug.Log("[Kimodo][BridgeShutdown] killed shared runtime service.");
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                    finally
                    {
                        try { runtimeService.Dispose(); } catch { }
                    }
                }
                else if (mode == ShutdownMode.KillAndDispose && endpointKnown)
                {
                    try
                    {
                        await BridgeRuntimeControl.TrySendQuitAsync(
                            endpointHost,
                            endpointPort,
                            BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                            BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                            token);
                        UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] sent quit to {endpointHost}:{endpointPort}.");
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (mode == ShutdownMode.KillAndDispose && endpointKnown)
                {
                    bool fullyStopped = await EnsureEndpointStoppedAfterShutdownAsync(endpointHost, endpointPort, token);
                    if (!fullyStopped)
                    {
                        throw new InvalidOperationException(
                            $"Bridge server still responsive at {endpointHost}:{endpointPort} after shutdown attempt.");
                    }
                }
            }
            finally
            {
                if (ticket == shutdownTicket)
                {
                    isClosing = false;
                }

                UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] end mode={mode}, ticket={ticket}");
            }
        }

        private static async Task<bool> EnsureEndpointStoppedAfterShutdownAsync(string host, int port, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0)
            {
                return true;
            }

            bool alive = await BridgeRuntimeControl.IsServerResponsiveAsync(
                host,
                port,
                connectTimeoutMs: 500,
                ioTimeoutMs: 800,
                acceptLoading: true,
                token: token);
            if (!alive)
            {
                return true;
            }

            UnityEngine.Debug.LogWarning($"[Kimodo][BridgeShutdown] endpoint still alive at {host}:{port}, trying force-kill by port owner.");
            bool forceKillTriggered = TryForceKillListeningProcessByPort(port);
            if (!forceKillTriggered)
            {
                return false;
            }

            await Task.Delay(700, CancellationToken.None);

            bool aliveAfterForceKill = await BridgeRuntimeControl.IsServerResponsiveAsync(
                host,
                port,
                connectTimeoutMs: 500,
                ioTimeoutMs: 800,
                acceptLoading: true,
                token: token);
            if (!aliveAfterForceKill)
            {
                TryDeleteServerPortByEndpoint(host, port);
                UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] force-kill succeeded for {host}:{port}.");
                return true;
            }

            UnityEngine.Debug.LogWarning($"[Kimodo][BridgeShutdown] endpoint still alive after force-kill at {host}:{port}.");
            return false;
        }

        private static void TryDeleteServerPortByEndpoint(string host, int port)
        {
            if (port <= 0)
            {
                return;
            }

            try
            {
                string runtimeRoot = KimodoBridgeRuntimeInstallFacade.GetRuntimeRootPath();
                if (string.IsNullOrWhiteSpace(runtimeRoot))
                {
                    return;
                }

                string serverPortPath = BridgeEndpointResolver.GetServerPortFilePath(runtimeRoot);
                if (!File.Exists(serverPortPath))
                {
                    return;
                }

                if (!BridgeEndpointResolver.TryReadServerEndpointFromFile(
                        serverPortPath,
                        string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host,
                        out string fileHost,
                        out int filePort,
                        out _))
                {
                    File.Delete(serverPortPath);
                    return;
                }

                if (filePort == port)
                {
                    File.Delete(serverPortPath);
                }
            }
            catch
            {
                // ignore cleanup failure
            }
        }

        private static bool TryForceKillListeningProcessByPort(int port)
        {
            if (port <= 0)
            {
                return false;
            }

            if (UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsEditor &&
                UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsPlayer)
            {
                return false;
            }

            HashSet<int> pids = QueryListeningPidsByPortOnWindows(port);
            if (pids.Count == 0)
            {
                UnityEngine.Debug.LogWarning($"[Kimodo][BridgeShutdown] no listening PID found for port {port}.");
                return false;
            }

            bool anyKilled = false;
            foreach (int pid in pids)
            {
                if (pid <= 0)
                {
                    continue;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/PID {pid} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using Process killer = Process.Start(psi);
                    if (killer != null)
                    {
                        killer.WaitForExit(5000);
                        anyKilled = true;
                        UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] taskkill by port {port}, pid={pid}, exit={killer.ExitCode}");
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[Kimodo][BridgeShutdown] taskkill failed for pid={pid}: {ex.Message}");
                }
            }

            return anyKilled;
        }

        private static HashSet<int> QueryListeningPidsByPortOnWindows(int port)
        {
            var result = new HashSet<int>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat.exe",
                    Arguments = "-ano -p tcp",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using Process p = Process.Start(psi);
                if (p == null)
                {
                    return result;
                }

                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] columns = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (columns.Length < 5)
                    {
                        continue;
                    }

                    string localAddress = columns[1];
                    string state = columns[3];
                    string pidText = columns[4];

                    if (!string.Equals(state, "LISTENING", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(state, "LISTEN", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryParsePortFromEndpoint(localAddress, out int localPort) || localPort != port)
                    {
                        continue;
                    }

                    if (int.TryParse(pidText, out int pid) && pid > 0)
                    {
                        result.Add(pid);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return result;
        }

        private static bool TryParsePortFromEndpoint(string endpoint, out int port)
        {
            port = -1;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return false;
            }

            int index = endpoint.LastIndexOf(':');
            if (index < 0 || index >= endpoint.Length - 1)
            {
                return false;
            }

            string portText = endpoint.Substring(index + 1);
            return int.TryParse(portText, out port) && port > 0 && port <= 65535;
        }

        public void Dispose()
        {
            _ = ShutdownAsync(ShutdownMode.KillAndDispose, null, CancellationToken.None);
        }
    }
}
