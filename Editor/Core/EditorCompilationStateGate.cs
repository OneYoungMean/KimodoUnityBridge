using System;
using UnityEditor;
using UnityEditor.Compilation;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class EditorCompilationStateGate
    {
        private static int compilingDepth;
        private static int reloadDepth;

        internal static bool IsCompilingOrReloading => compilingDepth > 0 || reloadDepth > 0 || EditorApplication.isCompiling;

        static EditorCompilationStateGate()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;
        }

        private static void OnCompilationStarted(object _)
        {
            compilingDepth++;
        }

        private static void OnCompilationFinished(object _)
        {
            compilingDepth = Math.Max(0, compilingDepth - 1);
        }

        private static void OnBeforeReload()
        {
            reloadDepth++;
        }

        private static void OnAfterReload()
        {
            reloadDepth = Math.Max(0, reloadDepth - 1);
        }
    }
}
