using System;
using System.IO;

namespace KimodoBridge.Editor
{
    internal static class KimodoBridgeRuntimeInstallFacade
    {
        internal static string[] SupportedModelNames => KimodoServerRuntimeUtil.SupportedModelNames;

        internal static string GetRuntimeRootPath()
        {
            return KimodoServerRuntimeUtil.GetRuntimeRootPath();
        }

        internal static bool BootstrapRuntimeRootIfMissing()
        {
            return KimodoServerRuntimeUtil.BootstrapRuntimeRootIfMissing();
        }

        internal static string ResolveStartScript(string runtimeRoot)
        {
            return BridgeLauncherResolver.ResolveStartScript(runtimeRoot);
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

        internal static string ResolveStartScriptOrThrow(string runtimeRoot)
        {
            string resolved = ResolveStartScript(runtimeRoot);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                return Path.GetFullPath(resolved);
            }

            throw new FileNotFoundException(
                $"Bridge launcher not found under runtime root: {runtimeRoot}. Expected new pipeline launcher: run_server.bat or run_server.sh.");
        }

        internal static ModelSetupStatus EvaluateModelSetupStatus(
            string runtimeRoot,
            bool highVram,
            string modelName,
            string modelsRootOverride)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return new ModelSetupStatus(false, 0, 0);
            }

            if (!string.IsNullOrWhiteSpace(modelsRootOverride))
            {
                return new ModelSetupStatus(false, 0, 0);
            }

            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(runtimeRoot, highVram, modelName, modelsRootOverride);
            if (points <= 0)
            {
                return new ModelSetupStatus(false, 0, 0);
            }

            int minutes = Math.Max(3, points * 3);
            return new ModelSetupStatus(true, points, minutes);
        }

        internal static bool TryGetModelMissingSetupMinutes(
            string runtimeRoot,
            bool highVram,
            string modelName,
            string modelsRootOverride,
            out int minutes)
        {
            ModelSetupStatus status = EvaluateModelSetupStatus(runtimeRoot, highVram, modelName, modelsRootOverride);
            minutes = 0;
            if (!status.Missing)
            {
                return false;
            }

            minutes = status.EstimatedMinutes;
            return true;
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
