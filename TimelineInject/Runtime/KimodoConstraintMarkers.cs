using System;
using System.Collections.Generic;
using UnityEngine;

namespace TimelineInject
{
    [Serializable]
    public class KimodoConstraintJson
    {
        public string type;
        public List<int> frame_indices = new List<int>();
        public List<float[]> smooth_root_2d;
        public List<float[]> global_root_heading;
        public List<float[][]> local_joints_rot;
        public List<float[]> root_positions;
        public List<float[]> target_positions;
        public List<string> joint_names;
        public bool? dense_path;
        public float[] target_root_2d;
        public float? max_speed;
        public float? max_acceleration;
        public float? arrival_threshold;
        public bool? include_heading;
    }

    public enum KimodoConstraintRigType
    {
        Soma77 = 0,
        G1 = 1,
        Smplx = 2,
        Unknown = 3,
        Core27 = 4
    }

    [Serializable]
    public sealed class KimodoMarkerSampleResult
    {
        public string constraintType = string.Empty;
        public double sampleTime;
        public KimodoConstraintRigType rigType = KimodoConstraintRigType.Soma77;
        public bool hasRootHeading = true;
        public Vector3 kimodoRootPosition;
        public Vector2 rootHeading = Vector2.right;
        public float rootTargetMaxSpeed = 1.25f;
        public float rootTargetMaxAcceleration = 1.5f;
        public float rootTargetArrivalThreshold = 0.1f;
        public bool rootTargetIncludeHeading = true;
        public Vector3 unityRootPos;
        public Quaternion unityRootRot = Quaternion.identity;
        public bool hasUnityHipsPose;
        public Vector3 unityHipsPos;
        public Quaternion unityHipsRot = Quaternion.identity;
        public bool hasEndEffectorTargetPosition;
        public Vector3 endEffectorTargetPositionRootLocal;
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
                kimodoRootPosition = kimodoRootPosition,
                rootHeading = rootHeading,
                rootTargetMaxSpeed = rootTargetMaxSpeed,
                rootTargetMaxAcceleration = rootTargetMaxAcceleration,
                rootTargetArrivalThreshold = rootTargetArrivalThreshold,
                rootTargetIncludeHeading = rootTargetIncludeHeading,
                unityRootPos = unityRootPos,
                unityRootRot = unityRootRot,
                hasUnityHipsPose = hasUnityHipsPose,
                unityHipsPos = unityHipsPos,
                unityHipsRot = unityHipsRot,
                hasEndEffectorTargetPosition = hasEndEffectorTargetPosition,
                endEffectorTargetPositionRootLocal = endEffectorTargetPositionRootLocal,
                jointNames = jointNames != null ? new List<string>(jointNames) : new List<string>(),
                localAxisAngles = localAxisAngles != null ? new List<Vector3>(localAxisAngles) : new List<Vector3>(),
                sampledJointIndices = sampledJointIndices != null ? new List<int>(sampledJointIndices) : new List<int>()
            };
        }
    }

}
