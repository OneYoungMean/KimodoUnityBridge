using TimelineInject;
using KimodoUnityBridge;
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

            int selected = EditorGUILayout.Popup(
                new GUIContent("Constraint Mode", "Only the selected mode is sampled, displayed, and exported."),
                mode.intValue >= 0 && mode.intValue <= 2 ? mode.intValue : -1,
                new[] { "Root2D", "FullBody", "Effector" });
            if (selected >= 0 && selected != mode.intValue)
            {
                mode.intValue = selected;
                so.FindProperty("sampleData.constraintMode").stringValue =
                    selected == 0 ? "root2d" : selected == 1 ? "fullbody" : "effector";
            }
            if (mode.intValue < 0 || mode.intValue > 2)
            {
                EditorGUILayout.HelpBox("Unsupported legacy constraint mode. Select Root2D, FullBody, or Effector explicitly.", MessageType.Error);
                return;
            }

            if (marker != null)
            {
                KimodoConstraintMarkerEditorUtility.DrawMarkerTimeField(so, marker);
            }

            switch ((KimodoConstraintMode)mode.enumValueIndex)
            {
                case KimodoConstraintMode.Root2D:
                    DrawRootOverride(so);
                    using (new EditorGUI.DisabledScope(true))
                    {
                        DrawEffectorPanels(so, "sampleData", "Effectors", true, showEnable: true);
                    }
                    break;
                case KimodoConstraintMode.Effector:
                    DrawRootOverride(so);
                    DrawEffectorPanels(so, "sampleData", "Effectors", IsAutoSample(so), showEnable: true);
                    break;
                default:
                    DrawFullBody(so);
                    DrawRootOverride(so);
                    DrawEffectorPanels(so, "sampleData", "Effectors", IsAutoSample(so), showEnable: true);
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

        private static void DrawRootOverride(SerializedObject so)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            SerializedProperty allowHeading = so.FindProperty("sampleData.enableMask.rootHeading");
            SerializedProperty sampleValid = so.FindProperty("sampleData.sampleValid");
            using (new EditorGUI.DisabledScope(IsAutoSample(so)))
            {
                if (DrawFullTransform(
                        so.FindProperty("sampleData.rootOverride"),
                        "Root Override (World Transform)",
                        showRotationToggle: false,
                        rotationEnabled: allowHeading?.boolValue == true))
                {
                    if (sampleValid != null) sampleValid.boolValue = true;
                }
                if (allowHeading != null)
                {
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(
                        allowHeading,
                        new GUIContent("Allow Heading", "Export Root2D heading and use it as FullBody yaw overlay."));
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (sampleValid != null) sampleValid.boolValue = true;
                    }
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
                so.FindProperty(root + ".enableMask.leftHand"),
                "Left Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.rightHand",
                so.FindProperty(root + ".enableMask.rightHand"),
                "Right Hand Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.leftFoot",
                so.FindProperty(root + ".enableMask.leftFoot"),
                "Left Foot Effector", autoSample, showEnable);
            DrawEndEffectorPanel(
                so,
                root + ".effectors.rightFoot",
                so.FindProperty(root + ".enableMask.rightFoot"),
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
            using (new EditorGUI.DisabledScope(autoSample))
            {
                if (showEnable && enabled != null)
                {
                    EditorGUILayout.PropertyField(enabled, new GUIContent(label + " Enable"));
                }
                SerializedProperty autoSampleTransform = so.FindProperty(transformPath);
                DrawSpacePopup(autoSampleTransform, so.FindProperty("sampleData.rootOverride"));
            }
            SerializedProperty transform = so.FindProperty(transformPath);
            using (new EditorGUI.DisabledScope(autoSample))
            {
                if (DrawFullTransform(transform, label))
                {
                    if (enabled != null && !showEnable) enabled.boolValue = true;
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawSpacePopup(SerializedProperty transform, SerializedProperty root)
        {
            if (transform == null) return;
            SerializedProperty space = transform.FindPropertyRelative("space");
            if (space == null) return;
            int oldIndex = space.enumValueIndex;
            Rect spaceRect = EditorGUILayout.GetControlRect();
            int newIndex = EditorGUI.Popup(
                spaceRect,
                "Space",
                oldIndex,
                new[] { "Local Space", "World Space" });
            space.enumValueIndex = newIndex;
            if (newIndex == oldIndex) return;

            SerializedProperty position = transform.FindPropertyRelative("position");
            SerializedProperty rotation = transform.FindPropertyRelative("rotation");
            Vector3 valuePosition = position != null ? position.vector3Value : Vector3.zero;
            Quaternion valueRotation = rotation != null ? rotation.quaternionValue : Quaternion.identity;
            Vector3 rootPosition = root?.FindPropertyRelative("position")?.vector3Value ?? Vector3.zero;
            Quaternion rootRotation = root?.FindPropertyRelative("rotation")?.quaternionValue ?? Quaternion.identity;
            KimodoConstraintSpace from = (KimodoConstraintSpace)oldIndex;
            KimodoConstraintSpace to = (KimodoConstraintSpace)newIndex;
            Vector3 worldPosition = KimodoConstraintSpaceUtility.ToWorldPosition(valuePosition, from,
                new KimodoRigidTransform { position = rootPosition, rotation = rootRotation });
            Quaternion worldRotation = KimodoConstraintSpaceUtility.ToWorldRotation(valueRotation, from,
                new KimodoRigidTransform { position = rootPosition, rotation = rootRotation });
            if (to == KimodoConstraintSpace.Local)
            {
                valuePosition = Quaternion.Inverse(rootRotation) * (worldPosition - rootPosition);
                valueRotation = (Quaternion.Inverse(rootRotation) * worldRotation).normalized;
            }
            else
            {
                valuePosition = worldPosition;
                valueRotation = worldRotation;
            }
            if (position != null) position.vector3Value = valuePosition;
            if (rotation != null) rotation.quaternionValue = valueRotation;
            space.enumValueIndex = newIndex;
            SerializedProperty sampleValid = transform.serializedObject?.FindProperty("sampleData.sampleValid");
            if (sampleValid != null) sampleValid.boolValue = true;
        }

        private static void DrawMuscleValues(SerializedProperty muscles)
        {
            KimodoConstraintMuscleValueGUI.Draw(muscles);
        }

        private static bool DrawFullTransform(
            SerializedProperty transform,
            string label,
            bool showRotationToggle = true,
            bool rotationEnabled = true)
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
                SerializedProperty enableRotation = transform.FindPropertyRelative("enableRotation");
                if (enableRotation != null && showRotationToggle)
                {
                    EditorGUILayout.PropertyField(enableRotation, new GUIContent("Enable Rotation"));
                    using (new EditorGUI.DisabledScope(!enableRotation.boolValue))
                    {
                        EditorGUILayout.PropertyField(rotation, new GUIContent("Rotation"));
                    }
                }
                else
                {
                    using (new EditorGUI.DisabledScope(!rotationEnabled))
                    {
                        EditorGUILayout.PropertyField(rotation, new GUIContent("Rotation"));
                    }
                }
            }
            bool changed = EditorGUI.EndChangeCheck();
            return changed;
        }
    }
}
