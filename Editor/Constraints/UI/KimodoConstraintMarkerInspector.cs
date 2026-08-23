using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoConstraintMarker))]
    internal sealed class KimodoConstraintInspectorEditor : UnityEditor.Editor
    {
        private void OnDisable()
        {
            KimodoConstraintMarkerEditorUtility.ClearMarkerPreview(target as KimodoConstraintMarker, keepIfOverrideWindowOpen: true);
        }

        internal bool DrawGUI(bool isWindow)
        {
            KimodoConstraintMarker marker = target as KimodoConstraintMarker;
            if (marker == null) return false;

            KimodoConstraintMarkerEditorUtility.HandleDeleteCommand(marker);
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "Edit the canonical Constraint SampleResult. Scene targets are edited with handles in the Scene view.",
                MessageType.Info);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Kimodo Constraint Marker (Constraint)", EditorStyles.boldLabel);
            KimodoConstraintMarkerEditorUtility.DrawEnabledField(serializedObject);
            if (isWindow)
            {
                EditorGUILayout.HelpBox(
                    "Scene handles are active for the preview character. Drag Root2D or effectors in the Scene view.",
                    MessageType.None);
            }
            else
            {
                KimodoConstraintMarkerEditorUtility.DrawEditButton(serializedObject, marker);
            }
            EditorGUILayout.Space(4f);
            KimodoConstraintEditorState.DrawConstraintPayload(serializedObject, marker);

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(marker);
                KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
            }
            return changed;
        }

        public override void OnInspectorGUI()
        {
            DrawGUI(isWindow: false);
        }
    }
}

