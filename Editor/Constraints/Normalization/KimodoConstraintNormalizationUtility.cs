using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintNormalizationUtility
    {
        private const double FirstFrameTimeEpsilonSeconds = 1e-4d;

        private sealed class ResolvedNormalizationAnchor
        {
            public KimodoConstraintNormalizationAnchorKind AnchorKind = KimodoConstraintNormalizationAnchorKind.None;
            public KimodoMarkerSampleResult AnchorSample;
            public Vector3 KimodoRootPosition = Vector3.zero;
            public Quaternion InverseKimodoRootRotation = Quaternion.identity;
        }

        internal static void NormalizeConstraintOrigin(
            List<KimodoMarkerSampleResult> samples,
            out KimodoConstraintNormalizationInfo normalizationInfo,
            out string warning)
        {
            NormalizeConstraintOrigin(
                samples,
                anchorWindowSeconds: double.PositiveInfinity,
                out normalizationInfo,
                out warning);
        }

        internal static void NormalizeConstraintOrigin(
            List<KimodoMarkerSampleResult> samples,
            double anchorWindowSeconds,
            out KimodoConstraintNormalizationInfo normalizationInfo,
            out string warning)
        {
            ResolvedNormalizationAnchor anchor = ResolveNormalizationAnchor(
                samples,
                anchorWindowSeconds,
                out warning);
            ApplyNormalizationAnchor(samples, anchor);
            normalizationInfo = BuildNormalizationInfo(anchor);
        }

        internal static KimodoConstraintNormalizationInfo ResolveTimelineAlignment(
            List<KimodoMarkerSampleResult> samples,
            double anchorWindowSeconds,
            out string warning)
        {
            return BuildNormalizationInfo(ResolveNormalizationAnchor(samples, anchorWindowSeconds, out warning));
        }

        private static ResolvedNormalizationAnchor ResolveNormalizationAnchor(
            List<KimodoMarkerSampleResult> samples,
            double anchorWindowSeconds,
            out string warning)
        {
            warning = string.Empty;
            var resolved = new ResolvedNormalizationAnchor();
            if (TryResolveConstraintAnchor(
                    samples,
                    anchorWindowSeconds,
                    ignoredSample: null,
                    out KimodoMarkerSampleResult anchor,
                    out KimodoConstraintNormalizationAnchorKind anchorKind,
                    out warning))
            {
                resolved.AnchorKind = anchorKind;
                resolved.AnchorSample = anchor != null ? anchor.Clone() : null;
                if (anchor != null)
                {
                    resolved.KimodoRootPosition = new Vector3(
                        anchor.kimodoRootPosition.x,
                        0f,
                        anchor.kimodoRootPosition.z);
                    resolved.InverseKimodoRootRotation = Quaternion.Inverse(ResolveKimodoPlanarRootRotation(anchor));
                }
            }

            return resolved;
        }

        private static bool TryResolveConstraintAnchor(
            List<KimodoMarkerSampleResult> samples,
            double anchorWindowSeconds,
            KimodoMarkerSampleResult ignoredSample,
            out KimodoMarkerSampleResult anchor,
            out KimodoConstraintNormalizationAnchorKind anchorKind,
            out string warning)
        {
            anchor = null;
            anchorKind = KimodoConstraintNormalizationAnchorKind.None;
            warning = string.Empty;
            if (samples == null || samples.Count == 0)
            {
                return false;
            }

            double earliestTime = double.MaxValue;
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample != null &&
                    !ReferenceEquals(sample, ignoredSample) &&
                    sample.sampleTime >= 0.0 &&
                    sample.sampleTime < anchorWindowSeconds &&
                    ResolveAnchorPriority(sample) != KimodoConstraintNormalizationAnchorKind.None &&
                    sample.sampleTime < earliestTime)
                {
                    earliestTime = sample.sampleTime;
                }
            }

            if (earliestTime == double.MaxValue)
            {
                return false;
            }

            int bestRank = 0;
            int bestRankCount = 0;

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null ||
                    ReferenceEquals(sample, ignoredSample) ||
                    sample.sampleTime < 0.0 ||
                    sample.sampleTime >= anchorWindowSeconds ||
                    !IsSameFirstFrameTime(sample.sampleTime, earliestTime))
                {
                    continue;
                }

                KimodoConstraintNormalizationAnchorKind candidateKind = ResolveAnchorPriority(sample);
                int candidateRank = ResolveAnchorRank(candidateKind);
                if (candidateRank > bestRank)
                {
                    anchor = sample;
                    anchorKind = candidateKind;
                    bestRank = candidateRank;
                    bestRankCount = 1;
                }
                else if (candidateRank == bestRank && candidateRank > 0)
                {
                    bestRankCount++;
                }
            }

            if (anchor == null)
            {
                return false;
            }

            if (bestRankCount > 1)
            {
                switch (bestRank)
                {
                    case 3:
                        warning = "Multiple fullbody constraints were found on the first frame; using the first one as the normalization anchor.";
                        break;
                    case 2:
                        warning = "Multiple end constraints were found on the first frame; using the first one as the normalization anchor.";
                        break;
                    case 1:
                        warning = "Multiple root2d constraints were found on the first frame; using the first one as the normalization anchor.";
                        break;
                }
            }

            return true;
        }

        private static int ResolveAnchorRank(KimodoConstraintNormalizationAnchorKind kind)
        {
            switch (kind)
            {
                case KimodoConstraintNormalizationAnchorKind.FullBody:
                    return 3;
                case KimodoConstraintNormalizationAnchorKind.Foot:
                case KimodoConstraintNormalizationAnchorKind.EndEffector:
                    return 2;
                case KimodoConstraintNormalizationAnchorKind.Root2D:
                    return 1;
                default:
                    return 0;
            }
        }

        internal static bool HasNormalizationAnchor(
            List<KimodoMarkerSampleResult> samples,
            double anchorWindowSeconds,
            KimodoMarkerSampleResult ignoredSample = null)
        {
            return TryResolveConstraintAnchor(
                samples,
                anchorWindowSeconds,
                ignoredSample,
                out _,
                out _,
                out _);
        }

        private static void ApplyNormalizationAnchor(
            List<KimodoMarkerSampleResult> samples,
            ResolvedNormalizationAnchor anchor)
        {
            if (samples == null || samples.Count == 0)
            {
                return;
            }

            Vector3 anchorRootPosition = anchor != null ? anchor.KimodoRootPosition : Vector3.zero;
            Quaternion inverseAnchorRootRotation = anchor != null ? anchor.InverseKimodoRootRotation : Quaternion.identity;
            for (int i = 0; i < samples.Count; i++)
            {
                NormalizeConstraintOriginSample(samples[i], anchorRootPosition, inverseAnchorRootRotation);
            }
        }

        private static KimodoConstraintNormalizationInfo BuildNormalizationInfo(ResolvedNormalizationAnchor anchor)
        {
            return new KimodoConstraintNormalizationInfo
            {
                Applied = anchor?.AnchorSample != null,
                AnchorKind = anchor != null ? anchor.AnchorKind : KimodoConstraintNormalizationAnchorKind.None,
                AnchorSample = anchor != null ? anchor.AnchorSample : null
            };
        }

        private static KimodoConstraintNormalizationAnchorKind ResolveAnchorPriority(KimodoMarkerSampleResult sample)
        {
            if (sample == null || string.IsNullOrWhiteSpace(sample.constraintType))
            {
                return KimodoConstraintNormalizationAnchorKind.None;
            }

            if (string.Equals(sample.constraintType, "left-foot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sample.constraintType, "right-foot", StringComparison.OrdinalIgnoreCase))
            {
                return KimodoConstraintNormalizationAnchorKind.Foot;
            }

            if (string.Equals(sample.constraintType, "end-effector", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sample.constraintType, "left-hand", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sample.constraintType, "right-hand", StringComparison.OrdinalIgnoreCase))
            {
                return KimodoConstraintNormalizationAnchorKind.EndEffector;
            }

            if (string.Equals(sample.constraintType, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                return KimodoConstraintNormalizationAnchorKind.FullBody;
            }

            if (string.Equals(sample.constraintType, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                return KimodoConstraintNormalizationAnchorKind.Root2D;
            }

            return KimodoConstraintNormalizationAnchorKind.None;
        }

        private static bool IsSameFirstFrameTime(double sampleTime, double earliestTime)
        {
            return Math.Abs(sampleTime - earliestTime) <= FirstFrameTimeEpsilonSeconds;
        }

        internal static Quaternion ResolveKimodoPlanarRootRotation(KimodoMarkerSampleResult sample)
        {
            Vector3 forward = sample != null && sample.hasRootHeading
                ? new Vector3(sample.rootHeading.x, 0f, sample.rootHeading.y)
                : sample?.localAxisAngles != null && sample.localAxisAngles.Count > 0
                    ? Vector3.ProjectOnPlane(KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(sample.localAxisAngles[0]) * Vector3.forward, Vector3.up)
                    : Vector3.forward;
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        internal static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        internal static float ResolveHumanScale(Avatar avatar)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar) ||
                !KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoConstraintScaleProbe",
                    out SkeletonCache cache,
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

        private static void NormalizeConstraintOriginSample(
            KimodoMarkerSampleResult sample,
            Vector3 anchorRootPosition,
            Quaternion inverseAnchorRootRotation)
        {
            if (sample == null)
            {
                return;
            }

            sample.kimodoRootPosition = inverseAnchorRootRotation * (sample.kimodoRootPosition - anchorRootPosition);
            if (sample.hasRootHeading)
            {
                sample.rootHeading = NormalizeRootHeading(sample.rootHeading, inverseAnchorRootRotation);
            }

            if (sample.localAxisAngles == null || sample.localAxisAngles.Count == 0)
            {
                return;
            }

            Quaternion rootJointRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(sample.localAxisAngles[0]);
            Quaternion normalizedRootJointRotation = inverseAnchorRootRotation * rootJointRotation;
            sample.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(normalizedRootJointRotation);
        }

        private static Vector2 NormalizeRootHeading(Vector2 rootHeading, Quaternion inverseAnchorRootRotation)
        {
            Vector3 forward = new Vector3(rootHeading.x, 0f, rootHeading.y);
            if (forward.sqrMagnitude <= 1e-8f)
            {
                return Vector2.right;
            }

            Vector3 normalizedForward = inverseAnchorRootRotation * forward.normalized;
            Vector2 planarHeading = new Vector2(normalizedForward.x, normalizedForward.z);
            if (planarHeading.sqrMagnitude <= 1e-8f)
            {
                return Vector2.right;
            }

            planarHeading.Normalize();
            return planarHeading;
        }

    }
}
