using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal sealed class KimodoConstraintOverrideEditWindow : EditorWindow
    {
        private KimodoConstraintMarkerBase marker;
        private Vector2 scroll;
        private string lastError;

        internal static void ShowWindow(KimodoConstraintMarkerBase marker)
        {
            var window = GetWindow<KimodoConstraintOverrideEditWindow>(true, "Kimodo Constraint Override Edit");
            window.minSize = new Vector2(420f, 260f);
            window.marker = marker;
            window.lastError = string.Empty;
            window.Show();
            window.Focus();
        }

        internal static bool IsOpenForMarker(KimodoConstraintMarkerBase marker)
        {
            if (marker == null)
            {
                return false;
            }

            KimodoConstraintOverrideEditWindow[] windows = Resources.FindObjectsOfTypeAll<KimodoConstraintOverrideEditWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] != null && windows[i].marker == marker)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            if (marker != null && KimodoConstraintOverrideEditSession.HasActiveSession(marker))
            {
                KimodoConstraintOverrideEditSession.Cancel(marker);
            }
        }

        private void OnEditorUpdate()
        {
            if (marker == null || !KimodoConstraintOverrideEditSession.HasActiveSession(marker))
            {
                Close();
                return;
            }

            KimodoConstraintOverrideEditSession.PingSession(marker);
            Repaint();
        }

        private void OnGUI()
        {
            if (marker == null)
            {
                EditorGUILayout.HelpBox("Marker is null.", MessageType.Error);
                return;
            }

            if (!KimodoConstraintOverrideEditSession.HasActiveSession(marker))
            {
                EditorGUILayout.HelpBox("Edit session is not active.", MessageType.Warning);
                if (GUILayout.Button("Close", GUILayout.Height(28f)))
                {
                    Close();
                }
                return;
            }

            DrawHeader();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawMarkerPayload();
            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Constraint Override Edit Session", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Edit the preview rig pose directly in Scene view. Marker override data updates in real time.",
                MessageType.Info);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Marker", KimodoConstraintOverrideEditSession.DescribeMarker(marker));
            EditorGUILayout.LabelField("Override", marker.useOverride ? "Enabled" : "Disabled");
            EditorGUILayout.Space(6f);
        }

        private void DrawMarkerPayload()
        {
            using (new EditorGUI.DisabledScope(true))
            {
                var so = new SerializedObject(marker);
                so.Update();
                EditorGUILayout.PropertyField(so.FindProperty("frameIndices"), true);
                EditorGUILayout.PropertyField(so.FindProperty("smoothRoot2D"), true);
                EditorGUILayout.PropertyField(so.FindProperty("rootPositions"), true);
                EditorGUILayout.PropertyField(so.FindProperty("localJointRots"), true);
                SerializedProperty includeHeadingProp = so.FindProperty("includeGlobalHeading");
                if (includeHeadingProp != null)
                {
                    EditorGUILayout.PropertyField(includeHeadingProp);
                    if (includeHeadingProp.boolValue)
                    {
                        EditorGUILayout.PropertyField(so.FindProperty("globalRootHeading"), true);
                    }
                }
            }
        }

        private void DrawFooter()
        {
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", GUILayout.Height(30f)))
            {
                KimodoConstraintOverrideEditSession.Cancel(marker);
                Close();
            }

            if (GUILayout.Button("End Edit", GUILayout.Height(30f)))
            {
                if (!KimodoConstraintOverrideEditSession.TryCommit(marker, out string error))
                {
                    lastError = string.IsNullOrWhiteSpace(error) ? "Commit failed." : error;
                }
                else
                {
                    Close();
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
