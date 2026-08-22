using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

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
            built.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                built.CombinedSamples,
                new KimodoConstraintExportContext
                {
                    projectedPoseProjector = KimodoConstraintExportProjector.Create(request.ModelName)
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

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                request.TimelineContext.Track,
                request.TimelineContext.Animator,
                out Vector3 worldPosition,
                out Quaternion worldRotation);
            Quaternion worldPlanarRotation = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(worldRotation);
            Vector3 forward = worldPlanarRotation * Vector3.forward;

            sample = new KimodoMarkerSampleResult
            {
                root2DOverride = new CharacterAnimationCli.Unity.KimodoRigidTransform
                { t = new Vector3(worldPosition.x, 0f, worldPosition.z), q = worldPlanarRotation },
                constraintMode = "constraint",
                constraintMode = "root2d",
                sampleTime = 0.0,
                enableMask = new KimodoSampleChannelMask
                {
                    root2DPosition = true,
                    root2DHeading = true
                }
            };
            sample.enableMask.muscle49 = false;
            sample.enableMask.rootTQ = false;
            sample.enableMask.leftFootTQ = false;
            sample.enableMask.rightFootTQ = false;
            return true;
        }

    }
}
