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
                        "Auto Sample is disabled. Enable it to synchronize the scene pose.",
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

            // Root2D is part of the canonical constraint payload and remains
            // visible in every mode, including FullBody and Effector.
            DrawRoot2D(so);

            switch ((KimodoConstraintMode)mode.enumValueIndex)
            {
                case KimodoConstraintMode.Root2D:
                    break;
                case KimodoConstraintMode.Effector:
                    DrawEffectors(so, "sampleData");
                    break;
                default:
                    DrawFullBody(so);
                    break;
            }
        }

        // The framed payload is the canonical presentation for both surfaces.
        internal static void DrawConstraintPayload(SerializedObject so, IMarker marker)
        {
            if (so == null) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawConstraintPanels(so, marker);
            EditorGUILayout.HelpBox(
                "Muscle values are the authoritative body-pose data. Scene target drags write back to the same canonical pose.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private static void DrawRoot2D(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty allowHeading = so.FindProperty("sampleData.enableMask.root2DHeading");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                if (DrawTransform(
                        so.FindProperty("sampleData.root2DOverride.position"),
                        so.FindProperty("sampleData.root2DOverride.rotation"),
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
            EditorGUILayout.HelpBox(
                "Root2D position/rotation is always part of the constraint payload and is applied before mode-specific data.",
                MessageType.None);
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
            EditorGUILayout.HelpBox(
                "FullBody edits the muscle pose. Root2D remains available above; effector values are edited in Effector mode.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private static void DrawEffectors(SerializedObject so, string root)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawEffectorPanels(so, root, "Effectors", IsAutoSample(so), showEnable: true);
            EditorGUILayout.EndVertical();
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
                showEnable ? so.FindProperty(root + ".enableMask.leftHandEffector") : null,
                "Left Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.rightHand",
                showEnable ? so.FindProperty(root + ".enableMask.rightHandEffector") : null,
                "Right Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.leftFoot",
                showEnable ? so.FindProperty(root + ".enableMask.leftFootEffector") : null,
                "Left Foot Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.rightFoot",
                showEnable ? so.FindProperty(root + ".enableMask.rightFootEffector") : null,
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
                DrawTransform(
                    so.FindProperty(transformPath + ".position"),
                    so.FindProperty(transformPath + ".rotation"),
                    "Target Position / Rotation");
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawMuscleValues(SerializedProperty muscles)
        {
            KimodoConstraintMuscleValueGUI.Draw(muscles);
        }

        private static bool DrawTransform(
            SerializedProperty position,
            SerializedProperty rotation,
            string label)
        {
            if (position == null && rotation == null) return false;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            if (position != null) EditorGUILayout.PropertyField(position, new GUIContent("Position"));
            if (rotation != null) EditorGUILayout.PropertyField(rotation, new GUIContent("Rotation"));
            bool changed = EditorGUI.EndChangeCheck();
            EditorGUILayout.EndVertical();
            return changed;
        }
    }
}
