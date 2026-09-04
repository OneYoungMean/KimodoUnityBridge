using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoConstraintMarker), true)]
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
            if (marker.IsAnalysis)
            {
                KimodoAnalysisKeyframeMarker analysisMarker = marker as KimodoAnalysisKeyframeMarker;
                if (analysisMarker == null)
                {
                    return false;
                }
                EditorGUILayout.LabelField("Analysis", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Message", analysisMarker.message ?? string.Empty);
                EditorGUILayout.ColorField("Color", analysisMarker.color);
                EditorGUILayout.LabelField("Source Role", analysisMarker.sourceRole ?? string.Empty);
                EditorGUILayout.LabelField("Frame", analysisMarker.frame.ToString());
                EditorGUILayout.PropertyField(serializedObject.FindProperty("markerType"), new GUIContent("Marker Type"));
                if (serializedObject.ApplyModifiedProperties())
                {
                    KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
                }
                return true;
            }
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

