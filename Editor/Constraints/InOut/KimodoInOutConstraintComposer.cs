using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

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
            built.BeginBoundaryWindowModelFrameCount =
                KimodoInOutConstraintTools.ResolveWindowFrameCount(request, isBegin: true);
            built.EndBoundaryWindowModelFrameCount =
                KimodoInOutConstraintTools.ResolveWindowFrameCount(request, isBegin: false);

            if (!KimodoInOutConstraintTools.TrySampleBoundaries(
                    request,
                    out List<KimodoMarkerSampleResult> beginSamples,
                    out List<KimodoMarkerSampleResult> endSamples,
                    out warning,
                    out error))
            {
                return false;
            }

            if (beginSamples.Count > 0)
            {
                // Keep the boundary first so the previous Timeline frame wins same-frame conflicts.
                // ARDY still promotes its single In sample to a ClipConstraint.
                built.BeginBoundarySample = beginSamples[0];
                built.BeginBoundarySamples.AddRange(beginSamples);
                if (!request.DeferBeginConstraint)
                {
                    built.CombinedSamples.AddRange(beginSamples);
                }
            }

            AppendSamples(request.ManualSamples, built.CombinedSamples);

            if (endSamples.Count > 0 &&
                !request.DeferEndConstraint &&
                KimodoInOutConstraintTools.ClampFrameCount(request.GenerationFrames) > 1)
            {
                built.CombinedSamples.AddRange(endSamples);
            }
            built.EndBoundarySamples.AddRange(endSamples);

            double normalizationAnchorWindowSeconds = request.AutoBeginAnchor
                ? AutoBeginAnchorWindowSeconds
                : double.PositiveInfinity;
            KimodoMarkerSampleResult autoBegin = null;
            // AutoBegin is a synthetic normalization anchor for the first pass.
            // Deferring the authored In boundary must not suppress this anchor;
            // the deferred boundary is appended separately after pass 1.
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
                var withAutoBegin = new List<KimodoMarkerSampleResult>(built.CombinedSamples.Count + 1)
                {
                    autoBegin
                };
                withAutoBegin.AddRange(built.CombinedSamples);
                bool autoBeginWithinRawMotionLimits = KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                        ExportSamples(request, withAutoBegin),
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName),
                        out _);
                if (autoBeginWithinRawMotionLimits)
                {
                    built.CombinedSamples.Insert(0, autoBegin);
                    built.HasSyntheticAutoBeginConstraint = true;
                }
                // Auto Begin is a synthetic normalization anchor. A raw speed or
                // acceleration violation removes this optional anchor; authored
                // boundary constraints are validated below and still report errors.
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
                    // constraint request. Real Timeline generation uses the
                    // bound Character projector above.
                    projectedPoseProjector = projectedPoseProjector
                },
                clipStartSeconds: 0.0,
                clipDurationSeconds: clipDurationSeconds,
                exportFps: generationFrameRate);
            if (!KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    built.ConstraintsJson,
                    generationFrameRate,
                    out error))
            {
                error = $"Boundary constraints violate root motion limits: {error}";
                return false;
            }

            result = built;
            return true;
        }

        private static string ExportSamples(
            KimodoInOutConstraintRequest request,
            IReadOnlyList<KimodoMarkerSampleResult> samples)
        {
            float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName);
            Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> projector =
                request.TimelineContext?.Animator != null
                    ? KimodoConstraintExportProjector.Create(request.TimelineContext)
                    : KimodoConstraintExportProjector.CreateProfileNative(request.ModelName);
            return KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                new KimodoConstraintExportContext { projectedPoseProjector = projector },
                0.0,
                KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(request.GenerationFrames, frameRate),
                frameRate);
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

            KimodoTimelineInOutConstraintContext context = request.TimelineContext;
            if (context.Animator == null)
            {
                error = "Auto Begin Root2D constraint requires the bound Character Animator.";
                return false;
            }

            if (!KimodoConstraintPoseUtility.TryCreateRestPoseSample(context, out sample, out error))
                return false;

            if (!context.HasOriginalRootSnapshot)
            {
                context.HasOriginalRootSnapshot = KimodoInOutConstraintTools.TryCaptureOriginalRoot2D(
                    context.Animator,
                    context.Director,
                    out context.OriginalRootPosition,
                    out context.OriginalRootRotation);
                if (!context.HasOriginalRootSnapshot)
                {
                    error = "Auto Begin Root2D constraint could not resolve the scene character root.";
                    return false;
                }
            }

            // Keep the valid rest-pose payload as Root2D's support sample, but
            // target the character's original scene root. Root2D only consumes
            // the planar root position and heading, so a scene T-pose cannot
            // become an accidental FullBody starting constraint.
            sample.rootOverride.t = context.OriginalRootPosition;
            sample.rootOverride.q = context.OriginalRootRotation.normalized;
            sample.constraintMode = "root2d";
            sample.sampleTime = 0.0;
            sample.enableMask = KimodoConstraintMask.ForType("root2d");
            sample.sampleValid = true;
            sample.enabled = true;
            return true;
        }

    }
}
