using System;
using System.Collections.Generic;
using System.Threading;

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

    public static class KimodoBridgeServerTool
    {
        private static int runtimeMaintenanceDepth;

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

        internal static string ResolveRuntimeRootOrThrow()
        {
            return KimodoBridgeRuntimeInstallFacade.ResolveRuntimeRootOrThrow();
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
