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
                KimodoPlayableClip.NormalizeBridgeModelName(modelName),
                out _);
        }

        internal static bool DrawModelSelector(SerializedProperty modelName, SerializedProperty diffusionSteps)
        {
            string current = KimodoPlayableClip.NormalizeBridgeModelName(modelName.stringValue);
            bool isArdy = IsArdy(current);
            bool selectedArdy = EditorGUILayout.Popup(
                new GUIContent("Base Model", "Select the Kimodo or ARDY model family."),
                isArdy ? 1 : 0,
                BaseModelOptions) == 1;
            if (selectedArdy != isArdy)
            {
                current = selectedArdy
                    ? KimodoMotionModelProfiles.ArdyCoreModelName
                    : KimodoPlayableClip.DefaultBridgeModelName;
                modelName.stringValue = current;
                diffusionSteps.intValue = selectedArdy ? 10 : 100;
            }

            string[] options = GetModelOptions(selectedArdy);
            int index = Mathf.Max(0, Array.IndexOf(options, current));
            modelName.stringValue = options[Mathf.Clamp(
                EditorGUILayout.Popup(new GUIContent("Model", "Model package used for generation."), index, options),
                0,
                options.Length - 1)];
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

        internal static void DrawVram(SerializedProperty vramMode)
        {
            EditorGUILayout.PropertyField(
                vramMode,
                new GUIContent("VRAM Mode", "Low: quantized text encoder (~4G). High: full Llama+LLM2Vec (~16G)."));
            DrawVramEstimate((KimodoBridgeVramMode)vramMode.enumValueIndex == KimodoBridgeVramMode.High);
        }

        internal static void DrawVramEstimate(bool highVram)
        {
            int encoderVramGb = highVram ? 16 : 4;
            EditorGUILayout.HelpBox(
                $"Estimated VRAM for selected mode: ~{2 + encoderVramGb} GB (core 2 GB + encoder {encoderVramGb} GB).",
                MessageType.Info);
        }

        internal static void DrawPrompt(SerializedProperty prompt)
        {
            EditorGUILayout.LabelField(new GUIContent("Prompt", "Natural-language motion prompt sent to Kimodo Bridge."));
            prompt.stringValue = EditorGUILayout.TextArea(prompt.stringValue, GUILayout.Height(60));
        }

        internal static bool DrawDuration(
            SerializedProperty generationFrames,
            float minSeconds,
            float maxSeconds,
            string tooltip)
        {
            int oldFrames = generationFrames.intValue;
            float duration = EditorGUILayout.Slider(
                new GUIContent("Duration (s)", tooltip),
                KimodoInOutConstraintAdapter.FrameCountToDurationSeconds(oldFrames),
                minSeconds,
                maxSeconds);
            generationFrames.intValue = KimodoInOutConstraintAdapter.DurationSecondsToFrameCount(duration);
            return generationFrames.intValue != oldFrames;
        }

        internal static void DrawDiffusionSteps(
            SerializedProperty diffusionSteps,
            SerializedProperty modelName)
        {
            if (KimodoMotionModelProfiles.TryGetArdy(
                    KimodoPlayableClip.NormalizeBridgeModelName(modelName.stringValue),
                    out KimodoMotionModelProfile profile))
            {
                diffusionSteps.intValue = EditorGUILayout.IntSlider(
                    new GUIContent("Diffusion Steps", $"0 uses the model default ({profile.MaxDiffusionSteps})."),
                    Mathf.Clamp(diffusionSteps.intValue, 0, profile.MaxDiffusionSteps),
                    0,
                    profile.MaxDiffusionSteps);
                return;
            }

            diffusionSteps.intValue = Mathf.Clamp(
                EditorGUILayout.IntField(
                    new GUIContent("Diffusion Steps", "Sampling steps for generation. Higher values increase compute time and may improve fidelity."),
                    diffusionSteps.intValue),
                1,
                1000);
        }

        internal static void DrawSeed(SerializedProperty randomSeed, SerializedProperty seed)
        {
            EditorGUILayout.BeginHorizontal();
            randomSeed.boolValue = EditorGUILayout.ToggleLeft(
                new GUIContent("Random", "Use a random seed on each generation run."),
                randomSeed.boolValue,
                GUILayout.Width(110f));
            using (new EditorGUI.DisabledScope(randomSeed.boolValue))
            {
                seed.intValue = EditorGUILayout.IntField(
                    new GUIContent("Seed", "Deterministic seed used when Random is disabled."),
                    seed.intValue);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
