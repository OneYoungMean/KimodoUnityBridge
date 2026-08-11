using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using UnityEngine;

namespace TimelineInject
{
    public interface IKimodoConstraintPreviewSelectable
    {
        bool ConstraintPreviewEnabled { get; }
        int ConstraintPreviewPriority { get; }
        string ConstraintPreviewName { get; }
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
        public List<float[]> target_positions;
        public List<string> joint_names;
        public bool? dense_path;
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
        public CharacterPose characterPose;
        public string constraintType = string.Empty;
        public double sampleTime;
        public KimodoConstraintRigType rigType = KimodoConstraintRigType.Soma77;
        public bool hasRootHeading = true;
        public Vector3 kimodoRootPosition;
        public Vector2 rootHeading = Vector2.right;
        public Vector3 unityRootPos;
        public Quaternion unityRootRot = Quaternion.identity;
        public bool hasEndEffectorTargetPosition;
        public Vector3 endEffectorTargetPositionRootLocal;
        public bool hasEndEffectorTargetRotation;
        public Quaternion endEffectorTargetRotationBodyRelative = Quaternion.identity;
        public List<string> jointNames = new List<string>();
        public List<Vector3> localAxisAngles = new List<Vector3>();
        public List<int> sampledJointIndices = new List<int>();
        public List<float> muscles = new List<float>();
        public Vector3 leftFootPosition;
        public Quaternion leftFootRotation = Quaternion.identity;
        public Vector3 rightFootPosition;
        public Quaternion rightFootRotation = Quaternion.identity;

        public KimodoMarkerSampleResult Clone()
        {
            return new KimodoMarkerSampleResult
            {
                characterPose = characterPose?.Clone(),
                constraintType = constraintType ?? string.Empty,
                sampleTime = sampleTime,
                rigType = rigType,
                hasRootHeading = hasRootHeading,
                kimodoRootPosition = kimodoRootPosition,
                rootHeading = rootHeading,
                unityRootPos = unityRootPos,
                unityRootRot = unityRootRot,
                hasEndEffectorTargetPosition = hasEndEffectorTargetPosition,
                endEffectorTargetPositionRootLocal = endEffectorTargetPositionRootLocal,
                hasEndEffectorTargetRotation = hasEndEffectorTargetRotation,
                endEffectorTargetRotationBodyRelative = endEffectorTargetRotationBodyRelative,
                jointNames = jointNames != null ? new List<string>(jointNames) : new List<string>(),
                localAxisAngles = localAxisAngles != null ? new List<Vector3>(localAxisAngles) : new List<Vector3>(),
                sampledJointIndices = sampledJointIndices != null ? new List<int>(sampledJointIndices) : new List<int>(),
                muscles = muscles != null ? new List<float>(muscles) : new List<float>(),
                leftFootPosition = leftFootPosition,
                leftFootRotation = leftFootRotation,
                rightFootPosition = rightFootPosition,
                rightFootRotation = rightFootRotation
            };
        }
    }

}
