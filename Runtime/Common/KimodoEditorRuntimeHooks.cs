using System;

namespace KimodoBridge
{
    internal delegate bool KimodoTrySyncRuntimeRoot(string runtimeRoot, out string message);

    internal static class KimodoEditorRuntimeHooks
    {
        private static Func<string> resolveRuntimeRoot;
        private static Func<string, bool> isRuntimeSyncRequired;
        private static KimodoTrySyncRuntimeRoot trySyncRuntimeRoot;
        private static Func<bool> resolveStaticGraphEnabled;
        private static Func<string> resolveMocapRepositoryPath;
        private static Func<string> resolveFlowHmrPythonPath;
        private static Func<bool> bootstrapRuntimeRoot;

        internal static void Register(
            Func<string> runtimeRootResolver,
            Func<string, bool> syncRequiredResolver,
            KimodoTrySyncRuntimeRoot runtimeRootSync,
            Func<bool> staticGraphResolver,
            Func<string> mocapRepositoryResolver,
            Func<string> flowHmrPythonResolver,
            Func<bool> runtimeRootBootstrap)
        {
            resolveRuntimeRoot = runtimeRootResolver ?? throw new ArgumentNullException(nameof(runtimeRootResolver));
            isRuntimeSyncRequired = syncRequiredResolver ?? throw new ArgumentNullException(nameof(syncRequiredResolver));
            trySyncRuntimeRoot = runtimeRootSync ?? throw new ArgumentNullException(nameof(runtimeRootSync));
            resolveStaticGraphEnabled = staticGraphResolver ?? throw new ArgumentNullException(nameof(staticGraphResolver));
            resolveMocapRepositoryPath = mocapRepositoryResolver ?? throw new ArgumentNullException(nameof(mocapRepositoryResolver));
            resolveFlowHmrPythonPath = flowHmrPythonResolver ?? throw new ArgumentNullException(nameof(flowHmrPythonResolver));
            bootstrapRuntimeRoot = runtimeRootBootstrap ?? throw new ArgumentNullException(nameof(runtimeRootBootstrap));
        }

        internal static string ResolveRuntimeRootOrThrow() =>
            Require(resolveRuntimeRoot, "runtime root resolver")();

        internal static bool IsRuntimeSyncRequired(string runtimeRoot) =>
            Require(isRuntimeSyncRequired, "runtime sync resolver")(runtimeRoot);

        internal static bool TrySyncRuntimeRoot(string runtimeRoot, out string message)
        {
            if (trySyncRuntimeRoot == null)
            {
                throw Missing("runtime sync handler");
            }

            return trySyncRuntimeRoot(runtimeRoot, out message);
        }

        internal static bool ResolveStaticGraphEnabled() =>
            Require(resolveStaticGraphEnabled, "static graph resolver")();

        internal static string ResolveMocapRepositoryPath() =>
            Require(resolveMocapRepositoryPath, "Mocap repository resolver")();

        internal static string ResolveFlowHmrPythonPath() =>
            Require(resolveFlowHmrPythonPath, "FlowHMR Python resolver")();

        internal static bool TryBootstrapRuntimeRoot() =>
            bootstrapRuntimeRoot?.Invoke() == true;

        private static T Require<T>(T value, string name) where T : class =>
            value ?? throw Missing(name);

        private static InvalidOperationException Missing(string name) =>
            new InvalidOperationException($"Kimodo Editor {name} is not registered.");
    }
}
