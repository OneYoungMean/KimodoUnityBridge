using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    // Single source of truth for Constraint Inspector/Edit Window state and
    // panel behavior. AutoSample values are always serialized on the marker.
    internal static class KimodoConstraintEditorState
    {
        internal static bool IsFullBodyAutoSample(SerializedObject so)
        {
            return so?.FindProperty("autoSampleFullBody")?.boolValue == true;
        }

        internal static void DrawConstraintPanels(SerializedObject so)
        {
            if (so == null) return;

            SerializedProperty pose = so.FindProperty("sampleData.characterPose");
            SerializedProperty mask = so.FindProperty("sampleData.mask");
            if (pose == null || mask == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty root = pose.FindPropertyRelative("root");
            SerializedProperty rootPosition = mask.FindPropertyRelative("rootPosition");
            SerializedProperty rootHeading = mask.FindPropertyRelative("rootHeading");
            using (new EditorGUI.DisabledScope(IsFullBodyAutoSample(so)))
            {
                if (DrawTransform(root, "Root Position / Rotation"))
                {
                    // Root is always part of the authoring surface.  Keep the
                    // historical Root2D mask as an internal export detail and
                    // only enable it when the user actually edits this root.
                    if (rootPosition != null) rootPosition.boolValue = true;
                    if (rootHeading != null) rootHeading.boolValue = true;
                }
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty fullBodyEnabled = mask.FindPropertyRelative("muscle");
            EditorGUILayout.PropertyField(fullBodyEnabled, new GUIContent("Muscle Values (FullBody)"));
            DrawAutoSampleField(so, "autoSampleFullBody");
            using (new EditorGUI.DisabledScope(
                fullBodyEnabled == null ||
                !fullBodyEnabled.boolValue ||
                IsFullBodyAutoSample(so)))
            {
                DrawMuscleValues(pose.FindPropertyRelative("muscles"));
            }
            EditorGUILayout.EndVertical();

            DrawEndEffectorPanel(
                pose.FindPropertyRelative("hands.left"),
                mask.FindPropertyRelative("leftHand"),
                "Left Hand Effector");
            DrawEndEffectorPanel(
                pose.FindPropertyRelative("hands.right"),
                mask.FindPropertyRelative("rightHand"),
                "Right Hand Effector");
            DrawEndEffectorPanel(
                pose.FindPropertyRelative("feet.left"),
                mask.FindPropertyRelative("leftFoot"),
                "Left Foot Effector");
            DrawEndEffectorPanel(
                pose.FindPropertyRelative("feet.right"),
                mask.FindPropertyRelative("rightFoot"),
                "Right Foot Effector");
        }

        private static void DrawAutoSampleField(SerializedObject so, string propertyPath)
        {
            SerializedProperty property = so.FindProperty(propertyPath);
            if (property != null)
            {
                EditorGUILayout.PropertyField(property, new GUIContent("Auto Sample"));
            }
        }

        private static void DrawMuscleValues(SerializedProperty muscles)
        {
            KimodoConstraintMuscleValueGUI.Draw(muscles);
        }

        private static void DrawEndEffectorPanel(
            SerializedProperty transform,
            SerializedProperty enabled,
            string label)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.PropertyField(enabled, new GUIContent(label + " Enable"));
            if (DrawTransform(transform, "Target Position / Rotation") && enabled != null)
            {
                enabled.boolValue = true;
            }
            EditorGUILayout.EndVertical();
        }

        private static bool DrawTransform(SerializedProperty transform, string label)
        {
            if (transform == null) return false;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            SerializedProperty position = transform.FindPropertyRelative("t");
            SerializedProperty rotation = transform.FindPropertyRelative("q");
            EditorGUI.BeginChangeCheck();
            if (position != null) EditorGUILayout.PropertyField(position, new GUIContent("Position"));
            if (rotation != null) EditorGUILayout.PropertyField(rotation, new GUIContent("Rotation"));
            bool changed = EditorGUI.EndChangeCheck();
            EditorGUILayout.EndVertical();
            return changed;
        }
    }
}
