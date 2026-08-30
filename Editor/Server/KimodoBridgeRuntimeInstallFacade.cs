using System;
using System.IO;
using UnityEditor;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoBridgeRuntimeInstallFacade
    {
        static KimodoBridgeRuntimeInstallFacade()
        {
            KimodoEditorRuntimeHooks.Register(
                ResolveRuntimeRootOrThrow,
                IsRuntimeSyncRequired,
                TrySyncRuntimeRootIfNeeded,
                ResolveKimodoStaticGraphEnabled,
                BootstrapRuntimeRootIfMissing);
        }

        internal static string[] SupportedModelNames => KimodoServerRuntimeUtil.SupportedModelNames;

        internal static string GetRuntimeRootPath()
        {
            return KimodoServerRuntimeUtil.GetRuntimeRootPath();
        }

        internal static bool ResolveKimodoStaticGraphEnabled()
        {
            return KimodoPlayableClipGenerationSettings.instance.EnableKimodoStaticGraph;
        }

        internal static bool BootstrapRuntimeRootIfMissing()
        {
            return KimodoServerRuntimeUtil.BootstrapRuntimeRootIfMissing();
        }

        internal static bool ReinstallRuntimeRoot()
        {
            return KimodoServerRuntimeUtil.ReinstallRuntimeRoot();
        }

        internal static bool RefreshRuntimeRoot()
        {
            return KimodoServerRuntimeUtil.RefreshRuntimeRoot();
        }

        internal static bool IsRuntimeSyncRequired(string runtimeRoot)
        {
            return KimodoServerRuntimeUtil.IsRuntimeSyncRequired(runtimeRoot);
        }

        internal static bool TrySyncRuntimeRootIfNeeded(string runtimeRoot, out string message)
        {
            return KimodoServerRuntimeUtil.TrySyncRuntimeRootIfNeeded(runtimeRoot, out message);
        }

        internal static string ResolveRuntimeRootOrThrow()
        {
            string runtimeRoot = GetRuntimeRootPath();
            if (!Directory.Exists(runtimeRoot) && !BootstrapRuntimeRootIfMissing())
            {
                throw new DirectoryNotFoundException(
                    $"Bridge runtime root not found and bootstrap failed: {runtimeRoot}");
            }

            return Path.GetFullPath(runtimeRoot);
        }

        internal static System.Collections.Generic.List<ModelDirectoryInfo> QueryDisplayableModelDirectories(string modelsRoot)
        {
            var result = new System.Collections.Generic.List<ModelDirectoryInfo>();
            if (string.IsNullOrWhiteSpace(modelsRoot))
            {
                return result;
            }

            string resolvedRoot;
            try
            {
                resolvedRoot = Path.GetFullPath(modelsRoot.Trim());
            }
            catch
            {
                return result;
            }

            if (!Directory.Exists(resolvedRoot))
            {
                return result;
            }

            string[] dirs = Directory.GetDirectories(resolvedRoot);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                string name = Path.GetFileName(dir);
                if (!ShouldDisplayModelDirectory(name))
                {
                    continue;
                }

                result.Add(new ModelDirectoryInfo(name, dir));
            }

            return result;
        }

        private static bool ShouldDisplayModelDirectory(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return
                name.StartsWith("Kimodo-", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("kimodo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("llama", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("llm2vec", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
