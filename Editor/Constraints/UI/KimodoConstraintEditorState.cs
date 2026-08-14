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

        internal static bool IsRoot2DAutoSample(SerializedObject so)
        {
            return so?.FindProperty("autoSampleRoot2D")?.boolValue == true;
        }

        internal static void DrawConstraintPanels(SerializedObject so)
        {
            if (so == null) return;

            SerializedProperty pose = so.FindProperty("sampleData.characterPose");
            SerializedProperty mask = so.FindProperty("sampleData.mask");
            if (pose == null || mask == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty fullBodyEnabled = mask.FindPropertyRelative("muscle");
            EditorGUILayout.PropertyField(fullBodyEnabled, new GUIContent("FullBody Constraint Enable"));
            DrawAutoSampleField(so, "autoSampleFullBody");
            using (new EditorGUI.DisabledScope(
                fullBodyEnabled == null ||
                !fullBodyEnabled.boolValue ||
                IsFullBodyAutoSample(so)))
            {
                DrawMuscleValues(pose.FindPropertyRelative("muscles"));
                DrawTransform(pose.FindPropertyRelative("root"), "Original Root Bone");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty rootPosition = mask.FindPropertyRelative("rootPosition");
            SerializedProperty rootHeading = mask.FindPropertyRelative("rootHeading");
            EditorGUILayout.PropertyField(rootPosition, new GUIContent("Root2D Constraint Enable"));
            DrawAutoSampleField(so, "autoSampleRoot2D");
            using (new EditorGUI.DisabledScope(
                rootPosition == null ||
                !rootPosition.boolValue ||
                IsRoot2DAutoSample(so)))
            {
                SerializedProperty root2D = so.FindProperty("sampleData.root2DOverride");
                SerializedProperty position = root2D?.FindPropertyRelative("t");
                if (position != null)
                {
                    Vector3 value = position.vector3Value;
                    EditorGUI.BeginChangeCheck();
                    Vector2 xz = EditorGUILayout.Vector2Field("Root2D XZ", new Vector2(value.x, value.z));
                    if (EditorGUI.EndChangeCheck())
                    {
                        position.vector3Value = new Vector3(xz.x, 0f, xz.y);
                    }
                }
            }

            EditorGUILayout.PropertyField(rootHeading, new GUIContent("Enable Root Heading"));
            using (new EditorGUI.DisabledScope(
                rootHeading == null ||
                !rootHeading.boolValue ||
                IsRoot2DAutoSample(so)))
            {
                SerializedProperty rotation = so.FindProperty("sampleData.root2DOverride.q");
                if (rotation != null)
                {
                    EditorGUI.BeginChangeCheck();
                    float yaw = EditorGUILayout.FloatField("Root Heading Y", rotation.quaternionValue.eulerAngles.y);
                    if (EditorGUI.EndChangeCheck())
                    {
                        rotation.quaternionValue = Quaternion.Euler(0f, yaw, 0f);
                    }
                }
            }
            EditorGUILayout.EndVertical();

            DrawEndEffectorPanel(
                pose.FindPropertyRelative("hands.left"),
                mask.FindPropertyRelative("leftHand"),
                "Left Hand Constraint");
            DrawEndEffectorPanel(
                pose.FindPropertyRelative("hands.right"),
                mask.FindPropertyRelative("rightHand"),
                "Right Hand Constraint");
            DrawEndEffectorPanel(
                pose.FindPropertyRelative("feet.left"),
                mask.FindPropertyRelative("leftFoot"),
                "Left Foot Constraint");
            DrawEndEffectorPanel(
                pose.FindPropertyRelative("feet.right"),
                mask.FindPropertyRelative("rightFoot"),
                "Right Foot Constraint");
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
            using (new EditorGUI.DisabledScope(enabled == null || !enabled.boolValue))
            {
                DrawTransform(transform, "Hand / Foot Value");
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawTransform(SerializedProperty transform, string label)
        {
            if (transform == null) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            SerializedProperty position = transform.FindPropertyRelative("t");
            SerializedProperty rotation = transform.FindPropertyRelative("q");
            if (position != null) EditorGUILayout.PropertyField(position, new GUIContent("Position"));
            if (rotation != null) EditorGUILayout.PropertyField(rotation, new GUIContent("Rotation"));
            EditorGUILayout.EndVertical();
        }
    }
}
