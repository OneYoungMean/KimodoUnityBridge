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

            // Root2DOverride is a shared world-space channel.  It must remain
            // visible while editing FullBody/Effector as well; otherwise the
            // hips source silently disappears from the unified payload.
            if ((KimodoConstraintMode)mode.enumValueIndex != KimodoConstraintMode.Root2D)
            {
                DrawRoot2DOverride(so);
            }

            switch ((KimodoConstraintMode)mode.enumValueIndex)
            {
                case KimodoConstraintMode.Root2D:
                    DrawRoot2D(so);
                    break;
                case KimodoConstraintMode.Effector:
                    DrawEffectors(so, "sampleData");
                    break;
                default:
                    DrawFullBody(so);
                    break;
            }
        }

        // The edit window's framed payload is the canonical presentation for
        // both surfaces. Keep the visual container and guidance in one place.
        internal static void DrawConstraintPayload(SerializedObject so)
        {
            if (so == null) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawConstraintPanels(so);
            EditorGUILayout.HelpBox(
                "Muscle values are the authoritative body-pose data. Scene target drags write back to the same canonical pose.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        // Inspector and the persistent edit window commit the same payload;
        // keep the serialized apply/dirty transition in one place.
        internal static bool ApplyConstraintPanels(SerializedObject so, KimodoConstraintMarker marker)
        {
            if (so == null) return false;
            bool changed = so.ApplyModifiedProperties();
            if (changed)
            {
                KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(marker);
            }
            return changed;
        }

        private static void DrawRoot2D(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty root = so.FindProperty("sampleData.root2DOverride");
            SerializedProperty allowHeading = so.FindProperty("sampleData.enableMask.root2DHeading");
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

        private static void DrawRoot2DOverride(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty root = so.FindProperty("sampleData.root2DOverride");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                DrawTransform(root, "Root Position / Rotation");
            }
            EditorGUILayout.HelpBox(
                "Root2DOverride is the world-space hips position/rotation and is applied before effectors.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private static void DrawFullBody(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty pose = so.FindProperty("sampleData.sampleData");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                DrawMuscleValues(pose);
            }
            DrawFullBodyEffectors(so, IsAutoSample(so));
            EditorGUILayout.HelpBox(
                "FullBody always exports its four effectors with the muscle pose. Dragging a Scene gizmo writes target data and never rewrites muscles.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private static void DrawFullBodyEffectors(SerializedObject so, bool autoSample)
        {
            EditorGUILayout.LabelField("FullBody Effectors", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(autoSample))
            {
                DrawTransform(so.FindProperty("sampleData.effectors.leftHand"), "Left Hand Effector");
                DrawTransform(so.FindProperty("sampleData.effectors.rightHand"), "Right Hand Effector");
                DrawTransform(so.FindProperty("sampleData.effectors.leftFoot"), "Left Foot Effector");
                DrawTransform(so.FindProperty("sampleData.effectors.rightFoot"), "Right Foot Effector");
            }
        }

        private static void DrawEffectors(SerializedObject so, string root)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.HelpBox(
                "Effectors keep the last sampled reference pose when Auto Sample is disabled. Only target channels are editable.",
                MessageType.None);
            DrawIkTargetPanels(so, root, "Effectors", IsAutoSample(so));
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
                so.FindProperty(root + ".effectors.leftHand"),
                so.FindProperty(root + ".enableMask.leftHandEffector"),
                "Left Hand Effector", autoSample);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".effectors.rightHand"),
                so.FindProperty(root + ".enableMask.rightHandEffector"),
                "Right Hand Effector", autoSample);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".effectors.leftFoot"),
                so.FindProperty(root + ".enableMask.leftFootEffector"),
                "Left Foot Effector", autoSample);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".effectors.rightFoot"),
                so.FindProperty(root + ".enableMask.rightFootEffector"),
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
