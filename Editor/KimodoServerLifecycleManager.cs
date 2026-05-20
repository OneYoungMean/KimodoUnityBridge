using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using KimodoUnityMotionTools;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [InitializeOnLoad]
    internal static class KimodoServerLifecycleManager
    {
        internal sealed class InstalledModelInfo
        {
            public string Name;
            public string DirectoryPath;
            public long SizeBytes;
        }

        private static KimodoBridgeClient sharedBridgeClient;
        private static bool isClosing;

        static KimodoServerLifecycleManager()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += OnEditorQuitting;
        }

        internal static bool IsServerRunning
        {
            get
            {
                return sharedBridgeClient != null && sharedBridgeClient.IsRunning;
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

            string setupLockPath = Path.Combine(runtimeRoot, ".setup_new.lock");
            return File.Exists(setupLockPath);
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

        internal static bool IsModelInstalled(string runtimeRoot, string modelName)
        {
            return KimodoServerRuntimeUtil.IsSelectedBridgeModelInstalled(runtimeRoot, modelName);
        }

        internal static bool TryGetModelMissingSetupMinutes(string runtimeRoot, bool highVram, string modelName, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return false;
            }

            if (KimodoServerRuntimeUtil.IsSelectedBridgeModelInstalled(runtimeRoot, modelName))
            {
                return false;
            }

            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(runtimeRoot, highVram, modelName);
            minutes = Math.Max(3, points * 3);
            return true;
        }

        private static KimodoBridgeClient GetOrCreateClient()
        {
            if (sharedBridgeClient == null)
            {
                sharedBridgeClient = new KimodoBridgeClient();
            }

            return sharedBridgeClient;
        }

        internal static async Task<string> StartServerAsync(
            string launcherPath,
            string modelName,
            bool highVram,
            string kimodoRootPath,
            Action<string> progress,
            CancellationToken token)
        {
            float startupTimeoutSeconds = 600f;
            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(kimodoRootPath, highVram, modelName);
            if (points > 0)
            {
                // Use estimated setup duration as startup wait budget on first/missing-model runs.
                // Keep a 10-minute floor to avoid premature timeout on slower disks/networks.
                int minutes = Math.Max(3, points * 3);
                startupTimeoutSeconds = Math.Max(600f, minutes * 60f);
            }

            KimodoBridgeClient bridge = GetOrCreateClient();
            return await bridge.StartAsync(
                launcherPath,
                modelName,
                highVram,
                kimodoRootPath,
                startupTimeoutSeconds,
                progress,
                token);
        }

        internal static async Task<string> GenerateAsync(
            string prompt,
            float durationSeconds,
            int? seed,
            int diffusionSteps,
            string constraintsJson,
            Action<string> progress,
            CancellationToken token)
        {
            KimodoBridgeClient bridge = GetOrCreateClient();
            return await bridge.GenerateAsync(
                prompt,
                durationSeconds,
                seed,
                diffusionSteps,
                constraintsJson,
                progress,
                token);
        }

        internal static ProcessStartInfo BuildScriptStartInfo(string scriptPath, string arguments, bool keepWindowOpen, bool useShellExecute)
        {
            string fullPath = Path.GetFullPath(scriptPath);
            string ext = Path.GetExtension(fullPath)?.ToLowerInvariant() ?? string.Empty;
            string workingDir = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            string safeArgs = arguments ?? string.Empty;

            if (ext == ".bat" || ext == ".cmd")
            {
                string mode = keepWindowOpen ? "/k" : "/c";
                return new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s {mode} \"\"{fullPath}\"{safeArgs}\"",
                    UseShellExecute = useShellExecute,
                    WorkingDirectory = workingDir
                };
            }

            if (ext == ".sh")
            {
                return new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"\"{fullPath}\"{safeArgs}",
                    UseShellExecute = useShellExecute,
                    WorkingDirectory = workingDir
                };
            }

            throw new NotSupportedException($"Unsupported script extension: {ext}");
        }

        internal static int RunScriptBlocking(string scriptPath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                return -1;
            }

            ProcessStartInfo psi = BuildScriptStartInfo(scriptPath, arguments, keepWindowOpen: false, useShellExecute: false);
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            psi.CreateNoWindow = false;

            using Process proc = Process.Start(psi);
            if (proc == null)
            {
                return -1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
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
                if (sharedBridgeClient == null)
                {
                    return;
                }

                KimodoBridgeClient bridge = sharedBridgeClient;
                sharedBridgeClient = null;
                try
                {
                    await bridge.KillServerTreeAsync(CancellationToken.None);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    bridge.Dispose();
                }
            }
            finally
            {
                isClosing = false;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }

            bool closeOnEnterPlay = true;
            try
            {
                closeOnEnterPlay = KimodoPlayableClipGenerationSettings.instance.CloseBridgeServerOnEnterPlayMode;
            }
            catch
            {
                closeOnEnterPlay = true;
            }

            if (closeOnEnterPlay)
            {
                _ = CloseServerAsync();
            }
        }

        private static void OnEditorQuitting()
        {
            _ = CloseServerAsync();
        }
    }
}
