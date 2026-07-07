using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintNormalizationUtility
    {
        private const double FirstFrameTimeEpsilonSeconds = 1e-4d;

        internal static void NormalizeConstraintOrigin(
            List<KimodoMarkerSampleResult> samples,
            IReadOnlyList<KimodoMarkerSampleResult> inOutAnchorCandidates,
            out KimodoConstraintNormalizationInfo normalizationInfo,
            out string warning)
        {
            normalizationInfo = new KimodoConstraintNormalizationInfo();
            warning = string.Empty;
            if (!TryResolveConstraintOriginAnchorSample(
                    samples,
                    inOutAnchorCandidates,
                    out KimodoMarkerSampleResult anchor,
                    out KimodoConstraintNormalizationAnchorKind anchorKind,
                    out warning))
            {
                return;
            }

            KimodoMarkerSampleResult anchorSnapshot = anchor != null ? anchor.Clone() : null;
            Vector3 anchorRootPosition = anchor.unityRootPos;
            Quaternion inverseAnchorRootRotation = Quaternion.Inverse(anchor.unityRootRot);
            for (int i = 0; i < samples.Count; i++)
            {
                NormalizeConstraintOriginSample(samples[i], anchorRootPosition, inverseAnchorRootRotation);
            }

            normalizationInfo.Applied = true;
            normalizationInfo.AnchorKind = anchorKind;
            normalizationInfo.AnchorSample = anchorSnapshot;
        }

        internal static void CopyPoseAxes(KimodoMarkerSampleResult sourceSample, KimodoMarkerSampleResult destinationSample)
        {
            if (sourceSample == null || destinationSample == null)
            {
                return;
            }

            destinationSample.localAxisAngles = sourceSample.localAxisAngles != null
                ? new List<Vector3>(sourceSample.localAxisAngles)
                : new List<Vector3>();
            destinationSample.sampledJointIndices = sourceSample.sampledJointIndices != null
                ? new List<int>(sourceSample.sampledJointIndices)
                : new List<int>();
            destinationSample.jointNames = sourceSample.jointNames != null
                ? new List<string>(sourceSample.jointNames)
                : new List<string>();
        }

        private static bool TryResolveConstraintOriginAnchorSample(
            List<KimodoMarkerSampleResult> samples,
            IReadOnlyList<KimodoMarkerSampleResult> inOutAnchorCandidates,
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

            if (!TryResolveConstraintOriginAnchor(
                    samples,
                    inOutAnchorCandidates,
                    out anchor,
                    out anchorKind,
                    out warning))
            {
                return false;
            }

            return anchor != null;
        }

        private static bool TryResolveConstraintOriginAnchor(
            List<KimodoMarkerSampleResult> samples,
            IReadOnlyList<KimodoMarkerSampleResult> inOutAnchorCandidates,
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

            if (inOutAnchorCandidates != null)
            {
                for (int i = 0; i < inOutAnchorCandidates.Count; i++)
                {
                    KimodoMarkerSampleResult candidate = inOutAnchorCandidates[i];
                    if (candidate != null && candidate.sampleTime < earliestTime)
                    {
                        earliestTime = candidate.sampleTime;
                    }
                }
            }

            if (earliestTime == double.MaxValue)
            {
                return false;
            }

            var sameFrameInOut = new List<KimodoMarkerSampleResult>();
            var sameFrameFullBody = new List<KimodoMarkerSampleResult>();
            var sameFrameRoot2D = new List<KimodoMarkerSampleResult>();
            var sameFrameFoot = new List<KimodoMarkerSampleResult>();

            if (inOutAnchorCandidates != null)
            {
                for (int i = 0; i < inOutAnchorCandidates.Count; i++)
                {
                    KimodoMarkerSampleResult candidate = inOutAnchorCandidates[i];
                    if (candidate == null || !IsSameFirstFrameTime(candidate.sampleTime, earliestTime))
                    {
                        continue;
                    }

                    sameFrameInOut.Add(candidate);
                }
            }

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

            if (sameFrameInOut.Count > 0)
            {
                anchor = sameFrameInOut[0];
                anchorKind = KimodoConstraintNormalizationAnchorKind.InOut;
                if (sameFrameInOut.Count > 1)
                {
                    warning = "Multiple in/out boundary constraints were found on the first frame; using the first one as the normalization anchor.";
                }

                return true;
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
