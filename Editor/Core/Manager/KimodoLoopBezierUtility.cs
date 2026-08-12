using UnityEngine;

namespace KimodoUnityBridge.Command
{
    /// <summary>Dependency-free cubic Bezier sampling used by loop generation.</summary>
    internal static class KimodoLoopBezierUtility
    {
        internal static Vector2 Evaluate(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        internal static Vector2 Tangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float u = 1f - Mathf.Clamp01(t);
            return 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
        }
    }
}
