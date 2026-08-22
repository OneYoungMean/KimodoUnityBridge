using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using UnityEngine;

namespace KimodoBridge
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
        public KimodoRigidTransform leftHand = KimodoRigidTransform.Identity;
        public KimodoRigidTransform rightHand = KimodoRigidTransform.Identity;
        public KimodoRigidTransform leftFoot = KimodoRigidTransform.Identity;
        public KimodoRigidTransform rightFoot = KimodoRigidTransform.Identity;

        public KimodoConstraintEffectors Clone() => new KimodoConstraintEffectors
        {
            leftHand = leftHand?.Clone() ?? KimodoRigidTransform.Identity,
            rightHand = rightHand?.Clone() ?? KimodoRigidTransform.Identity,
            leftFoot = leftFoot?.Clone() ?? KimodoRigidTransform.Identity,
            rightFoot = rightFoot?.Clone() ?? KimodoRigidTransform.Identity
        };

        public void CopyTo(CharacterPose pose)
        {
            if (pose == null) return;
            pose.leftHand = leftHand?.Clone() ?? KimodoRigidTransform.Identity;
            pose.rightHand = rightHand?.Clone() ?? KimodoRigidTransform.Identity;
            pose.leftFoot = leftFoot?.Clone() ?? KimodoRigidTransform.Identity;
            pose.rightFoot = rightFoot?.Clone() ?? KimodoRigidTransform.Identity;
        }
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

        /// <summary>
        /// Resolves the effective protocol channels from the canonical sample.
        /// New samples use enableMask as the sole validity source. A missing
        /// mask is treated as an old boundary object and inferred from mode.
        /// </summary>
        public static KimodoConstraintMask FromSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null)
            {
                return new KimodoConstraintMask();
            }

            KimodoSampleChannelMask enabled = sample.enableMask;
            if (enabled != null)
            {
                return new KimodoConstraintMask
                {
                    muscle = enabled.muscle49,
                    rootPosition = enabled.root2DPosition,
                    rootHeading = enabled.root2DHeading,
                    leftFoot = enabled.leftFootEffector,
                    rightFoot = enabled.rightFootEffector,
                    leftHand = enabled.leftHandEffector,
                    rightHand = enabled.rightHandEffector
                };
            }

            string mode = (sample.constraintMode ?? sample.constraintType ?? string.Empty)
                .Trim().ToLowerInvariant().Replace('_', '-');
            return mode == string.Empty || mode == "constraint"
                ? ForType("fullbody")
                : ForType(mode);
        }
    }

    /// <summary>
    /// Canonical raw pose data used by generation paths that already have
    /// profile joint rotations. Values are kept in Unity canonical space until
    /// the constraint JSON exporter applies the protocol conversion.
    /// </summary>
    [Serializable]
    internal sealed class KimodoConstraintInternalData
    {
        public Vector3 rootPosition;
        public List<Vector3> localJointAxisAngles = new List<Vector3>();
        public double sampleTime;

        public KimodoConstraintInternalData Clone() => new KimodoConstraintInternalData
        {
            rootPosition = rootPosition,
            localJointAxisAngles = localJointAxisAngles != null
                ? new List<Vector3>(localJointAxisAngles)
                : null,
            sampleTime = sampleTime
        };
    }

    [Serializable]
    public sealed class KimodoMarkerSampleResult
    {
        // Canonical payload. Legacy fields below are being removed in later
        // migration phases; new code must use sampleData and enableMask.
        public KimodoBridge.MuscleSample sampleData = new KimodoBridge.MuscleSample();
        public KimodoSampleChannelMask enableMask = new KimodoSampleChannelMask();
        public bool enabled = true;
        // Composer uses this as the explicit creation-order tie breaker.
        // When unset, input order remains the deterministic fallback.
        public long creationOrder;

        // Effectors are absolute scene-space transport values. They are kept
        // separate from the muscle pose; no intermediate solver interprets them.
        [UnityEngine.Serialization.FormerlySerializedAs("worldIkTargets")]
        public KimodoConstraintEffectors effectors = new KimodoConstraintEffectors();
        // FullBody owns rootTQ in sampleData. Root2D is kept separately so its
        // X/Z and heading override cannot destroy FullBody Y, pitch or roll.
        public KimodoRigidTransform root2DOverride = KimodoRigidTransform.Identity;
        // Protocol expansion uses a transient type (fullbody/root2d/effector
        // family). Authored SampleResult data persists only constraintMode;
        // the old constraintType field was redundant serialized state.
        [NonSerialized] private string protocolType;
        public string constraintType
        {
            get => string.IsNullOrWhiteSpace(protocolType) ? "constraint" : protocolType;
            set => protocolType = value;
        }
        // Non-empty for samples authored by the new mode-aware marker. Empty
        // samples retain the legacy resolver behavior for command-only data.
        public string constraintMode;
        public double sampleTime;

        public KimodoMarkerSampleResult Clone() => new KimodoMarkerSampleResult
        {
            sampleData = sampleData?.Clone() ?? new KimodoBridge.MuscleSample(),
            enableMask = enableMask?.Clone() ?? new KimodoSampleChannelMask(),
            enabled = enabled,
            creationOrder = creationOrder,
            effectors = effectors?.Clone() ?? new KimodoConstraintEffectors(),
            root2DOverride = root2DOverride.Clone(),
            protocolType = protocolType,
            constraintMode = this.constraintMode,
            sampleTime = sampleTime
        };
    }

}
