using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintMuscleValueGUI
    {
        private static readonly Dictionary<string, bool> FoldoutStates =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private const float MuscleLabelWidth = 190f;

        internal static void Draw(SerializedProperty muscles)
        {
            if (muscles == null || !muscles.isArray) return;

            string foldoutKey = KimodoUnityObjectIdUtility.StableKey(
                muscles.serializedObject.targetObject) + ":" + muscles.propertyPath;
            bool expanded = FoldoutStates.TryGetValue(foldoutKey, out bool savedExpanded) && savedExpanded;
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
                string boneLabel = bone >= 0 && bone < (int)HumanBodyBones.LastBone
                    ? ObjectNames.NicifyVariableName(((HumanBodyBones)bone).ToString())
                    : "Other";
                for (int axis = 0; axis < values.Count; axis++)
                {
                    int canonicalIndex = values[axis];
                    int traitIndex = canonicalIndex < KimodoMuscleSampleHumanPoseAdapter.UnityBodyMuscleIndices.Length
                        ? KimodoMuscleSampleHumanPoseAdapter.UnityBodyMuscleIndices[canonicalIndex]
                        : -1;
                    string muscleLabel = traitIndex >= 0 && traitIndex < HumanTrait.MuscleName.Length
                        ? HumanTrait.MuscleName[traitIndex]
                        : $"{boneLabel} Muscle {canonicalIndex}";
                    SerializedProperty value = muscles.GetArrayElementAtIndex(canonicalIndex);
                    EditorGUI.BeginChangeCheck();
                    float previousLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = MuscleLabelWidth;
                    float edited = EditorGUILayout.FloatField(
                        muscleLabel,
                        value.floatValue,
                        GUILayout.ExpandWidth(true));
                    EditorGUIUtility.labelWidth = previousLabelWidth;
                    if (EditorGUI.EndChangeCheck()) value.floatValue = edited;
                }
            }
            EditorGUI.indentLevel--;
        }
    }
}
