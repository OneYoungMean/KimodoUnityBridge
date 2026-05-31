using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace KimodoUnityMotionTools
{
    public sealed class KimodoTransformGizmoVisualizer : MonoBehaviour
    {
        public Color pointColor = new Color(0.2f, 0.9f, 1f, 1f);
        public Color lineColor = new Color(1f, 0.8f, 0.2f, 0.9f);
        public float pointRadius = 0.01f;
        public bool drawNames = true;
        public bool drawOnlyWhenSelected = false;

        private void OnDrawGizmos()
        {
            if (drawOnlyWhenSelected)
            {
                return;
            }

            DrawHierarchy();
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawOnlyWhenSelected)
            {
                return;
            }

            DrawHierarchy();
        }

        private void DrawHierarchy()
        {
            Transform root = transform;
            if (root == null)
            {
                return;
            }

            DrawRecursive(root, root);
        }

        private void DrawRecursive(Transform current, Transform root)
        {
            if (current == null)
            {
                return;
            }

            Gizmos.color = pointColor;
            Gizmos.DrawSphere(current.position, Mathf.Max(0.0005f, pointRadius));

#if UNITY_EDITOR
            if (drawNames)
            {
                Handles.color = pointColor;
                Handles.Label(current.position + Vector3.up * pointRadius * 2f, current.name);
            }
#endif

            if (current != root && current.parent != null)
            {
                Gizmos.color = lineColor;
                Gizmos.DrawLine(current.parent.position, current.position);
            }

            int childCount = current.childCount;
            for (int i = 0; i < childCount; i++)
            {
                DrawRecursive(current.GetChild(i), root);
            }
        }
    }
}
