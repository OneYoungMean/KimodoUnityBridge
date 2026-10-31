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
        Effector = 2
    }

    public enum KimodoConstraintMarkerType
    {
        Constraint = 0,
        External = 1,
        ExternalPath = 2,
        Analysis = 3
    }

    [Serializable]
    public sealed class KimodoRootPathKnot
    {
        public int frame;
        public Vector2 position;
        public bool hasHeading;
        public Vector2 heading;
        public bool hasTangentIn;
        public Vector2 tangentIn;
        public bool hasTangentOut;
        public Vector2 tangentOut;

        public KimodoRootPathKnot Clone() => new KimodoRootPathKnot
        {
            frame = frame,
            position = position,
            hasHeading = hasHeading,
            heading = heading,
            hasTangentIn = hasTangentIn,
            tangentIn = tangentIn,
            hasTangentOut = hasTangentOut,
            tangentOut = tangentOut
        };
    }

    [Serializable]
    public sealed class KimodoRootPathData
    {
        public string type = "forward";
        public float length = 1f;
        public float sourceHumanScale = 1f;
        public bool inverse;
        public List<KimodoRootPathKnot> knots = new List<KimodoRootPathKnot>();

        public KimodoRootPathData Clone() => new KimodoRootPathData
        {
            type = type,
            length = length,
            sourceHumanScale = sourceHumanScale,
            inverse = inverse,
            knots = knots?.ConvertAll(knot => knot?.Clone()) ?? new List<KimodoRootPathKnot>()
        };
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

    /// <summary>Persisted constraint channel intent.</summary>
    [Serializable]
    public sealed class KimodoConstraintMask
    {
        public bool rootHeading;
        public bool leftFoot;
        public bool rightFoot;
        public bool leftHand;
        public bool rightHand;

        public KimodoConstraintMask Clone() => (KimodoConstraintMask)MemberwiseClone();

        public static KimodoConstraintMask ForType(string type)
        {
            var result = new KimodoConstraintMask();
            string normalized = (type ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
            switch (normalized)
            {
                case "fullbody": result.rootHeading = true; break;
                case "root2d": result.rootHeading = true; break;
                case "effector":
                    result.rootHeading = true;
                    result.leftHand = true;
                    result.rightHand = true;
                    result.leftFoot = true;
                    result.rightFoot = true;
                    break;
                case "left-hand": EnableEffectorSupport(result); result.leftHand = true; break;
                case "right-hand": EnableEffectorSupport(result); result.rightHand = true; break;
                case "left-foot": EnableEffectorSupport(result); result.leftFoot = true; break;
                case "right-foot": EnableEffectorSupport(result); result.rightFoot = true; break;
            }
            return result;
        }

        private static void EnableEffectorSupport(KimodoConstraintMask result)
        {
            result.rootHeading = true;
        }

        public bool AnyEndEffector => leftFoot || rightFoot || leftHand || rightHand;
        public bool IsEmpty => !rootHeading && !AnyEndEffector;

        public static bool IsActive(KimodoMarkerSampleResult sample, string channel)
        {
            if (sample == null || !sample.sampleValid) return false;
            KimodoConstraintMask enabled = sample.enableMask;
            switch ((channel ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "muscle": return sample.sampleData?.IsValid == true;
                case "rootposition": return sample.rootOverride != null;
                case "rootheading": return sample.rootOverride != null && enabled?.rootHeading == true;
                case "lefthand": return enabled?.leftHand == true;
                case "righthand": return enabled?.rightHand == true;
                case "leftfoot": return enabled?.leftFoot == true;
                case "rightfoot": return enabled?.rightFoot == true;
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
        // Canonical payload.
        public KimodoBridge.MuscleSample sampleData = new KimodoBridge.MuscleSample();
        public KimodoConstraintMask enableMask = new KimodoConstraintMask();
        // Older Unity assets missing this field retain true as the default.
        public bool sampleValid = true;
        public bool enabled = true;
        // Creation metadata; does not merge or select constraint types.
        public long creationOrder;

        // Each effector declares local or world space. The pose pipeline
        // normalizes local values against rootOverride exactly once; command
        // inputs are authored as world-space values.
        public KimodoConstraintEffectors effectors = new KimodoConstraintEffectors();
        // Complete hips override in the same explicit-target space. For a
        // root2d constraint, consumers project only X/Z and heading;
        // sampled root Y, pitch and roll remain motion channels.
        public KimodoRigidTransform rootOverride = KimodoRigidTransform.Identity;

        // One mode is the only persisted constraint semantic. Wire-family
        // selection is centralized in KimodoConstraintInternal.
        public string constraintMode = "fullbody";
        public double sampleTime;

        public KimodoMarkerSampleResult Clone() => new KimodoMarkerSampleResult
        {
            sampleData = sampleData?.Clone() ?? new KimodoBridge.MuscleSample(),
            enableMask = enableMask?.Clone() ?? new KimodoConstraintMask(),
            sampleValid = sampleValid,
            enabled = enabled,
            creationOrder = creationOrder,
            effectors = effectors?.Clone() ?? new KimodoConstraintEffectors(),
            rootOverride = rootOverride?.Clone(),
            constraintMode = this.constraintMode,
            sampleTime = sampleTime
        };
    }

}
