using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoRuntimeMotionDriver))]
    [CanEditMultipleObjects]
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
        private SerializedProperty ardyAutoHistory;
        private SerializedProperty ardyHistoryWeight;
        private SerializedProperty ardyMaxSpeed;
        private SerializedProperty ardyMaxAcceleration;
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
            ardyAutoHistory = serializedObject.FindProperty("ardyAutoHistory");
            ardyHistoryWeight = serializedObject.FindProperty("ardyHistoryWeight");
            ardyMaxSpeed = serializedObject.FindProperty("ardyMaxSpeed");
            ardyMaxAcceleration = serializedObject.FindProperty("ardyMaxAcceleration");
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
                    ardyAutoHistory,
                    new GUIContent("Auto History", "0-1 m/s = 0.225; 1-10 m/s grows exponentially to 1; above 10 m/s = 1."));
                if (!ardyAutoHistory.hasMultipleDifferentValues && !ardyAutoHistory.boolValue)
                {
                    EditorGUILayout.PropertyField(
                        ardyHistoryWeight,
                        new GUIContent("ARDY History Weight", "0 uses one motion token; 1 uses the maximum history window."));
                }
                EditorGUILayout.PropertyField(ardyMaxSpeed, new GUIContent("Root2D Max Speed"));
                EditorGUILayout.PropertyField(ardyMaxAcceleration, new GUIContent("Root2D Max Acceleration"));
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

            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = driveFootIkTargets.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUILayout.Toggle(label, driveFootIkTargets.boolValue);
            if (EditorGUI.EndChangeCheck())
            {
                ForEachSelectedDriver(driver => driver.FootIkEnabled = enabled);
            }
            EditorGUI.showMixedValue = previousShowMixedValue;
        }

        private void DrawRuntimeControls()
        {
            EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("Apply", "Apply settings now; restart this session when required."), GUILayout.Height(30f)))
                {
                    serializedObject.ApplyModifiedProperties();
                    ForEachSelectedDriver(driver => driver.ApplyGenerationSettings());
                }

                if (GUILayout.Button("Reset Motion", GUILayout.Height(24f)))
                {
                    ForEachSelectedDriver(driver => _ = driver.ResetMotionAsync());
                }
            }

            int runningCount = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is KimodoRuntimeMotionDriver driver && driver.IsRunning)
                {
                    runningCount++;
                }
            }
            EditorGUILayout.LabelField(
                $"Drivers: {targets.Length} selected ({runningCount} running)",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "Bridge status: " + (KimodoBridgeService.Shared.IsConnected ? "connected" : "disconnected"),
                EditorStyles.miniLabel);
            string statusSummary = BuildStatusSummary();
            if (!string.IsNullOrWhiteSpace(statusSummary))
            {
                EditorGUILayout.LabelField(statusSummary, EditorStyles.wordWrappedMiniLabel);
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

        private void ForEachSelectedDriver(Action<KimodoRuntimeMotionDriver> action)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is KimodoRuntimeMotionDriver driver)
                {
                    action(driver);
                }
            }
        }

        private string BuildStatusSummary()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < targets.Length; i++)
            {
                if (!(targets[i] is KimodoRuntimeMotionDriver driver) ||
                    string.IsNullOrWhiteSpace(driver.StatusMessage))
                {
                    continue;
                }

                string status = driver.StatusMessage.Trim();
                counts.TryGetValue(status, out int count);
                counts[status] = count + 1;
            }

            if (counts.Count == 0)
            {
                return string.Empty;
            }

            var summaries = new List<string>(counts.Count);
            foreach (KeyValuePair<string, int> item in counts)
            {
                summaries.Add(item.Value > 1
                    ? $"{item.Key} (x{item.Value})"
                    : item.Key);
            }
            return string.Join(" | ", summaries);
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
