using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

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
        private const string KeepCpuForceEditorPrefsKey = "KimodoBridge.KeepCpuForceExperimental";

        [SerializeField] private int maxGeneratedClips = DefaultGeneratedClipsLimit;
        [SerializeField] private string localModelsPath = string.Empty;
        [SerializeField] private string defaultBridgeModelName = KimodoPlayableClip.DefaultBridgeModelName;
        [FormerlySerializedAs("defaultBridgeVramMode")]
        [SerializeField] private KimodoTextEncoderMode defaultTextEncoderMode = KimodoTextEncoderMode.HighPrecision;
        [SerializeField] private float generationTimeoutSeconds = DefaultGenerationTimeoutSeconds;
        [SerializeField] private bool floatingUiEnabled = true;
        [SerializeField] private bool keepCpuForceExperimental;
        [SerializeField] private bool setupWizardCompleted;
        [SerializeField] private string quickServerPath = string.Empty;
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

        internal string DefaultBridgeModelName
        {
            get => KimodoPlayableClip.NormalizeBridgeModelName(defaultBridgeModelName);
            set => defaultBridgeModelName = KimodoPlayableClip.NormalizeBridgeModelName(value);
        }

        internal KimodoTextEncoderMode DefaultTextEncoderMode
        {
            get => defaultTextEncoderMode;
            set => defaultTextEncoderMode = value;
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

        internal bool SetupWizardCompleted
        {
            get => setupWizardCompleted;
            set => setupWizardCompleted = value;
        }

        internal string QuickServerPath
        {
            get => quickServerPath?.Trim() ?? string.Empty;
            set => quickServerPath = value?.Trim() ?? string.Empty;
        }

        internal void SaveSettings()
        {
            bool effectiveKeepCpuForce = KeepCpuForceExperimental;
            maxGeneratedClips = Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            localModelsPath = localModelsPath ?? string.Empty;
            defaultBridgeModelName = KimodoPlayableClip.NormalizeBridgeModelName(defaultBridgeModelName);
            generationTimeoutSeconds = Mathf.Max(MinGenerationTimeoutSeconds, generationTimeoutSeconds);
            keepCpuForceExperimental = effectiveKeepCpuForce;
            quickServerPath = quickServerPath?.Trim() ?? string.Empty;
            EditorPrefs.SetBool(KeepCpuForceEditorPrefsKey, effectiveKeepCpuForce);
            Save(true);
        }
    }
}

