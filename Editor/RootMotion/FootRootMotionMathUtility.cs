using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class FootRootMotionMathUtility
    {
        public static Vector2 ToXZ(Vector3 value)
        {
            return new Vector2(value.x, value.z);
        }

        public static Vector2 RotateXZ(Vector2 value, float yawRadians)
        {
            float sin = Mathf.Sin(yawRadians);
            float cos = Mathf.Cos(yawRadians);
            return new Vector2(
                value.x * cos + value.y * sin,
                -value.x * sin + value.y * cos);
        }

        public static Vector3 RotateY(Vector3 value, float yawRadians)
        {
            Vector2 rotated = RotateXZ(new Vector2(value.x, value.z), yawRadians);
            return new Vector3(rotated.x, value.y, rotated.y);
        }

        public static float ExtractYawRadians(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-8f)
            {
                return 0f;
            }

            return Mathf.Atan2(forward.x, forward.z);
        }

        public static float DeltaYawRadians(float fromRadians, float toRadians)
        {
            return Mathf.DeltaAngle(fromRadians * Mathf.Rad2Deg, toRadians * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        }

        public static Vector2 ComputeLocalOffset(Vector2 worldPointXZ, Vector2 rootXZ, float rootYawRadians)
        {
            return RotateXZ(worldPointXZ - rootXZ, -rootYawRadians);
        }

        public static Vector2 ClampMagnitude(Vector2 delta, float maxDistance)
        {
            if (!IsFinite(maxDistance) || delta.sqrMagnitude <= maxDistance * maxDistance)
            {
                return delta;
            }

            return delta.normalized * maxDistance;
        }

        public static float ClampYawDelta(float yawDeltaRadians, float maxRadians)
        {
            float normalizedDelta = Mathf.DeltaAngle(0f, yawDeltaRadians * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            if (!IsFinite(maxRadians))
            {
                return normalizedDelta;
            }

            return Mathf.Clamp(normalizedDelta, -maxRadians, maxRadians);
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
