using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Process = System.Diagnostics.Process;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoBridgeGenerationFacade : IDisposable
    {
        internal enum ShutdownMode
        {
            DetachOnly = 0,
            StopAndDispose = 1
        }

        private KimodoRuntimeGenerationService sharedRuntimeGenerationService;
        private string currentServiceRuntimeRoot = string.Empty;
        private string currentServiceLauncherPath = string.Empty;
        private string currentServiceModelName = string.Empty;
        private string currentServiceModelsRoot = string.Empty;
        private bool currentServiceHighVram;
        private bool currentServiceForceSetup;
        private bool currentServiceForceCpu;
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
            bool forceCpu = KimodoPlayableClipGenerationSettings.instance != null &&
                KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental;
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
                forceCpu,
                startupTimeoutMs: (int)Math.Round(startupTimeoutSeconds * 1000f));
            return await runtimeService.StartAsync(KimodoBackendType.Bridge, progress, token).ConfigureAwait(false);
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

            bool forceCpu = KimodoPlayableClipGenerationSettings.instance != null &&
                KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental;
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
                forceCpu,
                startupTimeoutMs: startupTimeoutMs);

            await runtimeService.StartAsync(KimodoBackendType.Bridge, progress, token).ConfigureAwait(false);
            return await runtimeService.GenerateAsync(request, KimodoBackendType.Bridge, progress, token).ConfigureAwait(false);
        }

        internal async Task CloseServerAsync(Func<Task<(bool hasEndpoint, string host, int port)>> tryGetEndpointAsync)
        {
            await ShutdownAsync(ShutdownMode.StopAndDispose, tryGetEndpointAsync, CancellationToken.None).ConfigureAwait(false);
        }

        internal void DetachSharedRuntimeGenerationService()
        {
            _ = ShutdownAsync(ShutdownMode.DetachOnly, null, CancellationToken.None);
        }

        internal void DisposeSharedRuntimeGenerationService()
        {
            _ = ShutdownAsync(ShutdownMode.StopAndDispose, null, CancellationToken.None);
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
            bool forceCpu = false,
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
                currentServiceForceCpu == forceCpu &&
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
                    forceCpu: forceCpu,
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
            currentServiceForceCpu = forceCpu;
            currentServiceModelsRoot = resolvedModelsRoot;
            return sharedRuntimeGenerationService;
        }

        private static BridgeRuntimeSettings CreateBridgeSettings(
            string runtimeRoot,
            string launcherPath,
            string modelName,
            bool highVram,
            bool forceSetup,
            bool forceCpu,
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
                forceCpu = forceCpu,
                modelsRoot = modelsRoot,
                startupTimeoutMs = startupTimeoutMs,
                connectTimeoutMs = BridgeRuntimeSettings.DefaultConnectTimeoutMs,
                ioTimeoutMs = BridgeRuntimeSettings.DefaultIoTimeoutMs,
                modelLoadingTimeoutMs = BridgeRuntimeSettings.DefaultModelLoadingTimeoutMs,
                modelLoadingPollIntervalMs = BridgeRuntimeSettings.DefaultModelLoadingPollIntervalMs,
                statusConnectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                statusIoTimeoutMs = BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                idleTimeoutSeconds = editorSettings != null ? editorSettings.ServerIdleShutdownSeconds : 0,
                ownerProcessId = Process.GetCurrentProcess().Id,
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
            currentServiceForceCpu = false;
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
            if (mode == ShutdownMode.StopAndDispose && tryGetEndpointAsync != null)
            {
                try
                {
                    (endpointKnown, endpointHost, endpointPort) = await tryGetEndpointAsync().ConfigureAwait(false);
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
                            await runtimeService.DetachAsync(KimodoBackendType.Bridge, token).ConfigureAwait(false);
                            UnityEngine.Debug.Log("[Kimodo][BridgeShutdown] detached shared runtime service.");
                        }
                        else
                        {
                            await runtimeService.StopAsync(KimodoBackendType.Bridge, token).ConfigureAwait(false);
                            UnityEngine.Debug.Log("[Kimodo][BridgeShutdown] stopped shared runtime service.");
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
                else if (mode == ShutdownMode.StopAndDispose && endpointKnown)
                {
                    try
                    {
                        await BridgeRuntimeControl.TrySendQuitAsync(
                            endpointHost,
                            endpointPort,
                            BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                            BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                            token).ConfigureAwait(false);
                        UnityEngine.Debug.Log($"[Kimodo][BridgeShutdown] sent quit to {endpointHost}:{endpointPort}.");
                    }
                    catch
                    {
                        // ignore
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

        public void Dispose()
        {
            _ = ShutdownAsync(ShutdownMode.StopAndDispose, null, CancellationToken.None);
        }
    }
}
