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

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Kimodo Constraint Marker (Constraint)", EditorStyles.boldLabel);
            KimodoConstraintMarkerEditorUtility.DrawEnabledField(serializedObject);
            if (!isWindow)
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

