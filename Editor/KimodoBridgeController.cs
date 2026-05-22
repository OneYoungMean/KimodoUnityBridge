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
            public long SizeBytes;
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

        static KimodoBridgeController()
        {
            EditorApplication.delayCall += RecoverBridgeAfterDomainReload;
        }

        internal static bool IsServerRunning
        {
            get
            {
                string runtimeRoot = GetRuntimeRootPath();
                if (!Directory.Exists(runtimeRoot))
                {
                    return false;
                }

                if (!TryReadServerPort(runtimeRoot, out string host, out int port))
                {
                    return false;
                }

                return IsServerResponsive(host, port);
            }
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

        internal static string ResolveSetupScript(string runtimeRoot)
        {
            return KimodoServerRuntimeUtil.ResolveSetupScript(runtimeRoot);
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

        internal static string ResolveSetupScriptOrThrow(string runtimeRoot)
        {
            string setup = ResolveSetupScript(runtimeRoot);
            if (!string.IsNullOrWhiteSpace(setup) && File.Exists(setup))
            {
                return Path.GetFullPath(setup);
            }

            throw new FileNotFoundException(
                $"Setup script not found under runtime root: {runtimeRoot}. Only new pipeline setup is supported.");
        }

        internal static bool IsSetupRunning(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return false;
            }

            string setupLockPath = Path.Combine(runtimeRoot, ".setup.lock");
            return File.Exists(setupLockPath);
        }

        internal static bool IsRuntimeMaintenanceInProgress => runtimeMaintenanceDepth > 0;

        internal static IDisposable EnterRuntimeMaintenanceScope()
        {
            Interlocked.Increment(ref runtimeMaintenanceDepth);
            return new RuntimeMaintenanceScope();
        }

        internal static bool TryReadServerPort(string runtimeRoot, out string host, out int port)
        {
            return KimodoServerRuntimeUtil.TryReadServerPort(runtimeRoot, out host, out port);
        }

        internal static bool IsServerResponsive(string host, int port)
        {
            return KimodoServerRuntimeUtil.IsServerResponsive(host, port);
        }

        internal static bool TrySendQuit(string host, int port)
        {
            return KimodoServerRuntimeUtil.TrySendQuit(host, port);
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
                    DirectoryPath = item.DirectoryPath,
                    SizeBytes = item.SizeBytes
                });
            }

            return result;
        }

        internal static long GetDirectorySizeSafe(string root)
        {
            return KimodoServerRuntimeUtil.GetDirectorySizeSafe(root);
        }

        internal static string FormatBytes(long bytes)
        {
            return KimodoServerRuntimeUtil.FormatBytes(bytes);
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
            int startupTimeoutMs = 600000)
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
                bridgeSettings = new BridgeRuntimeSettings
                {
                    runtimeRoot = resolvedRuntimeRoot,
                    launcherPath = resolvedLauncherPath,
                    modelName = resolvedModelName,
                    highVram = highVram,
                    forceSetup = forceSetup,
                    modelsRoot = resolvedModelsRoot,
                    startupTimeoutMs = Math.Max(30000, startupTimeoutMs)
                },
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

                if (!IsServerResponsive(host, port))
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
                        highVram = false
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
            float startupTimeoutSeconds = 600f;
            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(kimodoRootPath, highVram, modelName, modelsRoot);
            if (points > 0)
            {
                // Use estimated setup duration as startup wait budget on first/missing-model runs.
                // Keep a 10-minute floor to avoid premature timeout on slower disks/networks.
                int minutes = Math.Max(3, points * 3);
                startupTimeoutSeconds = Math.Max(600f, minutes * 60f);
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
                            TrySendQuit(host, port);
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
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Keep force-quit capability only; do not auto-close on play mode transitions.
        }

        private static void OnEditorQuitting()
        {
            // Keep force-quit capability only; do not auto-close automatically.
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

