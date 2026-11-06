using KimodoBridge;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    [InitializeOnLoad]
    internal static class KimodoSessionRuntimeGuard
    {
        static KimodoSessionRuntimeGuard()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                EnsureLoadedContextGuards();
            }
        }

        private static void EnsureLoadedContextGuards()
        {
            foreach (GameObject root in FindLoadedContextRoots())
            {
                KimodoDisableOnRuntime.Ensure(root);
            }
        }

        private static GameObject[] FindLoadedContextRoots()
        {
            return command_context.FindLoadedSessionRoots();
        }
    }
}
