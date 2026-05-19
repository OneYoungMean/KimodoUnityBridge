#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.Editor
{
    [CustomEditor(typeof(KimodoSomaDefaultPosePreview))]
    internal sealed class KimodoSomaDefaultPosePreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();

            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Preview Actions", EditorStyles.boldLabel);

                var preview = (KimodoSomaDefaultPosePreview)target;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Build Preview", GUILayout.Height(28f)))
                    {
                        BuildPreview(preview);
                    }

                    if (GUILayout.Button("Clear Preview", GUILayout.Height(28f)))
                    {
                        ClearPreview(preview);
                    }
                }

                string mode = preview.useSoma77 ? "SOMA77" : "SOMA30";
                string rootName = preview.builtRoot != null ? preview.builtRoot.name : "(none)";
                EditorGUILayout.LabelField($"Mode: {mode}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Built Root: {rootName}", EditorStyles.miniLabel);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static void BuildPreview(KimodoSomaDefaultPosePreview preview)
        {
            if (preview == null)
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Build Kimodo SOMA Default Pose Preview");
            Undo.RegisterCompleteObjectUndo(preview, "Build Preview");

            preview.BuildPreview();
            EditorUtility.SetDirty(preview);

            if (preview.builtRoot != null)
            {
                Undo.RegisterCreatedObjectUndo(preview.builtRoot, "Create Preview Root");
                EditorGUIUtility.PingObject(preview.builtRoot);
            }

            Undo.CollapseUndoOperations(group);
        }

        private static void ClearPreview(KimodoSomaDefaultPosePreview preview)
        {
            if (preview == null)
            {
                return;
            }

            Undo.RegisterCompleteObjectUndo(preview, "Clear Preview");
            preview.ClearPreview();
            EditorUtility.SetDirty(preview);
        }
    }
}
#endif
