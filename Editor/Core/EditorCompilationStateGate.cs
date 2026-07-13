using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class EditorCompilationStateGate
    {
        private static int compilingDepth;
        private static int reloadDepth;
        private static readonly List<IDisposable> PendingDisposables = new List<IDisposable>();

        internal static event Action<bool> StateChanged;

        internal static bool IsCompilingOrReloading => compilingDepth > 0 || reloadDepth > 0 || EditorApplication.isCompiling;

        static EditorCompilationStateGate()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        internal static void RegisterDisposable(IDisposable disposable)
        {
            if (disposable == null || PendingDisposables.Contains(disposable))
            {
                return;
            }

            PendingDisposables.Add(disposable);
        }

        internal static void UnregisterDisposable(IDisposable disposable)
        {
            if (disposable == null)
            {
                return;
            }

            PendingDisposables.Remove(disposable);
        }

        internal static void FlushDisposables()
        {
            for (int i = PendingDisposables.Count - 1; i >= 0; i--)
            {
                IDisposable disposable = PendingDisposables[i];
                PendingDisposables.RemoveAt(i);
                try
                {
                    disposable?.Dispose();
                }
                catch
                {
                }
            }
        }

        private static void OnCompilationStarted(object _)
        {
            FlushDisposables();
            compilingDepth++;
            //UnityEngine.Debug.Log($"[Kimodo][CompileGate] compilation started depth={compilingDepth}");
            StateChanged?.Invoke(true);
        }

        private static void OnCompilationFinished(object _)
        {
            compilingDepth = Math.Max(0, compilingDepth - 1);
            //UnityEngine.Debug.Log($"[Kimodo][CompileGate] compilation finished depth={compilingDepth}, active={active}");
        }

        private static void OnBeforeReload()
        {
            FlushDisposables();
            reloadDepth++;
            //UnityEngine.Debug.Log($"[Kimodo][CompileGate] before reload depth={reloadDepth}");
        }

        private static void OnAfterReload()
        {
            reloadDepth = Math.Max(0, reloadDepth - 1);
            //UnityEngine.Debug.Log($"[Kimodo][CompileGate] after reload depth={reloadDepth}, active={active}");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode ||
                change == PlayModeStateChange.ExitingPlayMode)
            {
                FlushDisposables();
            }
        }
    }
}
