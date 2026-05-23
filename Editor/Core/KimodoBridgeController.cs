using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Bridge;
using KimodoUnityMotionTools.Generation;
using UnityEditor;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [InitializeOnLoad]
    internal static class KimodoBridgeController
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

        private static readonly KimodoBridgeGenerationFacade generationFacade = new KimodoBridgeGenerationFacade();
        private static readonly KimodoBridgeServerStateCache serverStateCache = new KimodoBridgeServerStateCache();
        private static bool isRecovering;
        private static int runtimeMaintenanceDepth;

        static KimodoBridgeController()
        {
            EditorApplication.delayCall += RecoverBridgeAfterDomainReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
            EditorApplication.quitting += HandleEditorQuitting;
        }

        internal static bool IsServerRunning => serverStateCache.IsServerRunning;

        internal static ServerStatusSnapshot GetServerStatusSnapshot()
        {
            return serverStateCache.GetSnapshot();
        }

        internal static void RequestServerStateRefresh(bool force)
        {
            serverStateCache.RequestRefresh(force);
        }

        internal static string[] SupportedModelNames => KimodoBridgeRuntimeInstallFacade.SupportedModelNames;

        internal static string GetRuntimeRootPath()
        {
            return KimodoBridgeRuntimeInstallFacade.GetRuntimeRootPath();
        }

        internal static bool EnsureRuntimeRootExists()
        {
            return KimodoBridgeRuntimeInstallFacade.EnsureRuntimeRootExists();
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

                if (!BridgeRuntimeControl.TryReadServerEndpoint(runtimeRoot, out string host, out int port))
                {
                    return;
                }

                bool alive = await BridgeRuntimeControl.IsServerResponsiveAsync(
                    host,
                    port,
                    BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                    BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                    acceptLoading: true,
                    token: CancellationToken.None);
                if (!alive)
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
                    bridgeSettings = new BridgeRuntimeSettings
                    {
                        runtimeRoot = runtimeRoot,
                        launcherPath = launcherPath,
                        modelName = "Kimodo-SOMA-RP-v1",
                        highVram = false,
                        forceSetup = false,
                        modelsRoot = string.Empty,
                        startupTimeoutMs = BridgeRuntimeSettings.DefaultStartupTimeoutMs
                    }
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
            return await generationFacade.StartServerAsync(
                launcherPath,
                modelName,
                highVram,
                kimodoRootPath,
                modelsRoot,
                forceSetup,
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
            return await generationFacade.GenerateBridgeAsync(
                launcherPath,
                modelName,
                highVram,
                kimodoRootPath,
                modelsRoot,
                request,
                progress,
                token);
        }

        internal static async Task CloseServerAsync()
        {
            await generationFacade.CloseServerAsync(async () =>
            {
                string runtimeRoot = GetRuntimeRootPath();
                if (!BridgeRuntimeControl.TryReadServerEndpoint(runtimeRoot, out string host, out int port))
                {
                    return (false, string.Empty, -1);
                }

                return await Task.FromResult((true, host, port));
            });
            serverStateCache.Invalidate(GetRuntimeRootPath);
        }

        private static void HandleBeforeAssemblyReload()
        {
            generationFacade.DetachSharedRuntimeGenerationService();
        }

        private static void HandleEditorQuitting()
        {
            generationFacade.DisposeSharedRuntimeGenerationService();
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
