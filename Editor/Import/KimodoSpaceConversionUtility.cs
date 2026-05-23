using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoSpaceConversionUtility
    {
        public static KimodoMarkerSampleResult ToKimodoSample(KimodoMarkerSampleResult unitySample)
        {
            if (unitySample == null)
            {
                return null;
            }

            var converted = new KimodoMarkerSampleResult
            {
                rootPosition = ToKimodoRootPosition(unitySample.rootPosition),
                rootHeading = ToKimodoHeading(unitySample.rootHeading),
                localAxisAngles = new List<Vector3>()
            };

            if (unitySample.localAxisAngles != null)
            {
                for (int i = 0; i < unitySample.localAxisAngles.Count; i++)
                {
                    converted.localAxisAngles.Add(ToKimodoAxisAngle(unitySample.localAxisAngles[i]));
                }
            }

            return converted;
        }

        public static Vector3 ToKimodoRootPosition(Vector3 unityWorldPosition)
        {
            return new Vector3(-unityWorldPosition.x, unityWorldPosition.y, unityWorldPosition.z);
        }

        public static Vector2 ToKimodoHeading(Vector2 unityHeadingXZ)
        {
            return new Vector2(-unityHeadingXZ.x, unityHeadingXZ.y);
        }

        public static Vector3 ToKimodoAxisAngle(Vector3 unityAxisAngle)
        {
            float angleRad = unityAxisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Vector3.zero;
            }

            Vector3 axis = unityAxisAngle / angleRad;
            Quaternion unityLocal = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
            Quaternion kimodoLocal = new Quaternion(unityLocal.x, -unityLocal.y, -unityLocal.z, unityLocal.w);
            return QuaternionToAxisAngleVector(kimodoLocal);
        }

        private static Vector3 QuaternionToAxisAngleVector(Quaternion q)
        {
            q.Normalize();
            q.ToAngleAxis(out float degrees, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis == Vector3.zero)
            {
                return Vector3.zero;
            }

            if (degrees > 180f)
            {
                degrees -= 360f;
            }

            float radians = degrees * Mathf.Deg2Rad;
            return axis.normalized * radians;
        }
    }
}
