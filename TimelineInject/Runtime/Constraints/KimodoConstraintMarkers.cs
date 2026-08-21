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

    public enum KimodoConstraintMode
    {
        Root2D = 0,
        FullBody = 1,
        Effector = 2,
        Mix = 3
    }

    [Serializable]
    public class KimodoConstraintEffectors
    {
        public CharacterPoseSides hands = new CharacterPoseSides();
        public CharacterPoseSides feet = new CharacterPoseSides();

        public KimodoConstraintEffectors Clone() => new KimodoConstraintEffectors
        {
            hands = hands != null ? hands.Clone() : new CharacterPoseSides(),
            feet = feet != null ? feet.Clone() : new CharacterPoseSides()
        };

        public void CopyTo(CharacterPose pose)
        {
            if (pose == null) return;
            pose.hands = hands != null ? hands.Clone() : new CharacterPoseSides();
            pose.feet = feet != null ? feet.Clone() : new CharacterPoseSides();
        }
    }

    [Serializable]
    public sealed class KimodoRoot2DConstraintData
    {
        public CharacterPoseTransform root = new CharacterPoseTransform();
        public bool allowHeading = true;

        public KimodoRoot2DConstraintData Clone() => new KimodoRoot2DConstraintData
        {
            root = root != null ? root.Clone() : new CharacterPoseTransform(),
            allowHeading = allowHeading
        };
    }

    [Serializable]
    public sealed class KimodoFullBodyConstraintData
    {
        // Muscles and root are the authored FullBody source. Its four effector
        // targets are always active, separate channels; they never write back
        // into the muscle source.
        public CharacterPose pose = new CharacterPose();
        [UnityEngine.Serialization.FormerlySerializedAs("ikTargets")]
        public KimodoConstraintEffectors effectors = new KimodoConstraintEffectors();


        public KimodoFullBodyConstraintData Clone() => new KimodoFullBodyConstraintData
        {
            pose = pose != null ? pose.Clone() : new CharacterPose(),
            effectors = effectors != null ? effectors.Clone() : new KimodoConstraintEffectors()
        };
    }

    [Serializable]
    public sealed class KimodoEffectorConstraintData
    {
        // The last successful animation sample is retained when Auto Sample
        // is disabled. Effector edits never write back to this reference muscle set.
        public CharacterPose referencePose = new CharacterPose();
        [UnityEngine.Serialization.FormerlySerializedAs("ikTargets")]
        public KimodoConstraintEffectors effectors = new KimodoConstraintEffectors();

        public bool leftHand;
        public bool rightHand;
        public bool leftFoot;
        public bool rightFoot;

        public KimodoEffectorConstraintData Clone() => new KimodoEffectorConstraintData
        {
            referencePose = referencePose != null ? referencePose.Clone() : new CharacterPose(),
            effectors = effectors != null ? effectors.Clone() : new KimodoConstraintEffectors(),
            leftHand = leftHand,
            rightHand = rightHand,
            leftFoot = leftFoot,
            rightFoot = rightFoot
        };
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
                case "fullbody":
                    result.muscle = true;
                    result.rootPosition = true;
                    result.rootHeading = true;
                    result.leftHand = true;
                    result.rightHand = true;
                    result.leftFoot = true;
                    result.rightFoot = true;
                    break;
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
        // Canonical payload. Legacy fields below are being removed in later
        // migration phases; new code must use sampleData and validMask.
        public float[] sampleData = KimodoSampleDataLayout.CreateBuffer();
        public KimodoSampleChannelMask validMask = new KimodoSampleChannelMask();
        public bool enabled = true;
        // Composer uses this as the explicit creation-order tie breaker.
        // When unset, input order remains the deterministic fallback.
        public long creationOrder;

        // Temporary source-compatibility bridge.  New code must use
        // KimodoSampleResultPoseUtility; this member is intentionally kept in
        // one isolated block so it can be deleted after the remaining editor
        // consumers are migrated.
        [Obsolete("Use KimodoSampleResultPoseUtility and sampleData.")]
        public CharacterPose characterPose
        {
            get => KimodoSampleResultPoseUtility.TryDecode(this, out CharacterPose pose, out _)
                ? pose
                : null;
            set
            {
                if (value == null)
                {
                    sampleData = KimodoSampleDataLayout.CreateBuffer();
                    validMask ??= new KimodoSampleChannelMask();
                    validMask.muscle49 = false;
                    validMask.rootTQ = false;
                    validMask.leftFootTQ = false;
                    validMask.rightFootTQ = false;
                    return;
                }
                KimodoSampleResultPoseUtility.TryEncode(this, value, out _);
            }
        }

        // Effectors are absolute scene-space transport values. They are kept
        // separate from the muscle pose; no intermediate solver interprets them.
        [UnityEngine.Serialization.FormerlySerializedAs("worldIkTargets")]
        public KimodoConstraintEffectors effectors = new KimodoConstraintEffectors();
        // FullBody owns characterPose.root. Root2D is kept separately so its
        // X/Z and heading override cannot destroy FullBody Y, pitch or roll.
        public CharacterPoseTransform root2DOverride = new CharacterPoseTransform();
        [Obsolete("Use validMask.root2DPosition. This compatibility property is not serialized.")]
        public bool hasRoot2DOverride
        {
            get => validMask?.root2DPosition == true;
            set
            {
                validMask ??= new KimodoSampleChannelMask();
                validMask.root2DPosition = value;
                validMask.NormalizeDependencies();
            }
        }
        public string constraintType = "constraint";
        // Non-empty for samples authored by the new mode-aware marker. Empty
        // samples retain the legacy resolver behavior for command-only data.
        public string constraintMode;
        public double sampleTime;
        [Obsolete("Use validMask.root2DHeading. This compatibility property is not serialized.")]
        public bool hasRootHeading
        {
            get => validMask?.root2DHeading == true;
            set
            {
                validMask ??= new KimodoSampleChannelMask();
                validMask.root2DHeading = value && validMask.root2DPosition;
            }
        }
        public KimodoConstraintMask mask;

        public KimodoMarkerSampleResult Clone() => new KimodoMarkerSampleResult
        {
            sampleData = sampleData != null ? (float[])sampleData.Clone() : KimodoSampleDataLayout.CreateBuffer(),
            validMask = validMask?.Clone() ?? new KimodoSampleChannelMask(),
            enabled = enabled,
            creationOrder = creationOrder,
            effectors = effectors?.Clone() ?? new KimodoConstraintEffectors(),
            root2DOverride = root2DOverride != null
                ? new CharacterPoseTransform { t = root2DOverride.t, q = root2DOverride.q }
                : null,
            constraintType = this.constraintType,
            constraintMode = this.constraintMode,
            sampleTime = sampleTime,
            mask = mask?.Clone()
        };
    }

}
