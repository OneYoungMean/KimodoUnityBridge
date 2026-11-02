using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    [InitializeOnLoad]
    internal static class KimodoSessionRuntimeGuard
    {
        private static readonly Dictionary<GameObject, bool> disabledRoots =
            new Dictionary<GameObject, bool>();

        static KimodoSessionRuntimeGuard()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                DisableLoadedContextRoots();
            }
            else if (state == PlayModeStateChange.EnteredPlayMode)
            {
                DisableLoadedContextRoots();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                foreach (KeyValuePair<GameObject, bool> entry in disabledRoots.ToArray())
                {
                    GameObject root = entry.Key;
                    if (root != null && root.activeSelf != entry.Value)
                    {
                        root.SetActive(entry.Value);
                    }
                }
                disabledRoots.Clear();
                foreach (KimodoDisableOnRuntime guard in FindLoadedGuards())
                {
                    guard.RestoreAfterPlay();
                }
            }
        }

        private static void DisableLoadedContextRoots()
        {
            foreach (GameObject root in FindLoadedContextRoots())
            {
                DisableContextRoot(root);
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

        internal static void DisableContextRoot(GameObject root)
        {
            if (root == null) return;
            if (!disabledRoots.ContainsKey(root))
            {
                disabledRoots[root] = root.activeSelf;
            }
            if (root.activeSelf)
            {
                root.SetActive(false);
            }
        }

        private static KimodoDisableOnRuntime[] FindLoadedGuards()
        {
            return Resources.FindObjectsOfTypeAll<KimodoDisableOnRuntime>()
                .Where(item => item != null && item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded)
                .ToArray();
        }
    }
}
