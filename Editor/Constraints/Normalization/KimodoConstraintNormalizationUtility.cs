using System;
using System.Collections.Generic;
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
                    IsAnchorConstraintType(sample.constraintType))
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

        internal static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        // Retarget clip baking still needs the target humanoid scale. This is
        // not used by world-space constraint export.
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

        internal static void NormalizeRootPose(
            Vector3 anchorRootPosition,
            Quaternion anchorRootRotation,
            ref Vector3 rootPosition,
            ref Quaternion rootRotation)
        {
            Quaternion inverseAnchor = Quaternion.Inverse(anchorRootRotation);
            rootPosition = inverseAnchor * (rootPosition - anchorRootPosition);
            rootRotation = inverseAnchor * rootRotation;
        }

    }
}
