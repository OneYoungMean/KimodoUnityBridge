using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoRuntimeMotionDriver))]
    internal sealed class KimodoRuntimeMotionDriverEditor : UnityEditor.Editor
    {
        private SerializedProperty targetAnimator;
        private SerializedProperty modelName;
        private SerializedProperty highVram;
        private SerializedProperty prompt;
        private SerializedProperty generationFrames;
        private SerializedProperty diffusionSteps;
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
            highVram = serializedObject.FindProperty("highVram");
            prompt = serializedObject.FindProperty("defaultPrompt");
            generationFrames = serializedObject.FindProperty("generationFrames");
            diffusionSteps = serializedObject.FindProperty("diffusionSteps");
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
                isArdy = KimodoGenerationInspectorGui.DrawModelSelector(modelName, diffusionSteps);
                DrawVramMode();
            }
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Target, model, and VRAM mode are applied when entering Play Mode.", EditorStyles.miniLabel);
            }
            if (Application.isPlaying)
            {
                KimodoRuntimeMotionDriver driver = (KimodoRuntimeMotionDriver)target;
                EditorGUILayout.LabelField(new GUIContent("Current Prompt", "Prompt used by the currently playing motion."));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextArea(driver.GetCurrentPrompt(out _), GUILayout.Height(60f));
                }
            }
            else
            {
                KimodoGenerationInspectorGui.DrawPrompt(prompt);
            }
            if (!isArdy)
            {
                KimodoGenerationInspectorGui.DrawDuration(
                    generationFrames,
                    1f,
                    10f,
                    "Duration of each generated motion segment.");
            }
            KimodoGenerationInspectorGui.DrawDiffusionSteps(diffusionSteps, modelName);
            KimodoGenerationInspectorGui.DrawSeed(randomSeed, seed);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawVramMode()
        {
            EditorGUI.BeginChangeCheck();
            KimodoBridgeVramMode mode = highVram.boolValue ? KimodoBridgeVramMode.High : KimodoBridgeVramMode.Low;
            mode = (KimodoBridgeVramMode)EditorGUILayout.EnumPopup(
                new GUIContent("VRAM Mode", "Low: quantized text encoder (~4G). High: full Llama+LLM2Vec (~16G)."),
                mode);
            if (EditorGUI.EndChangeCheck())
            {
                highVram.boolValue = mode == KimodoBridgeVramMode.High;
            }

            KimodoGenerationInspectorGui.DrawVramEstimate(highVram.boolValue);
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

                bool promptLocked = EditorGUILayout.ToggleLeft(
                    new GUIContent("Lock Prompt", "Keep using the current motion prompt; unlock to return to idle."),
                    driver.PromptLocked);
                if (promptLocked != driver.PromptLocked)
                {
                    driver.PromptLocked = promptLocked;
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
