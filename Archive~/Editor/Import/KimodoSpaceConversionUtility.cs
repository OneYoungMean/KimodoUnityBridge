using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
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
                kimodoRootPosition = ToKimodoRootPosition(unitySample.unityRootPos),
                rootHeading = ToKimodoHeading(unitySample.rootHeading),
                unityRootPos = unitySample.unityRootPos,
                unityRootRot = unitySample.unityRootRot,
                localAxisAngles = new List<Vector3>(),
                sampledJointIndices = unitySample.sampledJointIndices != null
                    ? new List<int>(unitySample.sampledJointIndices)
                    : new List<int>()
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
            return KimodoRuntimeUtility.QuaternionToAxisAngleVector(kimodoLocal);
        }

        public static KimodoMarkerSampleResult ToUnitySample(KimodoMarkerSampleResult kimodoSample)
        {
            if (kimodoSample == null)
            {
                return null;
            }

            var converted = new KimodoMarkerSampleResult
            {
                kimodoRootPosition = ToUnityRootPosition(kimodoSample.kimodoRootPosition),
                rootHeading = ToUnityHeading(kimodoSample.rootHeading),
                unityRootPos = kimodoSample.unityRootPos,
                unityRootRot = kimodoSample.unityRootRot,
                localAxisAngles = new List<Vector3>(),
                sampledJointIndices = kimodoSample.sampledJointIndices != null
                    ? new List<int>(kimodoSample.sampledJointIndices)
                    : new List<int>()
            };

            if (kimodoSample.localAxisAngles != null)
            {
                for (int i = 0; i < kimodoSample.localAxisAngles.Count; i++)
                {
                    converted.localAxisAngles.Add(ToUnityAxisAngle(kimodoSample.localAxisAngles[i]));
                }
            }

            return converted;
        }

        public static Vector3 ToUnityRootPosition(Vector3 kimodoPosition)
        {
            return new Vector3(-kimodoPosition.x, kimodoPosition.y, kimodoPosition.z);
        }

        public static Vector2 ToUnityHeading(Vector2 kimodoHeading)
        {
            return new Vector2(-kimodoHeading.x, kimodoHeading.y);
        }

        public static Vector3 ToUnityAxisAngle(Vector3 kimodoAxisAngle)
        {
            float angleRad = kimodoAxisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Vector3.zero;
            }

            Vector3 axis = kimodoAxisAngle / angleRad;
            Quaternion kimodoLocal = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
            Quaternion unityLocal = new Quaternion(kimodoLocal.x, -kimodoLocal.y, -kimodoLocal.z, kimodoLocal.w);
            return KimodoRuntimeUtility.QuaternionToAxisAngleVector(unityLocal);
        }
    }
}

