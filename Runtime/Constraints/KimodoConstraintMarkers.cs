using System;
using System.Collections.Generic;
using KimodoUnityBridge;
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

    }

    /// <summary>Channel bits used independently by enableMask and validMask.</summary>
    [Serializable]
    public sealed class KimodoConstraintMask
    {
        public bool muscle;
        public bool rootTQ;
        public bool leftFootTQ;
        public bool rightFootTQ;
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
        public bool IsEmpty => !muscle && !rootTQ && !leftFootTQ && !rightFootTQ &&
            !rootPosition && !rootHeading && !AnyEndEffector;

        public static KimodoConstraintMask Resolve(KimodoConstraintMask value, string type)
        {
            return value ?? new KimodoConstraintMask();
        }

        /// <summary>
        /// Resolves validity channels from the canonical sample. A missing
        /// validMask means every channel is invalid.
        /// </summary>
        public static KimodoConstraintMask FromSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null)
            {
                return new KimodoConstraintMask();
            }

            return sample.validMask?.Clone() ?? new KimodoConstraintMask();
        }

        public static bool IsEnabledAndValid(
            KimodoMarkerSampleResult sample,
            Func<KimodoConstraintMask, bool> enabledSelector,
            Func<KimodoConstraintMask, bool> validSelector)
        {
            if (sample == null || enabledSelector == null || validSelector == null)
            {
                return false;
            }

            KimodoConstraintMask enabled = sample.enableMask;
            KimodoConstraintMask valid = FromSample(sample);
            return enabled != null && enabledSelector(enabled) && validSelector(valid);
        }

        public static bool IsActive(KimodoMarkerSampleResult sample, string channel)
        {
            if (sample == null) return false;
            KimodoConstraintMask enabled = sample.enableMask;
            KimodoConstraintMask valid = FromSample(sample);
            switch ((channel ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "muscle": return enabled?.muscle == true && valid.muscle;
                case "roottq": return enabled?.rootTQ == true && valid.rootTQ;
                case "leftfoottq": return enabled?.leftFootTQ == true && valid.leftFootTQ;
                case "rightfoottq": return enabled?.rightFootTQ == true && valid.rightFootTQ;
                case "rootposition": return enabled?.rootPosition == true && valid.rootPosition;
                case "rootheading": return enabled?.rootPosition == true && valid.rootPosition &&
                    enabled.rootHeading && valid.rootHeading;
                case "lefthand": return enabled?.leftHand == true && valid.leftHand;
                case "righthand": return enabled?.rightHand == true && valid.rightHand;
                case "leftfoot": return enabled?.leftFoot == true && valid.leftFoot;
                case "rightfoot": return enabled?.rightFoot == true && valid.rightFoot;
                default: return false;
            }
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
        public KimodoConstraintMask enableMask = new KimodoConstraintMask();
        public KimodoConstraintMask validMask = new KimodoConstraintMask();
        public bool enabled = true;
        // Composer uses this as the explicit creation-order tie breaker.
        // When unset, input order remains the deterministic fallback.
        public long creationOrder;

        // Effectors are absolute scene-space transport values. They are kept
        // separate from the muscle pose; no intermediate solver interprets them.
        [UnityEngine.Serialization.FormerlySerializedAs("worldIkTargets")]
        public KimodoConstraintEffectors effectors = new KimodoConstraintEffectors();
        // Complete world-space hips override. Root2D X/Z and heading
        // projection is applied only when building the protocol constraint.
        [UnityEngine.Serialization.FormerlySerializedAs("root2DOverride")]
        public KimodoRigidTransform rootOverride = KimodoRigidTransform.Identity;

        // Source compatibility for older editor/test callers. This alias is
        // not a second serialized value.
        public KimodoRigidTransform root2DOverride
        {
            get => rootOverride;
            set => rootOverride = value;
        }
        // One mode is the only persisted constraint semantic. Wire-family
        // selection is centralized in KimodoConstraintInternal.
        public string constraintMode = "constraint";
        public double sampleTime;

        public KimodoMarkerSampleResult Clone() => new KimodoMarkerSampleResult
        {
            sampleData = sampleData?.Clone() ?? new KimodoBridge.MuscleSample(),
            enableMask = enableMask?.Clone() ?? new KimodoConstraintMask(),
            validMask = validMask?.Clone() ?? new KimodoConstraintMask(),
            enabled = enabled,
            creationOrder = creationOrder,
            effectors = effectors?.Clone() ?? new KimodoConstraintEffectors(),
            rootOverride = rootOverride?.Clone() ?? KimodoRigidTransform.Identity,
            constraintMode = this.constraintMode,
            sampleTime = sampleTime
        };
    }

}
