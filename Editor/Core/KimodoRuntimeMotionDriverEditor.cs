using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoRuntimeMotionDriver))]
    internal sealed class KimodoRuntimeMotionDriverEditor : UnityEditor.Editor
    {
        private SerializedProperty targetAnimators;
        private SerializedProperty modelsRoot;
        private SerializedProperty modelName;
        private SerializedProperty textEncoderMode;
        private SerializedProperty forceCpu;
        private SerializedProperty prompt;
        private SerializedProperty generationFrames;
        private SerializedProperty ardyPlaybackReserveSeconds;
        private SerializedProperty ardyAdaptivePlaybackReserve;
        private SerializedProperty ardyAutoHistory;
        private SerializedProperty ardyHistoryWeight;
        private SerializedProperty diffusionSteps;
        private SerializedProperty randomSeed;
        private SerializedProperty seed;
        private SerializedProperty driveFootIkTargets;
        private SerializedProperty drawDebugSkeleton;
        private SerializedProperty verboseLogging;

        private void OnEnable()
        {
            targetAnimators = serializedObject.FindProperty("targetHumanoidAnimators");
            modelsRoot = serializedObject.FindProperty("modelsRoot");
            modelName = serializedObject.FindProperty("modelName");
            textEncoderMode = serializedObject.FindProperty("textEncoderMode");
            forceCpu = serializedObject.FindProperty("forceCpu");
            prompt = serializedObject.FindProperty("defaultPrompt");
            generationFrames = serializedObject.FindProperty("generationFrames");
            ardyPlaybackReserveSeconds = serializedObject.FindProperty("ardyPlaybackReserveSeconds");
            ardyAdaptivePlaybackReserve = serializedObject.FindProperty("ardyAdaptivePlaybackReserve");
            ardyAutoHistory = serializedObject.FindProperty("ardyAutoHistory");
            ardyHistoryWeight = serializedObject.FindProperty("ardyHistoryWeight");
            diffusionSteps = serializedObject.FindProperty("diffusionSteps");
            randomSeed = serializedObject.FindProperty("randomSeed");
            seed = serializedObject.FindProperty("fixedSeed");
            driveFootIkTargets = serializedObject.FindProperty("driveFootIkTargets");
            drawDebugSkeleton = serializedObject.FindProperty("drawDebugSkeleton");
            verboseLogging = serializedObject.FindProperty("verboseLogging");
        }

        public override void OnInspectorGUI()
        {
            if (!Application.isPlaying || !serializedObject.hasModifiedProperties)
            {
                serializedObject.UpdateIfRequiredOrScript();
            }
            DrawGenerationSection();
            DrawRuntimeControls();
            DrawDebugSection();
            if (!Application.isPlaying)
            {
                serializedObject.ApplyModifiedProperties();
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        private void DrawGenerationSection()
        {
            EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.PropertyField(
                targetAnimators,
                new GUIContent("Target Animators", "The first valid Animator defines world-space constraints; all targets receive the same motion."),
                includeChildren: true);
            bool isArdy = KimodoGenerationInspectorGui.DrawModelSelector(modelName, diffusionSteps, textEncoderMode);
            EditorGUILayout.PropertyField(
                modelsRoot,
                new GUIContent("Models Root", "Optional model asset root. Empty uses the server default."));
            KimodoGenerationInspectorGui.DrawTextEncoderMode(textEncoderMode, isArdy);
            KimodoGenerationInspectorGui.DrawResolvedTextEncoderStatus();
            EditorGUILayout.PropertyField(
                forceCpu,
                new GUIContent("Force CPU", "Send simulate_free_vram_gb=0 so Kimodo and the text encoder both run on CPU."));
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField(
                    "Target or runtime changes restart this driver's generation session when applied.",
                    EditorStyles.miniLabel);
            }
            KimodoGenerationInspectorGui.DrawPrompt(prompt);
            if (!isArdy)
            {
                KimodoGenerationInspectorGui.DrawDuration(
                    generationFrames,
                    1f,
                    10f,
                    "Duration of each generated motion segment.");
            }
            else
            {
                EditorGUILayout.PropertyField(
                    ardyPlaybackReserveSeconds,
                    new GUIContent("Playback Reserve", "Request more motion when this much playable ARDY animation remains; default 1 second."));
                EditorGUILayout.PropertyField(
                    ardyAdaptivePlaybackReserve,
                    new GUIContent("Adaptive Playback Reserve", "Let the backend adapt the reserve from measured response time."));
                EditorGUILayout.PropertyField(
                    ardyAutoHistory,
                    new GUIContent("Auto History", "Adapt the history window from upcoming motion constraints."));
                if (!ardyAutoHistory.hasMultipleDifferentValues && !ardyAutoHistory.boolValue)
                {
                    EditorGUILayout.PropertyField(
                        ardyHistoryWeight,
                        new GUIContent("ARDY History Weight", "0 uses one motion token; 1 uses the maximum history window."));
                }
            }
            KimodoGenerationInspectorGui.DrawDiffusionSteps(diffusionSteps, modelName);
            KimodoGenerationInspectorGui.DrawSeed(randomSeed, seed);
            DrawFootIkSetting();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawFootIkSetting()
        {
            var label = new GUIContent(
                "Foot IK",
                "Enable foot target driving and runtime two-bone leg IK correction.");
            if (!Application.isPlaying)
            {
                EditorGUILayout.PropertyField(driveFootIkTargets, label);
                return;
            }

            KimodoRuntimeMotionDriver driver = (KimodoRuntimeMotionDriver)target;
            driver.FootIkEnabled = EditorGUILayout.Toggle(label, driver.FootIkEnabled);
        }

        private void DrawRuntimeControls()
        {
            KimodoRuntimeMotionDriver driver = (KimodoRuntimeMotionDriver)target;
            EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("Apply", "Apply settings now; restart this session when required."), GUILayout.Height(30f)))
                {
                    serializedObject.ApplyModifiedProperties();
                    driver.ApplyGenerationSettings();
                }

                if (GUILayout.Button("Reset Motion", GUILayout.Height(24f)))
                {
                    _ = driver.ResetMotionAsync();
                }
            }

            EditorGUILayout.LabelField("Driver status: " + (driver.IsRunning ? "running" : "stopped"), EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "Bridge status: " + (KimodoBridgeService.Shared.IsConnected ? "connected" : "disconnected"),
                EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(driver.StatusMessage))
            {
                EditorGUILayout.LabelField(driver.StatusMessage, EditorStyles.wordWrappedMiniLabel);
            }
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Runtime controls are available in Play Mode.", MessageType.Info);
            }
            else if (serializedObject.hasModifiedProperties)
            {
                EditorGUILayout.HelpBox("Inspector changes are staged. Click Apply to use them.", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawDebugSection()
        {
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.PropertyField(drawDebugSkeleton, new GUIContent("Draw Debug Skeleton"));
            if (drawDebugSkeleton.boolValue)
            {
                EditorGUILayout.LabelField(
                    "Editor-only profile model driven by the current source pose.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.PropertyField(verboseLogging, new GUIContent("Verbose Logging"));
            EditorGUILayout.EndVertical();
        }
    }
}
