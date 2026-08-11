using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoGenerationInspectorGui
    {
        private static readonly string[] BaseModelOptions = { "Kimodo", "ARDY" };

        internal static bool IsArdy(string modelName)
        {
            return KimodoMotionModelProfiles.TryGetArdy(
                KimodoMotionModelProfiles.NormalizeName(modelName),
                out _);
        }

        internal static bool DrawModelSelector(
            SerializedProperty modelName,
            SerializedProperty diffusionSteps,
            SerializedProperty textEncoderMode = null)
        {
            string current = KimodoMotionModelProfiles.NormalizeName(modelName.stringValue);
            bool isArdy = IsArdy(current);
            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = modelName.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool selectedArdy = EditorGUILayout.Popup(
                new GUIContent("Base Model", "Select the Kimodo or ARDY model family."),
                isArdy ? 1 : 0,
                BaseModelOptions) == 1;
            bool baseModelChanged = EditorGUI.EndChangeCheck();
            EditorGUI.showMixedValue = previousShowMixedValue;
            if (baseModelChanged)
            {
                current = selectedArdy
                    ? KimodoMotionModelProfiles.ArdyCoreModelName
                    : KimodoMotionModelProfiles.DefaultModelName;
                modelName.stringValue = current;
                diffusionSteps.intValue = selectedArdy ? 10 : 100;

                if (textEncoderMode != null)
                {
                    textEncoderMode.enumValueIndex = (int)(selectedArdy
                        ? KimodoTextEncoderMode.HighPrecision
                        : KimodoTextEncoderMode.HighPerformance);
                }
            }

            string[] options = GetModelOptions(selectedArdy);
            int index = Mathf.Max(0, Array.IndexOf(options, current));
            EditorGUI.showMixedValue = modelName.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int selectedIndex = Mathf.Clamp(
                EditorGUILayout.Popup(new GUIContent("Model", "Model package used for generation."), index, options),
                0,
                options.Length - 1);
            if (EditorGUI.EndChangeCheck())
            {
                modelName.stringValue = options[selectedIndex];
            }
            EditorGUI.showMixedValue = previousShowMixedValue;
            return selectedArdy;
        }

        internal static string[] GetModelOptions(bool ardy)
        {
            string[] allOptions = KimodoBridgeServerTool.SupportedModelNames;
            var options = new List<string>();
            for (int i = 0; i < allOptions.Length; i++)
            {
                if (IsArdy(allOptions[i]) == ardy)
                {
                    options.Add(allOptions[i]);
                }
            }

            return options.ToArray();
        }

        internal static void DrawTextEncoderMode(SerializedProperty textEncoderMode, bool ardy = false)
        {
            EditorGUILayout.PropertyField(
                textEncoderMode,
                new GUIContent("Text Encoder Mode", "Choose a text-encoder profile. Runtime platforms are selected automatically."));
            DrawTextEncoderEstimate((KimodoTextEncoderMode)textEncoderMode.enumValueIndex);

            DrawArdyTextEncoderWarning(ardy, (KimodoTextEncoderMode)textEncoderMode.enumValueIndex);
        }

        internal static void DrawArdyTextEncoderWarning(bool ardy, KimodoTextEncoderMode mode)
        {
            if (ardy && mode == KimodoTextEncoderMode.HighPerformance)
            {
                EditorGUILayout.HelpBox(
                    "ARDY recommends High Precision for the best prompt adherence.",
                    MessageType.Warning);
            }
        }

        internal static void DrawTextEncoderEstimate(KimodoTextEncoderMode mode)
        {
            EditorGUILayout.HelpBox(GetRuntimePlacementHint(), MessageType.Info);
        }

        internal static string GetRuntimePlacementHint()
        {
            return Application.platform == RuntimePlatform.OSXEditor
                ? "Runtime target: Kimodo model — Metal/MPS (CPU fallback); Text Encoder — Metal/MPS (CPU fallback)."
                : "Runtime target: Kimodo model — GPU/CPU automatic; Text Encoder — GPU/CPU automatic.";
        }

        internal static void DrawResolvedTextEncoderStatus()
        {
            string status = KimodoBridgeService.Shared.TextEncoderStatusMessage;
            if (!string.IsNullOrWhiteSpace(status))
            {
                EditorGUILayout.HelpBox(status, MessageType.None);
            }
        }

        internal static void DrawPrompt(SerializedProperty prompt)
        {
            EditorGUILayout.LabelField(new GUIContent("Prompt", "Natural-language motion prompt sent to Kimodo Bridge."));
            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = prompt.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            string value = EditorGUILayout.TextArea(prompt.stringValue, GUILayout.Height(60));
            ApplyPromptEdit(prompt, value, EditorGUI.EndChangeCheck());
            EditorGUI.showMixedValue = previousShowMixedValue;
        }

        internal static void ApplyPromptEdit(SerializedProperty prompt, string value, bool changed)
        {
            if (changed)
            {
                prompt.stringValue = value;
            }
        }

        internal static bool DrawDuration(
            SerializedProperty generationFrames,
            float minSeconds,
            float maxSeconds,
            string tooltip)
        {
            int oldFrames = generationFrames.intValue;
            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = generationFrames.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            float duration = EditorGUILayout.Slider(
                new GUIContent("Duration (s)", tooltip),
                KimodoInOutConstraintTools.FrameCountToDurationSeconds(oldFrames),
                minSeconds,
                maxSeconds);
            bool changed = EditorGUI.EndChangeCheck();
            EditorGUI.showMixedValue = previousShowMixedValue;
            if (!changed)
            {
                return false;
            }
            generationFrames.intValue = KimodoInOutConstraintTools.DurationSecondsToFrameCount(duration);
            return true;
        }

        internal static void DrawDiffusionSteps(
            SerializedProperty diffusionSteps,
            SerializedProperty modelName)
        {
            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = diffusionSteps.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int value;
            if (KimodoMotionModelProfiles.TryGetArdy(
                    KimodoMotionModelProfiles.NormalizeName(modelName.stringValue),
                    out KimodoMotionModelProfile profile))
            {
                value = EditorGUILayout.IntSlider(
                    new GUIContent("Diffusion Steps", $"0 uses the model default ({profile.MaxDiffusionSteps})."),
                    Mathf.Clamp(diffusionSteps.intValue, 0, profile.MaxDiffusionSteps),
                    0,
                    profile.MaxDiffusionSteps);
            }
            else
            {
                value = Mathf.Clamp(
                    EditorGUILayout.IntField(
                        new GUIContent("Diffusion Steps", "Sampling steps for generation. Higher values increase compute time and may improve fidelity."),
                        diffusionSteps.intValue),
                    1,
                    1000);
            }
            if (EditorGUI.EndChangeCheck())
            {
                diffusionSteps.intValue = value;
            }
            EditorGUI.showMixedValue = previousShowMixedValue;
        }

        internal static void DrawSeed(SerializedProperty randomSeed, SerializedProperty seed)
        {
            EditorGUILayout.BeginHorizontal();
            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = randomSeed.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool useRandomSeed = EditorGUILayout.ToggleLeft(
                new GUIContent("Random", "Use a random seed on each generation run."),
                randomSeed.boolValue,
                GUILayout.Width(110f));
            if (EditorGUI.EndChangeCheck())
            {
                randomSeed.boolValue = useRandomSeed;
            }

            EditorGUI.showMixedValue = seed.hasMultipleDifferentValues;
            using (new EditorGUI.DisabledScope(useRandomSeed))
            {
                EditorGUI.BeginChangeCheck();
                int value = EditorGUILayout.IntField(
                    new GUIContent("Seed", "Deterministic seed used when Random is disabled."),
                    seed.intValue);
                if (EditorGUI.EndChangeCheck())
                {
                    seed.intValue = value;
                }
            }
            EditorGUI.showMixedValue = previousShowMixedValue;
            EditorGUILayout.EndHorizontal();
        }
    }
}
