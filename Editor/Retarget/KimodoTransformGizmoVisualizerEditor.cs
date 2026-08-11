using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoTransformGizmoVisualizerEditor
    {
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawLabels(KimodoTransformGizmoVisualizer visualizer, GizmoType gizmoType)
        {
            if (visualizer == null || !visualizer.drawNames)
            {
                return;
            }

            if (visualizer.drawOnlyWhenSelected &&
                (gizmoType & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) == 0)
            {
                return;
            }

            Handles.color = visualizer.pointColor;
            DrawRecursive(visualizer.transform, Mathf.Max(0.0005f, visualizer.pointRadius));
        }

        private static void DrawRecursive(Transform current, float pointRadius)
        {
            if (current == null)
            {
                return;
            }

            Handles.Label(current.position + Vector3.up * pointRadius * 2f, current.name);
            for (int i = 0; i < current.childCount; i++)
            {
                DrawRecursive(current.GetChild(i), pointRadius);
            }
        }
    }
}
