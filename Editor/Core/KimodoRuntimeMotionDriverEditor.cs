using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoRuntimeMotionDriver))]
    internal sealed class KimodoRuntimeMotionDriverEditor : UnityEditor.Editor
    {
        private SerializedProperty targetAnimator;
        private SerializedProperty modelName;
        private SerializedProperty textEncoderMode;
        private SerializedProperty forceCpu;
        private SerializedProperty prompt;
        private SerializedProperty generationFrames;
        private SerializedProperty ardyPlaybackDelaySeconds;
        private SerializedProperty ardyHistoryCropSeconds;
        private SerializedProperty ardyFutureCropSeconds;
        private SerializedProperty ardyGenerateSyncIntervalSeconds;
        private SerializedProperty ardyReplanTriggerSeconds;
        private SerializedProperty ardyAutoReplan;
        private SerializedProperty diffusionSteps;
        private SerializedProperty textWeight;
        private SerializedProperty randomSeed;
        private SerializedProperty seed;
        private SerializedProperty drawDebugSkeleton;
        private SerializedProperty debugBoneColor;
        private SerializedProperty debugJointColor;
        private SerializedProperty debugJointSize;
        private SerializedProperty verboseLogging;

        private void OnEnable()
        {
            targetAnimator = serializedObject.FindProperty("targetHumanoidAnimator");
            modelName = serializedObject.FindProperty("modelName");
            textEncoderMode = serializedObject.FindProperty("textEncoderMode");
            forceCpu = serializedObject.FindProperty("forceCpu");
            prompt = serializedObject.FindProperty("defaultPrompt");
            generationFrames = serializedObject.FindProperty("generationFrames");
            ardyPlaybackDelaySeconds = serializedObject.FindProperty("ardyPlaybackDelaySeconds");
            ardyHistoryCropSeconds = serializedObject.FindProperty("ardyHistoryCropSeconds");
            ardyFutureCropSeconds = serializedObject.FindProperty("ardyFutureCropSeconds");
            ardyGenerateSyncIntervalSeconds = serializedObject.FindProperty("ardyGenerateSyncIntervalSeconds");
            ardyReplanTriggerSeconds = serializedObject.FindProperty("ardyReplanTriggerSeconds");
            ardyAutoReplan = serializedObject.FindProperty("ardyAutoReplan");
            diffusionSteps = serializedObject.FindProperty("diffusionSteps");
            textWeight = serializedObject.FindProperty("textWeight");
            randomSeed = serializedObject.FindProperty("randomSeed");
            seed = serializedObject.FindProperty("fixedSeed");
            drawDebugSkeleton = serializedObject.FindProperty("drawDebugSkeleton");
            debugBoneColor = serializedObject.FindProperty("debugSkeletonBoneColor");
            debugJointColor = serializedObject.FindProperty("debugSkeletonJointColor");
            debugJointSize = serializedObject.FindProperty("debugJointMarkerSize");
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
            bool isArdy;
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUILayout.PropertyField(targetAnimator, new GUIContent("Target Animator"));
                isArdy = KimodoGenerationInspectorGui.DrawModelSelector(modelName, diffusionSteps, textEncoderMode);
                KimodoGenerationInspectorGui.DrawTextEncoderMode(textEncoderMode, isArdy);
                KimodoGenerationInspectorGui.DrawResolvedTextEncoderStatus();
                EditorGUILayout.PropertyField(
                    forceCpu,
                    new GUIContent("Force CPU", "Send simulate_free_vram_gb=0 so Kimodo and the text encoder both run on CPU."));
            }
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Target, model, and text encoder mode are applied when entering Play Mode.", EditorStyles.miniLabel);
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
                    ardyPlaybackDelaySeconds,
                    new GUIContent("Playback Delay", "Future lead preserved by ARDY when replanning; default 0.2 seconds."));
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("ARDY Settings (seconds)", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(
                    ardyHistoryCropSeconds,
                    new GUIContent("History Crop", "0 uses the selected profile maximum."));
                EditorGUILayout.PropertyField(
                    ardyFutureCropSeconds,
                    new GUIContent("Future Crop", "0 uses the selected profile maximum."));
                EditorGUILayout.PropertyField(
                    ardyGenerateSyncIntervalSeconds,
                    new GUIContent("Generate Sync Interval", "Minimum interval between automatic Generate cursor updates; minimum 0.2 seconds."));
                EditorGUILayout.PropertyField(
                    ardyReplanTriggerSeconds,
                    new GUIContent("Replan Trigger Threshold", "Send the next Generate when Unity's remaining ARDY timeline reaches this duration."));
                EditorGUILayout.PropertyField(ardyAutoReplan, new GUIContent("Auto Replan"));
            }
            KimodoGenerationInspectorGui.DrawDiffusionSteps(diffusionSteps, modelName);
            KimodoGenerationInspectorGui.DrawTextWeight(textWeight);
            KimodoGenerationInspectorGui.DrawSeed(randomSeed, seed);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawRuntimeControls()
        {
            KimodoRuntimeMotionDriver driver = (KimodoRuntimeMotionDriver)target;
            EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("Apply", "Cancel queued/in-flight generation and generate the next segment with these settings."), GUILayout.Height(30f)))
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
                EditorGUILayout.PropertyField(debugBoneColor, new GUIContent("Bone Color"));
                EditorGUILayout.PropertyField(debugJointColor, new GUIContent("Joint Color"));
                EditorGUILayout.PropertyField(debugJointSize, new GUIContent("Joint Marker Size"));
            }
            EditorGUILayout.PropertyField(verboseLogging, new GUIContent("Verbose Logging"));
            EditorGUILayout.EndVertical();
        }
    }
}
