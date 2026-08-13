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

    [Serializable]
    public sealed class KimodoMarkerSampleResult
    {
        public CharacterPose characterPose;
        public string constraintType = "constraint";
        public double sampleTime;
        public bool hasRootHeading = true;
        public KimodoConstraintMask mask;

        public KimodoMarkerSampleResult Clone() => new KimodoMarkerSampleResult
        {
            characterPose = characterPose?.Clone(),
            constraintType = "constraint",
            sampleTime = sampleTime,
            hasRootHeading = hasRootHeading,
            mask = mask?.Clone()
        };
    }

}
