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
                    sample != null &&
                    sample.sampleTime >= 0.0 &&
                    sample.sampleTime < anchorWindowSeconds &&
                    IsAnchorConstraintType(sample.constraintMode))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsAnchorConstraintType(string constraintType)
        {
            return string.Equals(constraintType, "fullbody", StringComparison.OrdinalIgnoreCase) ||
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
