using UnityEditor;

namespace KimodoUnityMotionTools.ProjectEditor.AnimatorTooling
{
    [InitializeOnLoad]
    internal static class KimodoAvatarPreviewLifecycleGuard
    {
        static KimodoAvatarPreviewLifecycleGuard()
        {
            AssemblyReloadEvents.beforeAssemblyReload += CleanupAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                CleanupAll();
            }
        }

        private static void CleanupAll()
        {
            KimodoAvatarPreview.CleanupAllActivePreviews();
        }
    }
}
