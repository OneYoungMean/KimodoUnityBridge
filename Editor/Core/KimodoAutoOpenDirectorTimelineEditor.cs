using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoAutoOpenDirectorTimelineEditor
    {
        static KimodoAutoOpenDirectorTimelineEditor()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += () => OpenTimeline(SceneManager.GetActiveScene());
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            EditorApplication.delayCall += () => OpenTimeline(scene);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                EditorApplication.delayCall += () => OpenTimeline(SceneManager.GetActiveScene());
            }
        }

        private static void OpenTimeline(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                KimodoAutoOpenDirectorTimeline[] openers =
                    root.GetComponentsInChildren<KimodoAutoOpenDirectorTimeline>(true);
                foreach (KimodoAutoOpenDirectorTimeline opener in openers)
                {
                    PlayableDirector director = opener != null && opener.isActiveAndEnabled
                        ? opener.Director
                        : null;
                    if (director == null || director.playableAsset is not TimelineAsset)
                    {
                        continue;
                    }

                    TimelineEditorWindow window = TimelineEditor.GetOrCreateWindow();
                    window.Focus();

                    // Let TimelineWindow process and clear the previously active scene first.
                    EditorApplication.delayCall += () => ShowTimeline(window, director);
                    return;
                }
            }
        }

        private static void ShowTimeline(TimelineEditorWindow window, PlayableDirector director)
        {
            if (window == null || director == null || director.playableAsset is not TimelineAsset)
            {
                return;
            }

            window.SetTimeline(director);
            window.locked = true;
            window.Focus();
            TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }
    }
}
