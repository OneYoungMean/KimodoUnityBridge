using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace KimodoBridge.Editor
{
    internal readonly struct ModelSetupStatus
    {
        public readonly bool Missing;
        public readonly int MissingPoints;
        public readonly int EstimatedMinutes;

        public ModelSetupStatus(bool missing, int missingPoints, int estimatedMinutes)
        {
            Missing = missing;
            MissingPoints = missingPoints;
            EstimatedMinutes = estimatedMinutes;
        }
    }

    internal readonly struct ModelDirectoryInfo
    {
        public readonly string Name;
        public readonly string DirectoryPath;

        public ModelDirectoryInfo(string name, string directoryPath)
        {
            Name = name ?? string.Empty;
            DirectoryPath = directoryPath ?? string.Empty;
        }
    }

    [InitializeOnLoad]
    public static class KimodoBridgeServerManage
    {
        private enum ShutdownMode
        {
            DetachOnly = 0,
            StopAndDispose = 1
        }

        private static KimodoBridgeService sharedBridgeService;
        private static string currentServiceRuntimeRoot = string.Empty;
        private static string currentServiceLauncherPath = string.Empty;
        private static string currentServiceModelName = string.Empty;
        private static string currentServiceModelsRoot = string.Empty;
        private static bool currentServiceHighVram;
        private static bool currentServiceForceSetup;
        private static bool currentServiceForceCpu;
        private static bool isClosing;
        private static int shutdownTicket;
        private static int runtimeMaintenanceDepth;
        private static readonly object sharedServiceGate = new object();
        static KimodoBridgeServerManage()
        {
            EditorCompilationStateGate.StateChanged += HandleCompilationStateChanged;
        }

        public static string[] SupportedModelNames => KimodoBridgeRuntimeInstallFacade.SupportedModelNames;

        internal static string GetRuntimeRootPath()
        {
            return KimodoBridgeRuntimeInstallFacade.GetRuntimeRootPath();
        }

        internal static bool BootstrapRuntimeRootIfMissing()
        {
            return KimodoBridgeRuntimeInstallFacade.BootstrapRuntimeRootIfMissing();
        }

        internal static bool ReinstallRuntimeRoot()
        {
            return KimodoBridgeRuntimeInstallFacade.ReinstallRuntimeRoot();
        }

        internal static string ResolveStartScript(string runtimeRoot)
        {
            return KimodoBridgeRuntimeInstallFacade.ResolveStartScript(runtimeRoot);
        }

        internal static string ResolveRuntimeRootOrThrow()
        {
            return KimodoBridgeRuntimeInstallFacade.ResolveRuntimeRootOrThrow();
        }

        internal static string ResolveStartScriptOrThrow(string runtimeRoot)
        {
            return KimodoBridgeRuntimeInstallFacade.ResolveStartScriptOrThrow(runtimeRoot);
        }

        internal static bool IsRuntimeMaintenanceInProgress => runtimeMaintenanceDepth > 0;

        internal static IDisposable EnterRuntimeMaintenanceScope()
        {
            Interlocked.Increment(ref runtimeMaintenanceDepth);
            return new RuntimeMaintenanceScope();
        }

        internal static bool TryGetModelMissingSetupMinutes(string runtimeRoot, bool highVram, string modelName, string modelsRootOverride, out int minutes)
        {
            return KimodoBridgeRuntimeInstallFacade.TryGetModelMissingSetupMinutes(runtimeRoot, highVram, modelName, modelsRootOverride, out minutes);
        }

        internal static ModelSetupStatus EvaluateModelSetupStatus(string runtimeRoot, bool highVram, string modelName, string modelsRootOverride)
        {
            return KimodoBridgeRuntimeInstallFacade.EvaluateModelSetupStatus(runtimeRoot, highVram, modelName, modelsRootOverride);
        }

        internal static List<ModelDirectoryInfo> QueryDisplayableModelDirectories(string modelsRoot)
        {
            return KimodoBridgeRuntimeInstallFacade.QueryDisplayableModelDirectories(modelsRoot);
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
            KimodoBridgeService bridgeService = GetOrCreateSharedBridgeService(
                kimodoRootPath,
                launcherPath,
                modelName,
                highVram,
                modelsRoot,
                forceSetup);
            return await bridgeService.StartAsync(progress, token).ConfigureAwait(false);
        }

        internal static async Task StopServerAsync(CancellationToken token)
        {
            await ShutdownAsync(ShutdownMode.StopAndDispose, token).ConfigureAwait(false);
        }

        private static void HandleCompilationStateChanged(bool active)
        {
            if (!active)
            {
                return;
            }

            _ = ShutdownAsync(ShutdownMode.DetachOnly, CancellationToken.None);
        }

        private static KimodoBridgeService GetOrCreateSharedBridgeService(
            string runtimeRoot,
            string launcherPath,
            string modelName,
            bool highVram,
            string modelsRoot,
            bool forceSetup)
        {
            bool forceCpu = KimodoPlayableClipGenerationSettings.instance != null &&
                KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental;
            int startupTimeoutMs = ComputeStartupTimeoutMs(runtimeRoot, highVram, modelName, modelsRoot);

            string resolvedRuntimeRoot = Path.GetFullPath(runtimeRoot ?? string.Empty);
            string resolvedLauncherPath = Path.GetFullPath(launcherPath ?? string.Empty);
            string resolvedModelName = string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim();
            string resolvedModelsRoot = string.IsNullOrWhiteSpace(modelsRoot) ? string.Empty : Path.GetFullPath(modelsRoot.Trim());
            lock (sharedServiceGate)
            {
                bool reusable =
                    sharedBridgeService != null &&
                    string.Equals(currentServiceRuntimeRoot, resolvedRuntimeRoot, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(currentServiceLauncherPath, resolvedLauncherPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(currentServiceModelName, resolvedModelName, StringComparison.Ordinal) &&
                    currentServiceHighVram == highVram &&
                    currentServiceForceSetup == forceSetup &&
                    currentServiceForceCpu == forceCpu &&
                    string.Equals(currentServiceModelsRoot, resolvedModelsRoot, StringComparison.OrdinalIgnoreCase);

                if (reusable)
                {
                    return sharedBridgeService;
                }

                try
                {
                    sharedBridgeService?.Dispose();
                }
                catch
                {
                    // Ignore dispose failure while replacing the shared service.
                }

                sharedBridgeService = new KimodoBridgeService(BridgeRuntimeSettingsFactory.Create(
                    resolvedRuntimeRoot,
                    resolvedLauncherPath,
                    resolvedModelName,
                    highVram,
                    forceSetup,
                    forceCpu,
                    resolvedModelsRoot,
                    startupTimeoutMs));
                currentServiceRuntimeRoot = resolvedRuntimeRoot;
                currentServiceLauncherPath = resolvedLauncherPath;
                currentServiceModelName = resolvedModelName;
                currentServiceHighVram = highVram;
                currentServiceForceSetup = forceSetup;
                currentServiceForceCpu = forceCpu;
                currentServiceModelsRoot = resolvedModelsRoot;
                return sharedBridgeService;
            }
        }

        private static int ComputeStartupTimeoutMs(string runtimeRoot, bool highVram, string modelName, string modelsRoot)
        {
            int startupTimeoutMs = BridgeRuntimeSettings.DefaultStartupTimeoutMs;
            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(runtimeRoot, highVram, modelName, modelsRoot);
            if (points > 0)
            {
                int minutes = Math.Max(3, points * 3);
                startupTimeoutMs = Math.Max(
                    startupTimeoutMs,
                    (int)Math.Round(Math.Max(BridgeRuntimeSettings.DefaultStartupTimeoutMs / 1000f, minutes * 60f) * 1000f));
            }

            return startupTimeoutMs;
        }

        private static async Task ShutdownAsync(ShutdownMode mode, CancellationToken token)
        {
            int ticket = Interlocked.Increment(ref shutdownTicket);
            if (isClosing)
            {
                UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] skip duplicate shutdown, mode={mode}, ticket={ticket}");
                return;
            }

            isClosing = true;
            UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] begin mode={mode}, ticket={ticket}");

            KimodoBridgeService bridgeService;
            lock (sharedServiceGate)
            {
                bridgeService = sharedBridgeService;
                sharedBridgeService = null;
                ResetSharedServiceState();
            }

            try
            {
                if (bridgeService == null)
                {
                    return;
                }

                if (mode == ShutdownMode.DetachOnly)
                {
                    await bridgeService.DetachAsync(token).ConfigureAwait(false);
                    UnityEngine.Debug.Log("[Kimodo][BridgeShutdown] detached shared bridge service.");
                }
                else
                {
                    await bridgeService.StopAsync(token).ConfigureAwait(false);
                    UnityEngine.Debug.Log("[Kimodo][BridgeShutdown] stopped shared bridge service.");
                }
            }
            finally
            {
                try
                {
                    bridgeService?.Dispose();
                }
                catch
                {
                    // Ignore dispose failure during shutdown.
                }

                if (ticket == shutdownTicket)
                {
                    isClosing = false;
                }

                UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] end mode={mode}, ticket={ticket}");
            }
        }

        private static void ResetSharedServiceState()
        {
            currentServiceRuntimeRoot = string.Empty;
            currentServiceLauncherPath = string.Empty;
            currentServiceModelName = string.Empty;
            currentServiceModelsRoot = string.Empty;
            currentServiceHighVram = false;
            currentServiceForceSetup = false;
            currentServiceForceCpu = false;
        }

        internal static bool HasConnectedSession
        {
            get
            {
                lock (sharedServiceGate)
                {
                    return sharedBridgeService != null && sharedBridgeService.IsConnected;
                }
            }
        }

        internal static bool TryGetConnectedEndpoint(out string host, out int port)
        {
            lock (sharedServiceGate)
            {
                if (sharedBridgeService != null && sharedBridgeService.IsConnected)
                {
                    return sharedBridgeService.TryGetCurrentEndpoint(out host, out port);
                }
            }

            host = "127.0.0.1";
            port = -1;
            return false;
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
