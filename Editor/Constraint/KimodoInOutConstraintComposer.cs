using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintComposer
    {
        private const double AutoBeginAnchorWindowSeconds = 1.0;

        internal static bool TryBuild(
            KimodoInOutConstraintRequest request,
            out KimodoInOutConstraintResult result,
            out string warning,
            out string error)
        {
            result = null;
            warning = string.Empty;
            error = string.Empty;

            if (request == null)
            {
                error = "InOut constraint request is null.";
                return false;
            }

            var built = new KimodoInOutConstraintResult();

            if (!KimodoInOutConstraintTools.TrySampleBoundaryPair(
                    request,
                    out KimodoMarkerSampleResult beginSample,
                    out KimodoMarkerSampleResult endSample,
                    out warning,
                    out error))
            {
                return false;
            }

            if (beginSample != null)
            {
                // Clip constraints are only a sampling source. Downstream they are ordinary fullbody samples.
                // Add begin before begin-time markers so beginTime - 1 frame wins same-frame normalization ties.
                built.CombinedSamples.Add(beginSample);
            }

            AppendManualSamples(request.ManualSamples, built.CombinedSamples);

            if (endSample != null &&
                KimodoInOutConstraintAdapter.ClampFrameCount(request.GenerationFrames) > 1)
            {
                built.CombinedSamples.Add(endSample);
            }

            double normalizationAnchorWindowSeconds = request.AutoBeginAnchor
                ? AutoBeginAnchorWindowSeconds
                : double.PositiveInfinity;
            List<KimodoMarkerSampleResult> normalizationSamples = built.CombinedSamples;
            if (request.AutoBeginAnchor &&
                !KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    normalizationSamples,
                    normalizationAnchorWindowSeconds) &&
                !TryBuildAutoBeginAnchorSample(request, out built.AutoBeginAnchorSample, out error))
            {
                return false;
            }

            if (!request.DeferNormalization)
            {
                KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                    normalizationSamples,
                    built.AutoBeginAnchorSample,
                    normalizationAnchorWindowSeconds,
                    out KimodoConstraintNormalizationInfo normalizationInfo,
                    out string normalizeWarning);
                built.NormalizationInfo = normalizationInfo ?? new KimodoConstraintNormalizationInfo();
                if (!string.IsNullOrWhiteSpace(normalizeWarning))
                {
                    warning = string.IsNullOrWhiteSpace(warning)
                        ? normalizeWarning
                        : $"{warning}\n{normalizeWarning}";
                }
            }

            if (built.NormalizationInfo != null && built.NormalizationInfo.Applied && built.NormalizationInfo.AnchorSample != null)
            {
                KimodoMarkerSampleResult rawAnchor = built.NormalizationInfo.AnchorSample;
                KimodoMarkerSampleResult normalizedAnchor = FindNormalizedAnchorSample(normalizationSamples, rawAnchor);
                Quaternion kimodoAnchorRotation = KimodoConstraintNormalizationUtility.ResolveKimodoPlanarRootRotation(rawAnchor);
                Vector3 kimodoAnchorPosition = new Vector3(rawAnchor.kimodoRootPosition.x, 0f, rawAnchor.kimodoRootPosition.z);
                Vector3 rebuiltRoot = normalizedAnchor != null
                    ? kimodoAnchorPosition + kimodoAnchorRotation * normalizedAnchor.kimodoRootPosition
                    : Vector3.zero;
                float rebuiltRotationDelta = normalizedAnchor?.localAxisAngles != null &&
                    normalizedAnchor.localAxisAngles.Count > 0 &&
                    rawAnchor.localAxisAngles != null &&
                    rawAnchor.localAxisAngles.Count > 0
                        ? Quaternion.Angle(
                            kimodoAnchorRotation * KimodoConstraintNormalizationUtility.AxisAngleToQuaternion(normalizedAnchor.localAxisAngles[0]),
                            KimodoConstraintNormalizationUtility.AxisAngleToQuaternion(rawAnchor.localAxisAngles[0]))
                        : 0f;
                Debug.Log(
                    $"[Kimodo][ConstraintNormalize] applied=true anchorKind={built.NormalizationInfo.AnchorKind} " +
                    $"anchorType='{rawAnchor.constraintType}' anchorTime={rawAnchor.sampleTime:R} " +
                    $"exportFrame={KimodoFrameTimeUtility.SecondsToFrameIndex(rawAnchor.sampleTime, KimodoPlayableClip.FIXED_FRAME_RATE)} " +
                    $"targetAvatarRoot={rawAnchor.kimodoRootPosition:F6} " +
                    $"worldRoot={rawAnchor.unityRootPos:F6} worldRotation={rawAnchor.unityRootRot.eulerAngles:F6} " +
                    $"normalizedRoot={(normalizedAnchor != null ? normalizedAnchor.kimodoRootPosition.ToString("F6") : "(auto-begin)")} " +
                    $"rebuiltKimodoRoot={(normalizedAnchor != null ? rebuiltRoot.ToString("F6") : "(auto-begin)")} " +
                    $"rootPositionDelta={(normalizedAnchor != null ? Vector3.Distance(rebuiltRoot, rawAnchor.kimodoRootPosition) : 0f):F8} " +
                    $"rootRotationDeltaDeg={rebuiltRotationDelta:F6}");
            }

            double clipDurationSeconds = KimodoInOutConstraintAdapter.ResolveConstraintClipDurationSeconds(request.GenerationFrames);
            built.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                built.CombinedSamples,
                clipStartSeconds: 0.0,
                clipDurationSeconds: clipDurationSeconds);

            result = built;
            return true;
        }

        private static void AppendManualSamples(
            List<KimodoMarkerSampleResult> source,
            List<KimodoMarkerSampleResult> destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                KimodoMarkerSampleResult sample = source[i];
                if (sample != null)
                {
                    destination.Add(sample.Clone());
                }
            }
        }

        private static long ToTimeKey(double sampleTime)
        {
            return (long)System.Math.Round(sampleTime * 1000000.0);
        }

        private static bool TryBuildAutoBeginAnchorSample(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (request?.TimelineContext == null)
            {
                error = "Auto Begin anchor requires a Timeline context.";
                return false;
            }

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                request.TimelineContext.Track,
                request.TimelineContext.Animator,
                out Vector3 worldPosition,
                out Quaternion worldRotation);
            Quaternion worldPlanarRotation = ResolvePlanarRotation(worldRotation);
            float scale = Mathf.Max(1e-6f, request.KimodoHumanScale) /
                Mathf.Max(1e-6f, request.SourceHumanScale);
            Vector3 kimodoPosition = new Vector3(worldPosition.x, 0f, worldPosition.z) * scale;
            Vector3 forward = worldPlanarRotation * Vector3.forward;

            sample = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = kimodoPosition,
                unityRootPos = worldPosition,
                unityRootRot = worldPlanarRotation,
                hasRootHeading = true,
                rootHeading = new Vector2(forward.x, forward.z),
                localAxisAngles = new List<Vector3>
                {
                    KimodoRuntimeUtility.QuaternionToAxisAngleVector(worldPlanarRotation)
                },
                sampledJointIndices = new List<int> { 0 },
                jointNames = new List<string>()
            };
            return true;
        }

        private static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private static KimodoMarkerSampleResult FindNormalizedAnchorSample(
            List<KimodoMarkerSampleResult> samples,
            KimodoMarkerSampleResult rawAnchor)
        {
            if (samples == null || rawAnchor == null)
            {
                return null;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample != null &&
                    ToTimeKey(sample.sampleTime) == ToTimeKey(rawAnchor.sampleTime) &&
                    string.Equals(sample.constraintType, rawAnchor.constraintType, System.StringComparison.OrdinalIgnoreCase))
                {
                    return sample;
                }
            }

            return null;
        }
    }
}
