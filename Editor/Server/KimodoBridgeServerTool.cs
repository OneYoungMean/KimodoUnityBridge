using System;
using System.Collections.Generic;
using System.Threading;

namespace KimodoBridge.Editor
{
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

        internal static bool RefreshRuntimeRoot()
        {
            return KimodoBridgeRuntimeInstallFacade.RefreshRuntimeRoot();
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
