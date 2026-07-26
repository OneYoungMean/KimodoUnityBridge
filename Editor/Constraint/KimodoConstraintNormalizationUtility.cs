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
            public bool IsCustomAnchor;
            public KimodoConstraintNormalizationAnchorKind AnchorKind = KimodoConstraintNormalizationAnchorKind.None;
            public KimodoMarkerSampleResult AnchorSample;
            public Vector3 RootPosition = Vector3.zero;
            public Quaternion InverseRootRotation = Quaternion.identity;
        }

        internal static void NormalizeConstraintOrigin(
            List<KimodoMarkerSampleResult> samples,
            out KimodoConstraintNormalizationInfo normalizationInfo,
            out string warning)
        {
            normalizationInfo = new KimodoConstraintNormalizationInfo();
            warning = string.Empty;
            ResolvedNormalizationAnchor anchor = ResolveNormalizationAnchor(samples, out warning);
            ApplyNormalizationAnchor(samples, anchor);
            normalizationInfo = BuildNormalizationInfo(anchor);
        }

        private static ResolvedNormalizationAnchor ResolveNormalizationAnchor(
            List<KimodoMarkerSampleResult> samples,
            out string warning)
        {
            warning = string.Empty;
            var resolved = new ResolvedNormalizationAnchor();
            if (TryResolveCustomConstraintOriginAnchor(
                    samples,
                    out KimodoMarkerSampleResult anchor,
                    out KimodoConstraintNormalizationAnchorKind anchorKind,
                    out warning))
            {
                resolved.IsCustomAnchor = anchor != null;
                resolved.AnchorKind = anchorKind;
                resolved.AnchorSample = anchor != null ? anchor.Clone() : null;
                if (anchor != null)
                {
                    resolved.RootPosition = new Vector3(anchor.unityRootPos.x, 0f, anchor.unityRootPos.z);
                    resolved.InverseRootRotation = Quaternion.Inverse(ResolvePlanarRootRotation(anchor));
                }
            }

            return resolved;
        }

        private static bool TryResolveCustomConstraintOriginAnchor(
            List<KimodoMarkerSampleResult> samples,
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
                if (sample != null && sample.sampleTime < earliestTime)
                {
                    earliestTime = sample.sampleTime;
                }
            }

            if (earliestTime == double.MaxValue)
            {
                return false;
            }

            var sameFrameFullBody = new List<KimodoMarkerSampleResult>();
            var sameFrameRoot2D = new List<KimodoMarkerSampleResult>();
            var sameFrameFoot = new List<KimodoMarkerSampleResult>();

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null || !IsSameFirstFrameTime(sample.sampleTime, earliestTime))
                {
                    continue;
                }

                switch (ResolveAnchorPriority(sample))
                {
                    case KimodoConstraintNormalizationAnchorKind.FullBody:
                        sameFrameFullBody.Add(sample);
                        break;
                    case KimodoConstraintNormalizationAnchorKind.Root2D:
                        sameFrameRoot2D.Add(sample);
                        break;
                    case KimodoConstraintNormalizationAnchorKind.Foot:
                        sameFrameFoot.Add(sample);
                        break;
                }
            }

            if (sameFrameFullBody.Count > 0)
            {
                anchor = sameFrameFullBody[0];
                anchorKind = KimodoConstraintNormalizationAnchorKind.FullBody;
                if (sameFrameFullBody.Count > 1)
                {
                    warning = "Multiple fullbody constraints were found on the first frame; using the first one as the normalization anchor.";
                }

                return true;
            }

            if (sameFrameRoot2D.Count > 0 || sameFrameFoot.Count > 0)
            {
                var sameFramePlanar = new List<KimodoMarkerSampleResult>(sameFrameRoot2D.Count + sameFrameFoot.Count);
                sameFramePlanar.AddRange(sameFrameRoot2D);
                sameFramePlanar.AddRange(sameFrameFoot);
                sameFramePlanar.Sort((left, right) => left.sampleTime.CompareTo(right.sampleTime));

                anchor = sameFramePlanar[0];
                anchorKind = ResolveAnchorPriority(anchor);
                if (sameFramePlanar.Count > 1)
                {
                    warning = "Multiple root2d/foot constraints were found on the first frame; using the first one as the normalization anchor.";
                }

                return true;
            }

            return false;
        }

        private static void ApplyNormalizationAnchor(
            List<KimodoMarkerSampleResult> samples,
            ResolvedNormalizationAnchor anchor)
        {
            if (samples == null || samples.Count == 0)
            {
                return;
            }

            Vector3 anchorRootPosition = anchor != null ? anchor.RootPosition : Vector3.zero;
            Quaternion inverseAnchorRootRotation = anchor != null ? anchor.InverseRootRotation : Quaternion.identity;
            for (int i = 0; i < samples.Count; i++)
            {
                NormalizeConstraintOriginSample(samples[i], anchorRootPosition, inverseAnchorRootRotation);
            }
        }

        private static KimodoConstraintNormalizationInfo BuildNormalizationInfo(ResolvedNormalizationAnchor anchor)
        {
            return new KimodoConstraintNormalizationInfo
            {
                Applied = anchor != null && anchor.IsCustomAnchor,
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

        internal static Quaternion ResolvePlanarRootRotation(KimodoMarkerSampleResult sample)
        {
            Vector3 forward = sample != null && sample.hasRootHeading
                ? new Vector3(sample.rootHeading.x, 0f, sample.rootHeading.y)
                : Vector3.ProjectOnPlane((sample != null ? sample.unityRootRot : Quaternion.identity) * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
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

            Quaternion rootJointRotation = AxisAngleToQuaternion(sample.localAxisAngles[0]);
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

        private static Quaternion AxisAngleToQuaternion(Vector3 axisAngle)
        {
            float angleRad = axisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Quaternion.identity;
            }

            Vector3 axis = axisAngle / angleRad;
            return Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
        }
    }
}
