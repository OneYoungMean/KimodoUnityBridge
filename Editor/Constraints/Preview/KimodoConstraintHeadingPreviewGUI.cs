using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintHeadingPreviewGUI
    {
        private const float PreviewHeight = 84f;
        private const float ArrowInset = 12f;
        private static readonly Color BackgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Color BorderColor = new Color(0.28f, 0.28f, 0.28f, 1f);
        private static readonly Color AxisColor = new Color(0.48f, 0.48f, 0.48f, 1f);
        private static readonly Color ArrowColor = new Color(1f, 0.58f, 0.16f, 1f);

        internal static void Draw(Vector2 heading, bool enabled)
        {
            if (!enabled)
            {
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("Heading Preview", EditorStyles.miniBoldLabel);

            if (heading.sqrMagnitude <= 1e-6f)
            {
                EditorGUILayout.HelpBox("Heading vector is zero, so no direction preview is available.", MessageType.None);
                return;
            }

            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(PreviewHeight), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, BackgroundColor);
            Handles.BeginGUI();

            Vector2 center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - ArrowInset;
            Vector2 direction = heading.normalized;
            Vector2 axisX = new Vector2(1f, 0f);
            Vector2 axisY = new Vector2(0f, -1f);
            Vector2 tip = center + new Vector2(direction.x, -direction.y) * radius;

            Handles.color = BorderColor;
            Handles.DrawSolidRectangleWithOutline(rect, Color.clear, BorderColor);

            Handles.color = AxisColor;
            Handles.DrawLine(center + axisX * (radius + 4f), center - axisX * (radius + 4f));
            Handles.DrawLine(center + axisY * (radius + 4f), center - axisY * (radius + 4f));

            Handles.color = ArrowColor;
            Handles.DrawAAPolyLine(3f, center, tip);
            Handles.ArrowHandleCap(0, tip, Quaternion.LookRotation(new Vector3(direction.x, 0f, direction.y), Vector3.up), 14f, EventType.Repaint);
            Handles.DrawSolidDisc(center, Vector3.forward, 2.5f);
            Handles.EndGUI();

            EditorGUILayout.LabelField($"XZ Heading: ({heading.x:F3}, {heading.y:F3})", EditorStyles.miniLabel);
        }
    }
}
