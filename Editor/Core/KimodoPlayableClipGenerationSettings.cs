using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [FilePath("ProjectSettings/KimodoPlayableClipGenerationSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class KimodoPlayableClipGenerationSettings : ScriptableSingleton<KimodoPlayableClipGenerationSettings>
    {
        internal const int MinGeneratedClipsLimit = 1;
        internal const int MaxGeneratedClipsLimit = 1000;
        internal const int DefaultGeneratedClipsLimit = 400;
        internal const float MinGenerationTimeoutSeconds = 10f;
        internal const float DefaultGenerationTimeoutSeconds = 600f;
        internal const int MinServerIdleShutdownMinutes = 0;
        internal const int MaxServerIdleShutdownMinutes = 1440;
        internal const int DefaultServerIdleShutdownMinutes = 10;
        private const string AlwaysKeepServerEditorPrefsKey = "KimodoBridge.AlwaysKeepServerExperimental";
        private const string KeepCpuForceEditorPrefsKey = "KimodoBridge.KeepCpuForceExperimental";

        [SerializeField] private int maxGeneratedClips = DefaultGeneratedClipsLimit;
        [SerializeField] private string localModelsPath = string.Empty;
        [SerializeField] private float generationTimeoutSeconds = DefaultGenerationTimeoutSeconds;
        [SerializeField] private bool floatingUiEnabled = true;
        [SerializeField] private bool alwaysKeepServerExperimental;
        [SerializeField] private bool keepCpuForceExperimental;
        [SerializeField] private int serverIdleShutdownMinutes = DefaultServerIdleShutdownMinutes;
        [SerializeField, HideInInspector] private bool advancedCurveFilterFoldout = true;

        internal int MaxGeneratedClips
        {
            get => Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            set => maxGeneratedClips = Mathf.Clamp(value, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
        }

        internal string LocalModelsPath
        {
            get => localModelsPath ?? string.Empty;
            set => localModelsPath = value ?? string.Empty;
        }

        internal bool AdvancedCurveFilterFoldout
        {
            get => advancedCurveFilterFoldout;
            set => advancedCurveFilterFoldout = value;
        }

        internal bool FloatingUiEnabled
        {
            get => floatingUiEnabled;
            set => floatingUiEnabled = value;
        }

        internal bool AlwaysKeepServerExperimental
        {
            get => alwaysKeepServerExperimental || EditorPrefs.GetBool(AlwaysKeepServerEditorPrefsKey, false);
            set
            {
                alwaysKeepServerExperimental = value;
                EditorPrefs.SetBool(AlwaysKeepServerEditorPrefsKey, value);
            }
        }

        internal bool KeepCpuForceExperimental
        {
            get => keepCpuForceExperimental || EditorPrefs.GetBool(KeepCpuForceEditorPrefsKey, false);
            set
            {
                keepCpuForceExperimental = value;
                EditorPrefs.SetBool(KeepCpuForceEditorPrefsKey, value);
            }
        }

        internal float GenerationTimeoutSeconds
        {
            get => Mathf.Max(MinGenerationTimeoutSeconds, generationTimeoutSeconds);
            set => generationTimeoutSeconds = Mathf.Max(MinGenerationTimeoutSeconds, value);
        }

        internal int ServerIdleShutdownMinutes
        {
            get => Mathf.Clamp(serverIdleShutdownMinutes, MinServerIdleShutdownMinutes, MaxServerIdleShutdownMinutes);
            set => serverIdleShutdownMinutes = Mathf.Clamp(value, MinServerIdleShutdownMinutes, MaxServerIdleShutdownMinutes);
        }

        internal int ServerIdleShutdownSeconds
        {
            get
            {
                int minutes = ServerIdleShutdownMinutes;
                if (minutes <= 0)
                {
                    return int.MaxValue;
                }

                return minutes * 60;
            }
        }

        internal void SaveSettings()
        {
            bool effectiveAlwaysKeepServer = AlwaysKeepServerExperimental;
            bool effectiveKeepCpuForce = KeepCpuForceExperimental;
            maxGeneratedClips = Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            localModelsPath = localModelsPath ?? string.Empty;
            generationTimeoutSeconds = Mathf.Max(MinGenerationTimeoutSeconds, generationTimeoutSeconds);
            serverIdleShutdownMinutes = Mathf.Clamp(serverIdleShutdownMinutes, MinServerIdleShutdownMinutes, MaxServerIdleShutdownMinutes);
            alwaysKeepServerExperimental = effectiveAlwaysKeepServer;
            keepCpuForceExperimental = effectiveKeepCpuForce;
            EditorPrefs.SetBool(AlwaysKeepServerEditorPrefsKey, effectiveAlwaysKeepServer);
            EditorPrefs.SetBool(KeepCpuForceEditorPrefsKey, effectiveKeepCpuForce);
            Save(true);
        }
    }
}


