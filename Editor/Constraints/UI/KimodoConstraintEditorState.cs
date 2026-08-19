using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    // Single source of truth for the mode-aware Constraint inspector.
    internal static class KimodoConstraintEditorState
    {
        internal static bool IsAutoSample(SerializedObject so)
        {
            return so?.FindProperty("autoSample")?.boolValue == true;
        }

        internal static bool IsFullBodyAutoSample(SerializedObject so) => IsAutoSample(so);

        internal static void DrawConstraintPanels(SerializedObject so)
        {
            if (so == null) return;

            SerializedProperty mode = so.FindProperty("constraintMode");
            SerializedProperty autoSample = so.FindProperty("autoSample");
            if (autoSample != null)
            {
                EditorGUILayout.PropertyField(autoSample, new GUIContent("Auto Sample"));
                if (!autoSample.boolValue)
                {
                    EditorGUILayout.HelpBox(
                        "Auto Sample is disabled. Enable it to synchronize the scene pose.\n" +
                        "已关闭 Auto Sample；请打开它以同步场景姿势。",
                        MessageType.Warning);
                }
            }
            if (mode == null) return;

            EditorGUILayout.PropertyField(
                mode,
                new GUIContent("Constraint Mode", "Only the selected mode is sampled, displayed, and exported."));

            switch ((KimodoConstraintMode)mode.enumValueIndex)
            {
                case KimodoConstraintMode.Root2D:
                    DrawRoot2D(so);
                    break;
                case KimodoConstraintMode.IK:
                    DrawIk(so, "ikData");
                    break;
                default:
                    DrawFullBody(so);
                    break;
            }
        }

        private static void DrawRoot2D(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty root = so.FindProperty("root2DData.root");
            SerializedProperty allowHeading = so.FindProperty("root2DData.allowHeading");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                DrawTransform(root, "Root Position / Rotation");
                if (allowHeading != null)
                {
                    EditorGUILayout.PropertyField(
                        allowHeading,
                        new GUIContent("Allow Heading", "Export Root2D heading and use it as FullBody yaw overlay."));
                }
            }
            EditorGUILayout.HelpBox("Root2D mode draws only the root marker and exports only root2d.", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private static void DrawFullBody(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty pose = so.FindProperty("fullBodyData.pose");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                DrawTransform(pose?.FindPropertyRelative("root"), "Pelvis Position / Rotation");
                DrawMuscleValues(pose?.FindPropertyRelative("muscles"));
            }
            DrawFullBodyIkTargets(so, IsAutoSample(so));
            EditorGUILayout.HelpBox(
                "FullBody always exports its four IK targets with the muscle pose. Dragging a Scene target writes its target data and never rewrites muscles.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private static void DrawFullBodyIkTargets(SerializedObject so, bool autoSample)
        {
            EditorGUILayout.LabelField("FullBody IK Targets", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(autoSample))
            {
                DrawTransform(so.FindProperty("fullBodyData.ikTargets.hands.left"), "Left Hand Effector");
                DrawTransform(so.FindProperty("fullBodyData.ikTargets.hands.right"), "Right Hand Effector");
                DrawTransform(so.FindProperty("fullBodyData.ikTargets.feet.left"), "Left Foot Effector");
                DrawTransform(so.FindProperty("fullBodyData.ikTargets.feet.right"), "Right Foot Effector");
            }
        }

        private static void DrawIk(SerializedObject so, string root)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.HelpBox(
                "IK keeps the last sampled reference pose when Auto Sample is disabled. Only IK target channels are editable.",
                MessageType.None);
            DrawIkTargetPanels(so, root, "IK Targets", IsAutoSample(so));
            EditorGUILayout.EndVertical();
        }

        private static void DrawIkTargetPanels(
            SerializedObject so,
            string root,
            string label,
            bool autoSample)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".ikTargets.hands.left"),
                so.FindProperty(root + ".leftHand"),
                "Left Hand Effector", autoSample);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".ikTargets.hands.right"),
                so.FindProperty(root + ".rightHand"),
                "Right Hand Effector", autoSample);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".ikTargets.feet.left"),
                so.FindProperty(root + ".leftFoot"),
                "Left Foot Effector", autoSample);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".ikTargets.feet.right"),
                so.FindProperty(root + ".rightFoot"),
                "Right Foot Effector", autoSample);
        }

        private static void DrawEndEffectorPanel(
            SerializedProperty transform,
            SerializedProperty enabled,
            string label,
            bool autoSample)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (enabled != null)
            {
                EditorGUILayout.PropertyField(enabled, new GUIContent(label + " Enable"));
            }
            using (new EditorGUI.DisabledScope(autoSample || enabled == null || !enabled.boolValue))
            {
                DrawTransform(transform, "Target Position / Rotation");
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawMuscleValues(SerializedProperty muscles)
        {
            KimodoConstraintMuscleValueGUI.Draw(muscles);
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
