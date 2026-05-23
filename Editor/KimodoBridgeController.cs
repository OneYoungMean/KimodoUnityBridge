using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using KimodoUnityMotionTools;
using KimodoUnityMotionTools.Bridge;
using KimodoUnityMotionTools.Generation;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [InitializeOnLoad]
    internal static class KimodoBridgeController
    {
        internal sealed class InstalledModelInfo
        {
            public string Name;
            public string DirectoryPath;
        }

        internal readonly struct ServerStatusSnapshot
        {
            public readonly bool Ready;
            public readonly bool Running;
            public readonly bool HasPort;
            public readonly bool QueryInFlight;
            public readonly string Host;
            public readonly int Port;

            public ServerStatusSnapshot(bool ready, bool running, bool hasPort, bool queryInFlight, string host, int port)
            {
                Ready = ready;
                Running = running;
                HasPort = hasPort;
                QueryInFlight = queryInFlight;
                Host = host ?? "127.0.0.1";
                Port = port;
            }
        }

        private static KimodoRuntimeGenerationService sharedRuntimeGenerationService;
        private static string currentServiceRuntimeRoot = string.Empty;
        private static string currentServiceLauncherPath = string.Empty;
        private static string currentServiceModelName = string.Empty;
        private static string currentServiceModelsRoot = string.Empty;
        private static bool currentServiceHighVram;
        private static bool currentServiceForceSetup;
        private static bool isClosing;
        private static bool isRecovering;
        private static int runtimeMaintenanceDepth;
        private static readonly object serverStateGate = new object();
        private static bool serverRunningCached;
        private static bool serverHasPortCached;
        private static string serverHostCached = "127.0.0.1";
        private static int serverPortCached = -1;
        private static bool serverStateReady;
        private static bool serverStateQueryInFlight;
        private static int serverStateQueryVersion;
        private static double nextServerStateQueryAt;

        private const double ServerStateQueryCooldownSeconds = 2.0;

        static KimodoBridgeController()
        {
            EditorApplication.delayCall += RecoverBridgeAfterDomainReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
            EditorApplication.quitting += HandleEditorQuitting;
        }

        internal static bool IsServerRunning
        {
            get
            {
                RequestServerStateRefresh(force: false);
                lock (serverStateGate)
                {
                    return serverStateReady && serverRunningCached;
                }
            }
        }

        internal static ServerStatusSnapshot GetServerStatusSnapshot()
        {
            lock (serverStateGate)
            {
                return new ServerStatusSnapshot(
                    ready: serverStateReady,
                    running: serverRunningCached,
                    hasPort: serverHasPortCached,
                    queryInFlight: serverStateQueryInFlight,
                    host: serverHostCached,
                    port: serverPortCached);
            }
        }

        internal static void RequestServerStateRefresh(bool force)
        {
            ScheduleServerStateRefresh(force);
        }

        internal static string[] SupportedModelNames => KimodoServerRuntimeUtil.SupportedModelNames;

        internal static string GetRuntimeRootPath()
        {
            return KimodoServerRuntimeUtil.GetRuntimeRootPath();
        }

        internal static bool EnsureRuntimeRootExists()
        {
            return KimodoServerRuntimeUtil.EnsureRuntimeRootExists();
        }

        internal static string ResolveStartScript(string runtimeRoot)
        {
            return KimodoServerRuntimeUtil.ResolveStartScript(runtimeRoot);
        }

        internal static string ResolveRuntimeRootOrThrow()
        {
            string runtimeRoot = GetRuntimeRootPath();
            if (!Directory.Exists(runtimeRoot) && !EnsureRuntimeRootExists())
            {
                throw new DirectoryNotFoundException(
                    $"Bridge runtime root not found and bootstrap failed: {runtimeRoot}");
            }

            return Path.GetFullPath(runtimeRoot);
        }

        internal static string ResolveStartScriptOrThrow(string runtimeRoot)
        {
            string resolved = ResolveStartScript(runtimeRoot);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                return Path.GetFullPath(resolved);
            }

            throw new FileNotFoundException(
                $"Bridge launcher not found under runtime root: {runtimeRoot}. Expected new pipeline launcher: run_server.bat or bash/start_server.bat.");
        }

        internal static bool IsRuntimeMaintenanceInProgress => runtimeMaintenanceDepth > 0;

        internal static IDisposable EnterRuntimeMaintenanceScope()
        {
            Interlocked.Increment(ref runtimeMaintenanceDepth);
            return new RuntimeMaintenanceScope();
        }

        internal static bool TryReadServerPort(string runtimeRoot, out string host, out int port)
        {
            return BridgeRuntimeControl.TryReadServerEndpoint(runtimeRoot, out host, out port);
        }

        internal static async Task<bool> IsServerResponsiveAsync(string host, int port, CancellationToken token)
        {
            return await BridgeRuntimeControl.IsServerResponsiveAsync(
                host,
                port,
                BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                acceptLoading: true,
                token: token);
        }

        internal static async Task<bool> TrySendQuitAsync(string host, int port, CancellationToken token)
        {
            return await BridgeRuntimeControl.TrySendQuitAsync(
                host,
                port,
                BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                token);
        }

        internal static List<InstalledModelInfo> GetInstalledModels(string runtimeRoot)
        {
            List<KimodoServerRuntimeUtil.InstalledModelInfo> source = KimodoServerRuntimeUtil.GetInstalledModels(runtimeRoot);
            var result = new List<InstalledModelInfo>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                KimodoServerRuntimeUtil.InstalledModelInfo item = source[i];
                result.Add(new InstalledModelInfo
                {
                    Name = item.Name,
                    DirectoryPath = item.DirectoryPath
                });
            }

            return result;
        }

        internal static bool IsModelInstalled(string runtimeRoot, string modelName, string modelsRootOverride = null)
        {
            return KimodoServerRuntimeUtil.IsSelectedBridgeModelInstalled(runtimeRoot, modelName, modelsRootOverride);
        }

        internal static bool TryGetModelMissingSetupMinutes(string runtimeRoot, bool highVram, string modelName, string modelsRootOverride, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return false;
            }

            // Custom models path means model completeness is managed by user.
            // Do not show "model missing" warning in this mode.
            if (!string.IsNullOrWhiteSpace(modelsRootOverride))
            {
                return false;
            }

            if (KimodoServerRuntimeUtil.IsSelectedBridgeModelInstalled(runtimeRoot, modelName, modelsRootOverride)
                && KimodoServerRuntimeUtil.IsTextEncoderInstalled(runtimeRoot, highVram, modelsRootOverride))
            {
                return false;
            }

            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(runtimeRoot, highVram, modelName, modelsRootOverride);
            minutes = Math.Max(3, points * 3);
            return true;
        }

        private static KimodoRuntimeGenerationService GetOrCreateRuntimeGenerationService(
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

        private static async void RecoverBridgeAfterDomainReload()
        {
            if (isRecovering)
            {
                return;
            }

            isRecovering = true;
            try
            {
                string runtimeRoot = GetRuntimeRootPath();
                if (!Directory.Exists(runtimeRoot))
                {
                    return;
                }

                if (!TryReadServerPort(runtimeRoot, out string host, out int port))
                {
                    return;
                }

                if (!await IsServerResponsiveAsync(host, port, CancellationToken.None))
                {
                    return;
                }

                string launcherPath = ResolveStartScript(runtimeRoot);
                if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                {
                    return;
                }

                var settings = new KimodoRuntimeGenerationSettings
                {
                    bridgeSettings = CreateBridgeSettings(
                        runtimeRoot: runtimeRoot,
                        launcherPath: launcherPath,
                        modelName: "Kimodo-SOMA-RP-v1",
                        highVram: false,
                        forceSetup: false,
                        modelsRoot: string.Empty,
                        startupTimeoutMs: BridgeRuntimeSettings.DefaultStartupTimeoutMs)
                };
                using var bridge = new KimodoBridgeService(settings.bridgeSettings);
                _ = await bridge.AttachAsync(message => UnityEngine.Debug.Log($"[KimodoBridge] {message}"), CancellationToken.None);
            }
            catch
            {
                // ignore recovery failures
            }
            finally
            {
                isRecovering = false;
            }
        }

        internal static async Task<string> StartServerAsync(
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
                // Use estimated setup duration as startup wait budget on first/missing-model runs.
                // Keep a 10-minute floor to avoid premature timeout on slower disks/networks.
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
            return await runtimeService.StartAsync(
                KimodoBackendType.Bridge,
                progress,
                token);
        }

        internal static async Task<KimodoGenerationResultDto> GenerateBridgeAsync(
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

        internal static async Task CloseServerAsync()
        {
            if (isClosing)
            {
                return;
            }

            isClosing = true;
            try
            {
                try
                {
                    KimodoRuntimeGenerationService runtimeService = sharedRuntimeGenerationService;
                    sharedRuntimeGenerationService = null;
                    if (runtimeService != null)
                    {
                        await runtimeService.KillAsync(KimodoBackendType.Bridge, CancellationToken.None);
                    }
                    else
                    {
                        string runtimeRoot = GetRuntimeRootPath();
                        if (TryReadServerPort(runtimeRoot, out string host, out int port))
                        {
                            // Domain reload may clear our process handle; fall back to TCP quit.
                            await TrySendQuitAsync(host, port, CancellationToken.None);
                        }
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    try { sharedRuntimeGenerationService?.Dispose(); } catch { }
                    currentServiceRuntimeRoot = string.Empty;
                    currentServiceLauncherPath = string.Empty;
                    currentServiceModelName = string.Empty;
                    currentServiceModelsRoot = string.Empty;
                    currentServiceHighVram = false;
                    currentServiceForceSetup = false;
                }
            }
            finally
            {
                isClosing = false;
                InvalidateServerStateCache();
            }
        }

        private static void HandleBeforeAssemblyReload()
        {
            DetachSharedRuntimeGenerationService();
        }

        private static void HandleEditorQuitting()
        {
            DisposeSharedRuntimeGenerationService();
        }

        private static void DisposeSharedRuntimeGenerationService()
        {
            KimodoRuntimeGenerationService service = sharedRuntimeGenerationService;
            sharedRuntimeGenerationService = null;
            if (service == null)
            {
                return;
            }

            try
            {
                service.Dispose();
            }
            catch
            {
                // ignore
            }

            currentServiceRuntimeRoot = string.Empty;
            currentServiceLauncherPath = string.Empty;
            currentServiceModelName = string.Empty;
            currentServiceModelsRoot = string.Empty;
            currentServiceHighVram = false;
            currentServiceForceSetup = false;
            InvalidateServerStateCache();
        }

        private static void DetachSharedRuntimeGenerationService()
        {
            KimodoRuntimeGenerationService service = sharedRuntimeGenerationService;
            if (service == null)
            {
                return;
            }

            try
            {
                service.DetachAsync(KimodoBackendType.Bridge, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // ignore
            }
        }

        private static void ScheduleServerStateRefresh(bool force)
        {
            double now = EditorApplication.timeSinceStartup;
            string runtimeRoot = GetRuntimeRootPath();

            lock (serverStateGate)
            {
                if (!force)
                {
                    if (serverStateQueryInFlight || now < nextServerStateQueryAt)
                    {
                        return;
                    }
                }

                serverStateQueryInFlight = true;
                nextServerStateQueryAt = now + ServerStateQueryCooldownSeconds;
                int version = ++serverStateQueryVersion;
                _ = RefreshServerStateAsync(runtimeRoot, version);
            }
        }

        private static async Task RefreshServerStateAsync(string runtimeRoot, int queryVersion)
        {
            bool running = false;
            bool hasPort = false;
            string host = "127.0.0.1";
            int port = -1;
            try
            {
                if (!string.IsNullOrWhiteSpace(runtimeRoot) &&
                    Directory.Exists(runtimeRoot) &&
                    TryReadServerPort(runtimeRoot, out string readHost, out int readPort))
                {
                    hasPort = true;
                    host = readHost;
                    port = readPort;
                    running = await IsServerResponsiveAsync(readHost, readPort, CancellationToken.None);
                }
            }
            catch
            {
                running = false;
            }

            lock (serverStateGate)
            {
                if (queryVersion != serverStateQueryVersion)
                {
                    return;
                }

                serverRunningCached = running;
                serverHasPortCached = hasPort;
                serverHostCached = host;
                serverPortCached = port;
                serverStateReady = true;
                serverStateQueryInFlight = false;
            }
        }

        private static void InvalidateServerStateCache()
        {
            lock (serverStateGate)
            {
                serverRunningCached = false;
                serverHasPortCached = false;
                serverHostCached = "127.0.0.1";
                serverPortCached = -1;
                serverStateReady = false;
                serverStateQueryInFlight = false;
                serverStateQueryVersion++;
                nextServerStateQueryAt = 0.0;
            }

            ScheduleServerStateRefresh(force: true);
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
                statusIoTimeoutMs = BridgeRuntimeSettings.DefaultStatusIoTimeoutMs
            };
        }

        private sealed class RuntimeMaintenanceScope : IDisposable
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                int value = Interlocked.Decrement(ref runtimeMaintenanceDepth);
                if (value < 0)
                {
                    Interlocked.Exchange(ref runtimeMaintenanceDepth, 0);
                }
            }
        }
    }
}

