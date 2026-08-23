using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    // Single source of truth for the mode-aware Constraint inspector.
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

        // The edit window's framed payload is the canonical presentation for
        // both surfaces. Keep the visual container and guidance in one place.
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
                if (DrawTransform(root, "Root Position / Rotation"))
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
            SerializedProperty pose = so.FindProperty("sampleData.sampleData");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                DrawMuscleValues(pose);
            }
            DrawEffectorPanels(so, "sampleData", "FullBody Effectors", IsAutoSample(so), showEnable: false);
            EditorGUILayout.HelpBox(
                "FullBody always exports its four effectors with the muscle pose. Dragging a Scene gizmo writes target data and never rewrites muscles.",
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
                so.FindProperty(root + ".effectors.leftHand"),
                showEnable ? so.FindProperty(root + ".enableMask.leftHandEffector") : null,
                "Left Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".effectors.rightHand"),
                showEnable ? so.FindProperty(root + ".enableMask.rightHandEffector") : null,
                "Right Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".effectors.leftFoot"),
                showEnable ? so.FindProperty(root + ".enableMask.leftFootEffector") : null,
                "Left Foot Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so.FindProperty(root + ".effectors.rightFoot"),
                showEnable ? so.FindProperty(root + ".enableMask.rightFootEffector") : null,
                "Right Foot Effector", autoSample, showEnable);
        }

        private static void DrawEndEffectorPanel(
            SerializedProperty transform,
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
