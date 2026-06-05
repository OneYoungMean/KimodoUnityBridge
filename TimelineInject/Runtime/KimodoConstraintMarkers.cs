using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Timeline;

namespace TimelineInject
{
    public static class KimodoVectorExtensions
    {
        public static float[] ToArray(this Vector2 value)
        {
            return new[] { value.x, value.y };
        }

        public static float[] ToArray(this Vector3 value)
        {
            return new[] { value.x, value.y, value.z };
        }
    }

    [Serializable]
    public class KimodoConstraintJson
    {
        public string type;
        public List<int> frame_indices = new List<int>();
        public List<float[]> smooth_root_2d;
        public List<float[]> global_root_heading;
        public List<float[][]> local_joints_rot;
        public List<float[]> root_positions;
        public List<string> joint_names;
    }

    public enum KimodoConstraintRigType
    {
        Soma30 = 0,
        G1 = 1,
        Smplx = 2,
        Unknown = 3
    }

    [Serializable]
    public sealed class KimodoMarkerSampleResult
    {
        public string constraintType = string.Empty;
        public double sampleTime;
        public KimodoConstraintRigType rigType = KimodoConstraintRigType.Soma30;
        public bool hasRootHeading = true;
        public Vector3 rootPosition;
        public Vector2 rootHeading = Vector2.right;
        public List<string> jointNames = new List<string>();
        public List<Vector3> localAxisAngles = new List<Vector3>();
        public List<int> sampledJointIndices = new List<int>();

        public KimodoMarkerSampleResult Clone()
        {
            return new KimodoMarkerSampleResult
            {
                constraintType = constraintType ?? string.Empty,
                sampleTime = sampleTime,
                rigType = rigType,
                hasRootHeading = hasRootHeading,
                rootPosition = rootPosition,
                rootHeading = rootHeading,
                jointNames = jointNames != null ? new List<string>(jointNames) : new List<string>(),
                localAxisAngles = localAxisAngles != null ? new List<Vector3>(localAxisAngles) : new List<Vector3>(),
                sampledJointIndices = sampledJointIndices != null ? new List<int>(sampledJointIndices) : new List<int>()
            };
        }
    }

    public interface IKimodoSampleMarker
    {
        bool TrySampleMarker(
            Animator animator,
            Transform skeletonRoot,
            TimelineClip sourceClip,
            string modelName,
            double globalTime,
            string markerType,
            out KimodoMarkerSampleResult result,
            out string error);
    }
}
