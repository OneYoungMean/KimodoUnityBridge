using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    // Shared mode-aware Constraint payload drawing used by the Inspector
    // Editor and the EditorWindow-created instance of that Editor.
    internal static class KimodoConstraintEditorState
    {
        internal static bool IsAutoSample(SerializedObject so)
        {
            return so?.FindProperty("autoSample")?.boolValue == true;
        }

        internal static void DrawConstraintPanels(SerializedObject so, IMarker marker)
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
                        "Enabling Auto Sample will overwrite the current effectors and motion.",
                        MessageType.Info);
                }
            }
            if (mode == null) return;

            EditorGUILayout.PropertyField(
                mode,
                new GUIContent("Constraint Mode", "Only the selected mode is sampled, displayed, and exported."));

            if (marker != null)
            {
                KimodoConstraintMarkerEditorUtility.DrawMarkerTimeField(so, marker);
            }

            switch ((KimodoConstraintMode)mode.enumValueIndex)
            {
                case KimodoConstraintMode.Root2D:
                    DrawRoot2D(so);
                    break;
                case KimodoConstraintMode.Effector:
                    DrawRoot2D(so);
                    DrawEffectors(so, "sampleData");
                    break;
                default:
                    DrawFullBody(so);
                    DrawRoot2D(so);
                    DrawEffectorPanels(so, "sampleData", "Effectors", IsAutoSample(so), showEnable: false);
                    break;
            }
        }

        // The framed payload is the canonical presentation for both surfaces.
        internal static void DrawConstraintPayload(SerializedObject so, IMarker marker)
        {
            if (so == null) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawConstraintPanels(so, marker);
            EditorGUILayout.EndVertical();
        }

        private static void DrawRoot2D(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty allowHeading = so.FindProperty("sampleData.enableMask.root2DHeading");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                if (DrawTransform(
                        so.FindProperty("sampleData.rootOverride"),
                        "Root Position / Rotation"))
                {
                    SerializedProperty positionEnabled = so.FindProperty("sampleData.enableMask.root2DPosition");
                    if (positionEnabled != null)
                    {
                        positionEnabled.boolValue = true;
                    }
                }
                if (allowHeading != null)
                {
                    EditorGUILayout.PropertyField(
                        allowHeading,
                        new GUIContent("Allow Heading", "Export Root2D heading and use it as FullBody yaw overlay."));
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawFullBody(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty pose = so.FindProperty("sampleData.sampleData.data");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                DrawMuscleValues(pose);
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawEffectors(SerializedObject so, string root)
        {
            DrawEffectorPanels(so, root, "Effectors", IsAutoSample(so), showEnable: true);
        }

        private static void DrawEffectorPanels(
            SerializedObject so,
            string root,
            string label,
            bool autoSample,
            bool showEnable)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.leftHand",
                so.FindProperty(root + ".enableMask.leftHandEffector"),
                "Left Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.rightHand",
                so.FindProperty(root + ".enableMask.rightHandEffector"),
                "Right Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.leftFoot",
                so.FindProperty(root + ".enableMask.leftFootEffector"),
                "Left Foot Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.rightFoot",
                so.FindProperty(root + ".enableMask.rightFootEffector"),
                "Right Foot Effector", autoSample, showEnable);
        }

        private static void DrawEndEffectorPanel(
            SerializedObject so,
            string transformPath,
            SerializedProperty enabled,
            string label,
            bool autoSample,
            bool showEnable)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (showEnable && enabled != null)
            {
                EditorGUILayout.PropertyField(enabled, new GUIContent(label + " Enable"));
            }
            // Channel enable controls export in Effector mode; it must not
            // hide authored values while a non-AutoSample marker is edited.
            using (new EditorGUI.DisabledScope(autoSample))
            {
                if (DrawTransform(
                    so.FindProperty(transformPath),
                    label) && !showEnable && enabled != null)
                {
                    enabled.boolValue = true;
                }
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
            SerializedProperty position = transform.FindPropertyRelative("position");
            SerializedProperty rotation = transform.FindPropertyRelative("rotation");
            if (position == null && rotation == null) return false;

            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            if (position != null)
            {
                EditorGUILayout.PropertyField(position, new GUIContent("Position"));
            }
            if (rotation != null)
            {
                EditorGUILayout.PropertyField(rotation, new GUIContent("Rotation"));
            }
            bool changed = EditorGUI.EndChangeCheck();
            return changed;
        }
    }
}
