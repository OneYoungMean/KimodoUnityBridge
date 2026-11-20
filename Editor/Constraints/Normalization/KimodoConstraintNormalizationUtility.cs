using System;
using System.Collections.Generic;
using KimodoBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintNormalizationUtility
    {
        internal static bool HasNormalizationAnchor(
            List<KimodoMarkerSampleResult> samples,
            double anchorWindowSeconds,
            KimodoMarkerSampleResult ignoredSample = null)
        {
            if (samples == null)
            {
                return false;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (!ReferenceEquals(sample, ignoredSample) &&
                    IsExportableAnchorSample(sample) &&
                    sample.sampleTime >= 0.0 &&
                    sample.sampleTime < anchorWindowSeconds &&
                    IsAnchorConstraintType(sample.constraintMode))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsExportableAnchorSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null || !sample.enabled || !sample.sampleValid || sample.rootOverride == null)
            {
                return false;
            }

            string mode = (sample.constraintMode ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
            switch (mode)
            {
                case "fullbody":
                    return sample.sampleData?.IsValid == true;
                case "root2d":
                    return KimodoConstraintMask.IsActive(sample, "rootposition") ||
                        KimodoConstraintMask.IsActive(sample, "rootheading");
                case "effector":
                    return IsExportableEffectorSample(sample);
                case "left-foot":
                    return IsExportableEffectorSample(sample) && KimodoConstraintMask.IsActive(sample, "leftfoot");
                case "right-foot":
                    return IsExportableEffectorSample(sample) && KimodoConstraintMask.IsActive(sample, "rightfoot");
                case "left-hand":
                    return IsExportableEffectorSample(sample) && KimodoConstraintMask.IsActive(sample, "lefthand");
                case "right-hand":
                    return IsExportableEffectorSample(sample) && KimodoConstraintMask.IsActive(sample, "righthand");
                case "end-effector":
                    return IsExportableEffectorSample(sample);
                default:
                    return false;
            }
        }

        private static bool IsExportableEffectorSample(KimodoMarkerSampleResult sample)
        {
            KimodoConstraintMask mask = sample?.enableMask;
            KimodoConstraintEffectors effectors = sample?.effectors;
            return sample?.sampleData?.IsValid == true &&
                mask?.AnyEndEffector == true &&
                effectors != null &&
                (!mask.leftHand || effectors.leftHand != null) &&
                (!mask.rightHand || effectors.rightHand != null) &&
                (!mask.leftFoot || effectors.leftFoot != null) &&
                (!mask.rightFoot || effectors.rightFoot != null);
        }

        private static bool IsAnchorConstraintType(string constraintType)
        {
            return string.Equals(constraintType, "fullbody", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "effector", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "root2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "left-foot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "right-foot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "end-effector", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "left-hand", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "right-hand", StringComparison.OrdinalIgnoreCase);
        }

        internal static Quaternion ResolvePlanarRotation(Quaternion rotation) =>
            KimodoMotionMath.ResolvePlanarHeading(rotation);

        internal static float ResolveHumanScale(Avatar avatar)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar) ||
                !KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar,
                    "KimodoRetargetClipScaleProbe",
                    out RetargetSkeleton cache,
                    out _))
            {
                return 1f;
            }

            try
            {
                return Mathf.Max(1e-6f, cache.humanScale);
            }
            finally
            {
                cache.Dispose();
            }
        }
    }
}
