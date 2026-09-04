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
        internal const int MinTimelineConstraintCacheTimeFrames = 1;
        internal const int MaxTimelineConstraintCacheTimeFrames = 900;
        internal const int DefaultTimelineConstraintCacheTimeFrames = 60;
        internal const string DefaultPromptFallback = "a man walk and say hello";
        private const string KeepCpuForceEditorPrefsKey = "KimodoBridge.KeepCpuForceExperimental";

        [SerializeField] private int maxGeneratedClips = DefaultGeneratedClipsLimit;
        [SerializeField] private int timelineConstraintCacheTimeFrames = DefaultTimelineConstraintCacheTimeFrames;
        [SerializeField] private string localModelsPath = string.Empty;
        [SerializeField] private string defaultPrompt = DefaultPromptFallback;
        [SerializeField] private string defaultBridgeModelName = KimodoMotionModelProfiles.DefaultModelName;
        [FormerlySerializedAs("defaultBridgeVramMode")]
        [SerializeField] private KimodoTextEncoderMode defaultTextEncoderMode = KimodoTextEncoderMode.HighPerformance;
        [SerializeField] private bool keepCpuForceExperimental;
        [SerializeField] private bool writeResampledTimelineCacheClips;
        [SerializeField] private bool enableDebugLog;
        [SerializeField] private bool enableDebugMode;
        [SerializeField] private bool enableKimodoStaticGraph;
        [SerializeField] private bool enableSplineExperimental;
        [SerializeField] private bool setupWizardCompleted;
        [SerializeField] private string quickServerPath = string.Empty;
        [SerializeField] private bool autoSyncQuickServer = true;
        [SerializeField, HideInInspector] private bool advancedCurveFilterFoldout = true;

        internal int MaxGeneratedClips
        {
            get => Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            set => maxGeneratedClips = Mathf.Clamp(value, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
        }

        internal int TimelineConstraintCacheTimeFrames
        {
            get => Mathf.Clamp(
                timelineConstraintCacheTimeFrames,
                MinTimelineConstraintCacheTimeFrames,
                MaxTimelineConstraintCacheTimeFrames);
            set => timelineConstraintCacheTimeFrames = Mathf.Clamp(
                value,
                MinTimelineConstraintCacheTimeFrames,
                MaxTimelineConstraintCacheTimeFrames);
        }

        internal string LocalModelsPath
        {
            get => localModelsPath ?? string.Empty;
            set => localModelsPath = value ?? string.Empty;
        }

        internal string DefaultPrompt
        {
            get => string.IsNullOrWhiteSpace(defaultPrompt) ? DefaultPromptFallback : defaultPrompt.Trim();
            set => defaultPrompt = string.IsNullOrWhiteSpace(value) ? DefaultPromptFallback : value.Trim();
        }

        internal string ResolvePrompt(string prompt)
        {
            string trimmed = prompt?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(trimmed) ||
                string.Equals(trimmed, DefaultPromptFallback, System.StringComparison.Ordinal)
                    ? DefaultPrompt
                    : trimmed;
        }

        internal string DefaultBridgeModelName
        {
            get => KimodoMotionModelProfiles.NormalizeName(defaultBridgeModelName);
            set => defaultBridgeModelName = KimodoMotionModelProfiles.NormalizeName(value);
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

        internal bool KeepCpuForceExperimental
        {
            get => keepCpuForceExperimental || EditorPrefs.GetBool(KeepCpuForceEditorPrefsKey, false);
            set
            {
                keepCpuForceExperimental = value;
                EditorPrefs.SetBool(KeepCpuForceEditorPrefsKey, value);
            }
        }

        internal bool WriteResampledTimelineCacheClips
        {
            get => writeResampledTimelineCacheClips;
            set => writeResampledTimelineCacheClips = value;
        }

        internal bool EnableDebugLog
        {
            get => enableDebugLog;
            set => enableDebugLog = value;
        }

        internal bool EnableDebugMode
        {
            get => enableDebugMode;
            set => enableDebugMode = value;
        }

        internal bool EnableKimodoStaticGraph
        {
            get => enableKimodoStaticGraph;
            set => enableKimodoStaticGraph = value;
        }

        internal bool EnableSplineExperimental
        {
            get => enableSplineExperimental;
            set => enableSplineExperimental = value;
        }

        internal static void DebugLog(string message)
        {
            if (instance.EnableDebugLog && !string.IsNullOrWhiteSpace(message))
            {
                Debug.Log(message);
            }
        }

        internal static void DebugLogWarning(string message)
        {
            if (instance.EnableDebugLog && !string.IsNullOrWhiteSpace(message))
            {
                Debug.LogWarning(message);
            }
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

        internal bool AutoSyncQuickServer
        {
            get => autoSyncQuickServer;
            set => autoSyncQuickServer = value;
        }

        internal void SaveSettings()
        {
            bool effectiveKeepCpuForce = KeepCpuForceExperimental;
            maxGeneratedClips = Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            timelineConstraintCacheTimeFrames = Mathf.Clamp(
                timelineConstraintCacheTimeFrames,
                MinTimelineConstraintCacheTimeFrames,
                MaxTimelineConstraintCacheTimeFrames);
            localModelsPath = localModelsPath ?? string.Empty;
            defaultPrompt = DefaultPrompt;
            defaultBridgeModelName = KimodoMotionModelProfiles.NormalizeName(defaultBridgeModelName);
            keepCpuForceExperimental = effectiveKeepCpuForce;
            quickServerPath = quickServerPath?.Trim() ?? string.Empty;
            EditorPrefs.SetBool(KeepCpuForceEditorPrefsKey, effectiveKeepCpuForce);
            Save(true);
        }
    }
}
