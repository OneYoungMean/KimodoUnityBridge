using System;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoAnimatorEditorPanel
    {
        private Vector2 rightScroll;

        public void Draw(
            float windowWidth,
            float windowHeight,
            KimodoAnimatorPreviewPanel previewPanel,
            ref string bridgeModelName,
            ref KimodoTextEncoderMode textEncoderMode,
            ref string motionPrompt,
            float suggestedDurationSeconds,
            ref int diffusionSteps,
            ref float textWeight,
            ref KimodoInOutConstraintMode inOutConstraintMode,
            ref bool isLoop,
            ref bool randomSeed,
            ref int seed,
            bool hasUnsupportedBlendTreeSelection,
            bool isGenerating,
            Action startGenerate,
            Action cancelGenerate,
            Action applyGeneratedResult,
            Action resetGenerated,
            AnimationClip generatedClipForPreview,
            AnimationClip lastSuccessfulGeneratedClipForApply)
        {
            float width = Mathf.Max(420f, windowWidth * 0.46f);
            float panelHeight = Mathf.Max(260f, windowHeight - 92f);
            float applySectionHeight = 86f;
            float scrollHeight = Mathf.Max(160f, panelHeight - applySectionHeight);
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width), GUILayout.Height(panelHeight)))
            {
                using (var scroll = new EditorGUILayout.ScrollViewScope(rightScroll, GUILayout.Height(scrollHeight)))
                {
                    rightScroll = scroll.scrollPosition;

                    if (previewPanel != null)
                    {
                        previewPanel.DrawSelectionInfo();
                    }

                    DrawGeneratePanel(
                        previewPanel != null && previewPanel.HasSelection,
                        ref bridgeModelName,
                        ref textEncoderMode,
                        ref motionPrompt,
                        suggestedDurationSeconds,
                        ref diffusionSteps,
                        ref textWeight,
                        ref inOutConstraintMode,
                        ref isLoop,
                        ref randomSeed,
                        ref seed,
                        hasUnsupportedBlendTreeSelection,
                        isGenerating,
                        startGenerate,
                        cancelGenerate);
                    DrawResultPanel(generatedClipForPreview, resetGenerated);
                }

                GUILayout.FlexibleSpace();
                DrawApplyPanel(
                    previewPanel != null && previewPanel.HasSelection,
                    isGenerating,
                    lastSuccessfulGeneratedClipForApply,
                    applyGeneratedResult);
            }
        }

        private static void DrawGeneratePanel(
            bool hasSelection,
            ref string bridgeModelName,
            ref KimodoTextEncoderMode textEncoderMode,
            ref string motionPrompt,
            float suggestedDurationSeconds,
            ref int diffusionSteps,
            ref float textWeight,
            ref KimodoInOutConstraintMode inOutConstraintMode,
            ref bool isLoop,
            ref bool randomSeed,
            ref int seed,
            bool hasUnsupportedBlendTreeSelection,
            bool isGenerating,
            Action startGenerate,
            Action cancelGenerate)
        {
            EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            DrawBridgePanel(ref bridgeModelName, ref textEncoderMode);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Prompt", EditorStyles.miniBoldLabel);
            motionPrompt = EditorGUILayout.TextArea(motionPrompt ?? string.Empty, GUILayout.Height(60f));

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField(
                    new GUIContent("Source Duration (s)", "Generation length is read from the selected clip or transition."),
                    suggestedDurationSeconds);
            }

            diffusionSteps = Mathf.Clamp(
                EditorGUILayout.IntField(new GUIContent("Diffusion Steps"), diffusionSteps),
                1,
                1000);
            textWeight = KimodoGenerationInspectorGui.DrawTextWeight(textWeight);

            inOutConstraintMode = (KimodoInOutConstraintMode)EditorGUILayout.EnumPopup(
                new GUIContent("InOut Constraint", "None disables boundary constraints. Inside uses the selected clip's own start/end poses. Outside uses transition boundary poses."),
                inOutConstraintMode);
            if (inOutConstraintMode != KimodoInOutConstraintMode.Inside)
            {
                isLoop = false;
            }

            using (new EditorGUI.DisabledScope(inOutConstraintMode != KimodoInOutConstraintMode.Inside))
            {
                isLoop = EditorGUILayout.ToggleLeft(
                    new GUIContent("Is Loop", "Reuse the start fullbody pose axes for the end fullbody constraint while preserving end root motion."),
                    isLoop);
            }

            EditorGUILayout.BeginHorizontal();
            randomSeed = EditorGUILayout.ToggleLeft(new GUIContent("Random"), randomSeed, GUILayout.Width(90f));
            using (new EditorGUI.DisabledScope(randomSeed))
            {
                seed = EditorGUILayout.IntField(new GUIContent("Seed"), seed);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            bool canGenerate = !isGenerating && hasSelection && !hasUnsupportedBlendTreeSelection;
            EditorGUI.BeginDisabledGroup(!canGenerate);
            if (GUILayout.Button("Generate & Bake", GUILayout.Height(30f)))
            {
                startGenerate?.Invoke();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!isGenerating);
            if (GUILayout.Button("Cancel", GUILayout.Height(24f)))
            {
                cancelGenerate?.Invoke();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndVertical();
        }

        private static void DrawBridgePanel(ref string bridgeModelName, ref KimodoTextEncoderMode textEncoderMode)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Kimodo Bridge", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginVertical("box");

            string[] options = KimodoBridgeServerTool.SupportedModelNames;
            if (options != null && options.Length > 0)
            {
                string current = string.IsNullOrWhiteSpace(bridgeModelName) ? options[0] : bridgeModelName.Trim();
                int currentIndex = Array.IndexOf(options, current);
                if (currentIndex < 0)
                {
                    currentIndex = 0;
                }

                int newIndex = EditorGUILayout.Popup(new GUIContent("Bridge Model"), currentIndex, options);
                bridgeModelName = options[Mathf.Clamp(newIndex, 0, options.Length - 1)];
                if (newIndex != currentIndex)
                {
                    textEncoderMode = KimodoGenerationInspectorGui.IsArdy(bridgeModelName)
                        ? KimodoTextEncoderMode.HighPrecision
                        : KimodoTextEncoderMode.HighPerformance;
                }
            }
            else
            {
                bridgeModelName = EditorGUILayout.TextField(new GUIContent("Bridge Model"), bridgeModelName ?? string.Empty);
            }

            textEncoderMode = (KimodoTextEncoderMode)EditorGUILayout.EnumPopup(
                new GUIContent("Text Encoder Mode", "High Performance uses NF4/INT8. High Precision uses FP16. Device placement is automatic."),
                textEncoderMode);
            KimodoGenerationInspectorGui.DrawTextEncoderEstimate(textEncoderMode);
            KimodoGenerationInspectorGui.DrawArdyTextEncoderWarning(
                KimodoGenerationInspectorGui.IsArdy(bridgeModelName),
                textEncoderMode);
            KimodoGenerationInspectorGui.DrawResolvedTextEncoderStatus();

            EditorGUILayout.EndVertical();
        }

        private static void DrawResultPanel(AnimationClip generatedClipForPreview, Action resetGenerated)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(
                    new GUIContent("Generated Clip Preview"),
                    generatedClipForPreview,
                    typeof(AnimationClip),
                    false);
            }

            bool canReset = generatedClipForPreview != null;
            EditorGUI.BeginDisabledGroup(!canReset);
            if (GUILayout.Button("Reset", GUILayout.Width(100f)))
            {
                resetGenerated?.Invoke();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndVertical();
        }

        private static void DrawApplyPanel(
            bool hasSelection,
            bool isGenerating,
            AnimationClip lastSuccessfulGeneratedClipForApply,
            Action applyGeneratedResult)
        {
            EditorGUILayout.LabelField("Apply", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            bool canApply = lastSuccessfulGeneratedClipForApply != null && !isGenerating && hasSelection;
            EditorGUI.BeginDisabledGroup(!canApply);
            if (GUILayout.Button("Apply", GUILayout.Height(28f)))
            {
                applyGeneratedResult?.Invoke();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndVertical();
        }
    }
}
