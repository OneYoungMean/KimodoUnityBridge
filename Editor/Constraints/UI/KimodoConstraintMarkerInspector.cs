using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintMuscleValueGUI
    {
        private static readonly Dictionary<string, bool> FoldoutStates =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly string[] AxisLabels = { "X", "Y", "Z" };
        private const float BoneLabelWidth = 104f;
        private const float AxisLabelWidth = 14f;
        private const float AxisFieldWidth = 58f;

        internal static void Draw(SerializedProperty muscles)
        {
            if (muscles == null || !muscles.isArray) return;

            string foldoutKey = KimodoUnityObjectIdUtility.StableKey(
                muscles.serializedObject.targetObject) + ":" + muscles.propertyPath;
            FoldoutStates.TryGetValue(foldoutKey, out bool expanded);
            expanded = EditorGUILayout.Foldout(expanded, "Muscle Values", true);
            FoldoutStates[foldoutKey] = expanded;
            if (!expanded) return;

            var groups = new Dictionary<int, List<int>>();
            var order = new List<int>();
            for (int index = 0; index < muscles.arraySize; index++)
            {
                int traitIndex = index < KimodoMuscleSampleHumanPoseAdapter.UnityBodyMuscleIndices.Length
                    ? KimodoMuscleSampleHumanPoseAdapter.UnityBodyMuscleIndices[index]
                    : index;
                int bone = traitIndex >= 0 && traitIndex < HumanTrait.MuscleCount
                    ? HumanTrait.BoneFromMuscle(traitIndex)
                    : -1;
                if (!groups.TryGetValue(bone, out List<int> values))
                {
                    values = new List<int>();
                    groups.Add(bone, values);
                    order.Add(bone);
                }
                values.Add(index);
            }

            EditorGUI.indentLevel++;
            for (int groupIndex = 0; groupIndex < order.Count; groupIndex++)
            {
                int bone = order[groupIndex];
                List<int> values = groups[bone];
                EditorGUILayout.BeginHorizontal();
                string boneLabel = bone >= 0 && bone < (int)HumanBodyBones.LastBone
                    ? ObjectNames.NicifyVariableName(((HumanBodyBones)bone).ToString())
                    : "Other";
                EditorGUILayout.LabelField(boneLabel, GUILayout.Width(BoneLabelWidth));
                for (int axis = 0; axis < values.Count; axis++)
                {
                    SerializedProperty value = muscles.GetArrayElementAtIndex(values[axis]);
                    string axisLabel = axis < AxisLabels.Length ? AxisLabels[axis] : $"C{axis + 1}";
                    EditorGUI.BeginChangeCheck();
                    float previousLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = AxisLabelWidth;
                    float edited = EditorGUILayout.FloatField(
                        axisLabel,
                        value.floatValue,
                        GUILayout.Width(AxisFieldWidth));
                    EditorGUIUtility.labelWidth = previousLabelWidth;
                    if (EditorGUI.EndChangeCheck()) value.floatValue = edited;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
        }
    }

internal abstract class KimodoConstraintStandardMarkerEditorBase : UnityEditor.Editor
    {
        protected abstract string TypeLabel { get; }
        protected abstract string TipText { get; }

        private void OnDisable()
        {
            KimodoConstraintMarkerEditorUtility.ClearMarkerPoseCachePreview(target as KimodoConstraintMarker, keepIfOverrideWindowOpen: true);
        }

        public override void OnInspectorGUI()
        {
            KimodoConstraintMarkerEditorUtility.HandleDeleteCommand(target as KimodoConstraintMarker);
            serializedObject.Update();

            EditorGUILayout.HelpBox(TipText, MessageType.Info);
            EditorGUILayout.Space(4f);

            DrawCommonHeader(TypeLabel);
            DrawMarkerTime();

            KimodoConstraintMarker markerTarget = target as KimodoConstraintMarker;
            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(markerTarget);

            // The edit window owns the live preview while it is open. Sampling
            // here would overwrite a just-dragged rig on the next inspector
            // repaint and make the two surfaces disagree.
            if (!windowOpen)
            {
                if (!KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(markerTarget, forceRefresh: false, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
            }

            DrawFields(false);

            KimodoConstraintEditorState.ApplyConstraintPanels(
                serializedObject,
                target as KimodoConstraintMarker);

            KimodoConstraintSelectionPreviewTool.ScheduleRefresh();
        }

        private void DrawCommonHeader(string type)
        {
            EditorGUILayout.LabelField($"Kimodo Constraint Marker ({type})", EditorStyles.boldLabel);
            KimodoConstraintMarkerEditorUtility.DrawEnabledField(serializedObject);
            KimodoConstraintMarkerEditorUtility.DrawOverrideEditButton(serializedObject, target as KimodoConstraintMarker);
            EditorGUILayout.Space(4f);
        }

        private void DrawMarkerTime()
        {
            KimodoConstraintMarkerEditorUtility.DrawMarkerTimeField(serializedObject, target as IMarker);
        }

        protected abstract void DrawFields(bool readOnly);
    }

    [CustomEditor(typeof(KimodoConstraintMarker))]
    internal sealed class KimodoConstraintMarkerEditor : KimodoConstraintStandardMarkerEditorBase
    {
        protected override string TypeLabel => "Constraint";
        protected override string TipText =>
            "Edit one canonical pose: fixed Root Position/Rotation, FullBody muscle values, and four limb effectors. " +
            "Root2D overrides remain command-only internal data.";

        protected override void DrawFields(bool readOnly)
        {
            KimodoConstraintEditorState.DrawConstraintPayload(serializedObject);
        }
    }
}

