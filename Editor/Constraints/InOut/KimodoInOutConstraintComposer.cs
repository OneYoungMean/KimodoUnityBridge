using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintComposer
    {
        private const double AutoBeginAnchorWindowSeconds = 1.0;
        private const string Root2DConstraintType = "root2d";

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
                // Keep the boundary first so the previous Timeline frame wins same-frame conflicts.
                // Generation hosts may promote this sample to a one-frame ClipConstraint.
                built.CombinedSamples.Add(beginSample);
                built.BeginBoundarySample = beginSample;
            }

            AppendSamples(request.ManualSamples, built.CombinedSamples);

            if (endSample != null &&
                KimodoInOutConstraintTools.ClampFrameCount(request.GenerationFrames) > 1)
            {
                built.CombinedSamples.Add(endSample);
            }

            double normalizationAnchorWindowSeconds = request.AutoBeginAnchor
                ? AutoBeginAnchorWindowSeconds
                : double.PositiveInfinity;
            KimodoMarkerSampleResult autoBegin = null;
            if (request.AutoBeginAnchor &&
                !KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    built.CombinedSamples,
                    normalizationAnchorWindowSeconds) &&
                !TryBuildAutoBeginConstraint(request, out autoBegin, out error))
            {
                return false;
            }
            else if (autoBegin != null)
            {
                built.CombinedSamples.Insert(0, autoBegin);
                built.HasSyntheticAutoBeginConstraint = true;
            }

            float generationFrameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName);
            double clipDurationSeconds = KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(
                request.GenerationFrames,
                generationFrameRate);
            Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> projectedPoseProjector =
                request.TimelineContext?.Animator != null
                    ? KimodoConstraintExportProjector.Create(request.TimelineContext)
                    : KimodoConstraintExportProjector.CreateProfileNative(request.ModelName);
            built.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                built.CombinedSamples,
                new KimodoConstraintExportContext
                {
                    // Lightweight editor/tests can construct a track-only
                    // Root2D request. Real Timeline generation always uses the
                    // bound Character projector above.
                    projectedPoseProjector = projectedPoseProjector
                },
                clipStartSeconds: 0.0,
                clipDurationSeconds: clipDurationSeconds,
                exportFps: generationFrameRate);

            result = built;
            return true;
        }

        internal static void AppendSamples(
            IReadOnlyList<KimodoMarkerSampleResult> source,
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

        private static bool TryBuildAutoBeginConstraint(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (request?.TimelineContext == null)
            {
                error = "Auto Begin constraint requires a Timeline context.";
                return false;
            }

            TimelineClip sourceClip = request.TimelineContext.SourceClip;
            bool hasTimelineSamplingContext = sourceClip != null &&
                request.TimelineContext.Director != null &&
                request.TimelineContext.Animator != null;
            double timelineStart = sourceClip != null
                ? Math.Max(0.0, sourceClip.start)
                : 0.0;
            if (hasTimelineSamplingContext)
            {
                if (!KimodoTimelineConstraintSampler.TrySampleMarker(
                        request.TimelineContext,
                        timelineStart,
                        0.0,
                        Root2DConstraintType,
                        request.ModelName,
                        out sample,
                        out error))
                {
                    return false;
                }
            }
            else
            {
                // Keep the lightweight construction usable for editor/test
                // callers that only provide an AnimationTrack. Real Timeline
                // generation always has a complete sampling context above.
                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    request.TimelineContext.Track,
                    request.TimelineContext.Animator,
                    out Vector3 worldPosition,
                    out Quaternion worldRotation);
                sample = new KimodoMarkerSampleResult
                {
                    rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = worldPosition,
                        q = worldRotation
                    },
                    enableMask = new KimodoConstraintMask { rootPosition = true, rootHeading = true },
                    validMask = new KimodoConstraintMask { rootPosition = true, rootHeading = true }
                };
            }

            if (sample?.rootOverride == null)
            {
                error = "Auto Begin Root2D sampling returned no world root.";
                return false;
            }

            // Root2D is an application mode, not a reduced payload format.
            // AutoBegin therefore retains the sampled complete root transform.
            sample.constraintMode = Root2DConstraintType;
            sample.sampleTime = 0.0;
            sample.enableMask = new KimodoConstraintMask
            {
                rootPosition = true,
                rootHeading = true
            };
            sample.validMask = new KimodoConstraintMask
            {
                rootPosition = true,
                rootHeading = true
            };
            sample.effectors = new KimodoConstraintEffectors();
            return true;
        }

    }
}
