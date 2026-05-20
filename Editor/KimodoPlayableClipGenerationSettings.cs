using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [FilePath("ProjectSettings/KimodoPlayableClipGenerationSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class KimodoPlayableClipGenerationSettings : ScriptableSingleton<KimodoPlayableClipGenerationSettings>
    {
        internal const int MinGeneratedClipsLimit = 1;
        internal const int MaxGeneratedClipsLimit = 1000;
        internal const int DefaultGeneratedClipsLimit = 400;

        [SerializeField] private int maxGeneratedClips = DefaultGeneratedClipsLimit;
        [SerializeField] private bool closeBridgeServerOnEnterPlayMode = true;

        internal int MaxGeneratedClips
        {
            get => Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            set => maxGeneratedClips = Mathf.Clamp(value, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
        }

        internal bool CloseBridgeServerOnEnterPlayMode
        {
            get => closeBridgeServerOnEnterPlayMode;
            set => closeBridgeServerOnEnterPlayMode = value;
        }

        internal void SaveSettings()
        {
            maxGeneratedClips = Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            Save(true);
        }
    }

    internal sealed class KimodoPlayableClipGenerationSettingsProvider : SettingsProvider
    {
        private KimodoPlayableClipGenerationSettingsProvider(string path, SettingsScope scope) : base(path, scope) { }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new KimodoPlayableClipGenerationSettingsProvider("Project/Kimodo Playable Clip", SettingsScope.Project)
            {
                keywords = new System.Collections.Generic.HashSet<string>(new[] { "Kimodo", "Playable", "Clip", "Animation", "Limit", "History" })
            };
        }

        public override void OnGUI(string searchContext)
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            EditorGUILayout.LabelField("Kimodo Playable Clip", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox("Each generation creates a new animation clip. When the limit is exceeded, the oldest generated clips are removed first. Clips referenced by other assets are skipped.", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            int newLimit = EditorGUILayout.IntSlider(
                new GUIContent("Max Generated Clips", "Range: 1-1000"),
                settings.MaxGeneratedClips,
                KimodoPlayableClipGenerationSettings.MinGeneratedClipsLimit,
                KimodoPlayableClipGenerationSettings.MaxGeneratedClipsLimit);

            if (EditorGUI.EndChangeCheck())
            {
                settings.MaxGeneratedClips = newLimit;
                settings.SaveSettings();
            }

            EditorGUI.BeginChangeCheck();
            bool closeOnEnterPlay = EditorGUILayout.Toggle(
                new GUIContent("Close Server On Enter Play Mode", "When enabled, entering Play Mode will close Kimodo bridge server."),
                settings.CloseBridgeServerOnEnterPlayMode);
            if (EditorGUI.EndChangeCheck())
            {
                settings.CloseBridgeServerOnEnterPlayMode = closeOnEnterPlay;
                settings.SaveSettings();
            }

            EditorGUILayout.LabelField($"Current Limit: {settings.MaxGeneratedClips}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"Close On Enter Play Mode: {(settings.CloseBridgeServerOnEnterPlayMode ? "Enabled" : "Disabled")}",
                EditorStyles.miniLabel);
        }
    }
}
