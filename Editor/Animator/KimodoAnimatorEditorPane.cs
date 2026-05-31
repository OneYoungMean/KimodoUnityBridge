using System;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.AnimatorTooling
{
    internal sealed class KimodoAnimatorEditorPane
    {
        private Vector2 rightScroll;

        public void Draw(
            float windowWidth,
            SerializedObject clipSo,
            KimodoAnimatorPreviewPane previewPane,
            bool isGenerating,
            Action startGenerate,
            Action cancelGenerate,
            Action applyGeneratedResult,
            Action resetGenerated,
            AnimationClip generatedClipForPreview,
            AnimationClip lastSuccessfulGeneratedClipForApply)
        {
            float width = Mathf.Max(420f, windowWidth * 0.46f);
            using (var scroll = new EditorGUILayout.ScrollViewScope(rightScroll, GUILayout.Width(width)))
            {
                rightScroll = scroll.scrollPosition;
                clipSo.UpdateIfRequiredOrScript();

                previewPane.DrawSelectionInfo();
                DrawGeneratePanel(clipSo, isGenerating, previewPane.HasSelection, startGenerate, cancelGenerate);
                DrawBakePanel(clipSo);
                DrawGeneratedPanel(generatedClipForPreview, resetGenerated);
                DrawAnimationClipPanel(clipSo);
                DrawApplyPanel(isGenerating, previewPane.HasSelection, lastSuccessfulGeneratedClipForApply, applyGeneratedResult);

                clipSo.ApplyModifiedProperties();
            }
        }

        private static void DrawGeneratePanel(SerializedObject clipSo, bool isGenerating, bool hasSelection, Action startGenerate, Action cancelGenerate)
        {
            EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp(clipSo, "generationBackend", "Backend");
            DrawKimodoBridgePanel(clipSo);
            DrawComfyUiPanel(clipSo);
            DrawProp(clipSo, "motionPrompt", "Prompt", true, 60f);
            DrawProp(clipSo, "generationFrames", "Duration (frames)");
            DrawProp(clipSo, "diffusionSteps", "Diffusion Steps");
            DrawProp(clipSo, "randomSeed", "Random");
            DrawProp(clipSo, "seed", "Seed");
            DrawProp(clipSo, "enableInbetweenInterpolation", "In-between Interpolation");
            DrawProp(clipSo, "showConstraint", "Show Constraint");

            bool canGenerate = !isGenerating && hasSelection;
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

        private static void DrawKimodoBridgePanel(SerializedObject clipSo)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Kimodo Bridge", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp(clipSo, "bridgeModelName", "Bridge Model");
            DrawProp(clipSo, "bridgeVramMode", "VRAM Mode");
            EditorGUILayout.EndVertical();
        }

        private static void DrawComfyUiPanel(SerializedObject clipSo)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("ComfyUI", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp(clipSo, "workflowJsonAsset", "Workflow JSON Asset");
            DrawProp(clipSo, "comfyUiHost", "ComfyUI Host");
            DrawProp(clipSo, "comfyUiPort", "ComfyUI Port");
            EditorGUILayout.EndVertical();
        }

        private static void DrawBakePanel(SerializedObject clipSo)
        {
            EditorGUILayout.LabelField("Animation Bake", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp(clipSo, "autoRetargetOnBinding", "Auto Retarget On Binding");
            DrawProp(clipSo, "customRetargetAvatar", "Custom Avatar");
            DrawProp(clipSo, "curveFilterOptions", "Curve Filter Options", true);
            EditorGUILayout.EndVertical();
        }

        private static void DrawGeneratedPanel(AnimationClip generatedClipForPreview, Action resetGenerated)
        {
            EditorGUILayout.LabelField("Generated", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Generated Clip Preview", generatedClipForPreview, typeof(AnimationClip), false);
            }
            if (GUILayout.Button("Reset", GUILayout.Width(100)))
            {
                resetGenerated?.Invoke();
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawAnimationClipPanel(SerializedObject clipSo)
        {
            EditorGUILayout.LabelField("Animation Clip", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp(clipSo, "m_Clip", "Clip");
            DrawProp(clipSo, "m_ApplyFootIK", "Foot IK");
            DrawProp(clipSo, "m_Loop", "Loop");
            EditorGUILayout.EndVertical();
        }

        private static void DrawApplyPanel(bool isGenerating, bool hasSelection, AnimationClip lastSuccessfulGeneratedClipForApply, Action applyGeneratedResult)
        {
            EditorGUILayout.Space(4f);
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

        private static void DrawProp(SerializedObject clipSo, string propertyName, string label, bool multiLine = false, float height = 18f)
        {
            SerializedProperty p = clipSo.FindProperty(propertyName);
            if (p == null)
            {
                return;
            }
            if (multiLine && p.propertyType == SerializedPropertyType.String)
            {
                EditorGUILayout.LabelField(label);
                p.stringValue = EditorGUILayout.TextArea(p.stringValue ?? string.Empty, GUILayout.Height(height));
                return;
            }
            EditorGUILayout.PropertyField(p, new GUIContent(label), multiLine);
        }
    }
}
