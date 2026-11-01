using System.Linq;
using KimodoBridge;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command
{
    [InitializeOnLoad]
    internal static class KimodoSessionRuntimeGuard
    {
        static KimodoSessionRuntimeGuard()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.delayCall += EnsureLoadedSceneGuards;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            EditorApplication.delayCall += EnsureLoadedSceneGuards;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                command_context.CapturePlayModeSessionRestoreMarker();
                DisableLoadedContextRoots();
            }
            else if (state == PlayModeStateChange.EnteredPlayMode)
            {
                DisableLoadedContextRoots();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                foreach (KimodoDisableOnRuntime guard in FindLoadedGuards())
                {
                    guard.RestoreAfterPlay();
                }
                command_context.RestorePlayModeSessionAfterPlayMode();
            }
        }

        private static void EnsureLoadedSceneGuards()
        {
            foreach (GameObject root in FindLoadedContextRoots())
            {
                KimodoDisableOnRuntime.Ensure(root);
            }
        }

        private static void DisableLoadedContextRoots()
        {
            foreach (GameObject root in FindLoadedContextRoots())
            {
                KimodoDisableOnRuntime.Ensure(root)?.DisableForPlay();
            }

            foreach (KimodoDisableOnRuntime guard in FindLoadedGuards())
            {
                guard.DisableForPlay();
            }
        }

        private static GameObject[] FindLoadedContextRoots()
        {
            return command_context.FindLoadedSessionRoots();
        }

        private static KimodoDisableOnRuntime[] FindLoadedGuards()
        {
            return Resources.FindObjectsOfTypeAll<KimodoDisableOnRuntime>()
                .Where(item => item != null && item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded)
                .ToArray();
        }
    }
}
