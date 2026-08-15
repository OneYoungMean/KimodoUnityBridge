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

    /// <summary>Channels owned by one canonical constraint pose.  The protocol
    /// still receives its historical fullbody/root2d/end-effector records.</summary>
    [Serializable]
    public sealed class KimodoConstraintMask
    {
        public bool muscle;
        public bool rootPosition;
        public bool rootHeading;
        public bool leftFoot;
        public bool rightFoot;
        public bool leftHand;
        public bool rightHand;

        public KimodoConstraintMask Clone() => (KimodoConstraintMask)MemberwiseClone();

        public static KimodoConstraintMask ForType(string type)
        {
            var result = new KimodoConstraintMask();
            switch ((type ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-'))
            {
                case "fullbody": result.muscle = true; result.rootPosition = true; result.rootHeading = true; break;
                case "root2d": result.rootPosition = true; result.rootHeading = true; break;
                case "left-hand": result.leftHand = true; break;
                case "right-hand": result.rightHand = true; break;
                case "left-foot": result.leftFoot = true; break;
                case "right-foot": result.rightFoot = true; break;
            }
            return result;
        }

        public bool AnyEndEffector => leftFoot || rightFoot || leftHand || rightHand;
        public bool IsEmpty => !muscle && !rootPosition && !rootHeading && !AnyEndEffector;

        public static KimodoConstraintMask Resolve(KimodoConstraintMask value, string type)
        {
            // All authored markers now carry an explicit mask. A null mask is
            // normalized once to the default unified full-body channel set.
            return value ?? ForType("fullbody");
        }
    }

    /// <summary>
    /// Canonical raw pose data used by generation paths that already have
    /// profile joint rotations. Values are kept in Unity canonical space until
    /// the constraint JSON exporter applies the protocol conversion.
    /// </summary>
    [Serializable]
    public sealed class KimodoConstraintRawData
    {
        public Vector3 rootPosition;
        public List<Vector3> localJointAxisAngles = new List<Vector3>();

        public KimodoConstraintRawData Clone() => new KimodoConstraintRawData
        {
            rootPosition = rootPosition,
            localJointAxisAngles = localJointAxisAngles != null
                ? new List<Vector3>(localJointAxisAngles)
                : null
        };
    }

    [Serializable]
    public sealed class KimodoMarkerSampleResult
    {
        public CharacterPose characterPose;
        [NonSerialized]
        public KimodoConstraintRawData rawData;
        // FullBody owns characterPose.root. Root2D is kept separately so its
        // X/Z and heading override cannot destroy FullBody Y, pitch or roll.
        public CharacterPoseTransform root2DOverride = new CharacterPoseTransform();
        public bool hasRoot2DOverride;
        public string constraintType = "constraint";
        public double sampleTime;
        public bool hasRootHeading = true;
        public KimodoConstraintMask mask;

        public KimodoMarkerSampleResult Clone() => new KimodoMarkerSampleResult
        {
            characterPose = characterPose?.Clone(),
            rawData = rawData?.Clone(),
            root2DOverride = root2DOverride != null
                ? new CharacterPoseTransform { t = root2DOverride.t, q = root2DOverride.q }
                : null,
            hasRoot2DOverride = hasRoot2DOverride,
            constraintType = this.constraintType,
            sampleTime = sampleTime,
            hasRootHeading = hasRootHeading,
            mask = mask?.Clone()
        };
    }

}
