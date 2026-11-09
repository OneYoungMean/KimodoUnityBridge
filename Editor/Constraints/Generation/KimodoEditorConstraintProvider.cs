using System;
using System.Collections.Generic;
using System.Linq;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorConstraintProvider
    {
        internal sealed class ConstraintData
        {
            public List<KimodoMarkerSampleResult> Samples = new List<KimodoMarkerSampleResult>();
            public string Json = string.Empty;
        }

        public ConstraintData BuildGenerationConstraintsOrThrow(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            int runtimeFrameCount,
            float runtimeLengthSeconds,
            float frameRate,
            double sampleTimeOffsetSeconds,
            TimelineClip timelineClip)
        {
            var result = new ConstraintData();
            bool includeTimeline = externalConstraint?.Enabled != true || externalConstraint.IncludeTimelineConstraints;
            bool denseRootPath = false;
            KimodoTimelineInOutConstraintContext context = null;
            KimodoInOutConstraintAdapter.TryResolveTimelineContext(timelineClip, out context, out _);
            CaptureTrackOffset(context);
            if (includeTimeline)
            {
                if (!KimodoTimelineConstraintMarkerSampler.TryBuildMarkerSamplesForExport(
                        context, out var markerSamples, out string markerError))
                    throw new InvalidOperationException($"Build marker constraints failed: {markerError}");
                if (!KimodoSplinePathEditorBridge.TryBuildConstraintSamples(
                        clip, timelineClip, runtimeFrameCount, frameRate,
                        out var splineSamples, out denseRootPath, out string splineError))
                    throw new InvalidOperationException($"Build spline path constraints failed: {splineError}");
                markerSamples.AddRange(splineSamples);
                result.Samples = KimodoInOutConstraintTools.BuildLocalManualSamples(
                    markerSamples, timelineClip.start, sampleTimeOffsetSeconds);
            }
            if (externalConstraint?.Enabled == true)
            {
                result.Samples.AddRange(externalConstraint.ConstraintSamples
                    .Where(sample => sample != null).Select(sample =>
                    {
                        var copy = sample.Clone();
                        copy.sampleTime += sampleTimeOffsetSeconds;
                        return copy;
                    }));
            }
            result.Json = includeTimeline || result.Samples.Count > 0
                ? KimodoConstraintJsonExporter.ToConstraintsJson(
                    result.Samples, ResolveExportContext(timelineClip, context),
                    0.0, runtimeLengthSeconds, frameRate, denseRootPath)
                : externalConstraint?.ConstraintsJson ?? string.Empty;
            if (!KimodoConstraintRootMotionValidator.TryValidateRawConstraintRootMotion(result.Json, frameRate, out string error))
                throw new InvalidOperationException($"Generation constraints violate root motion limits: {error}");
            return result;
        }

        public TimelineClip FindTimelineClipForAsset(PlayableAsset asset)
        {
            return KimodoTimelineClipResolver.FindTimelineClipForAsset(asset);
        }

        public GameObject FindTimelineBindingObjectForAsset(
            PlayableAsset asset,
            TimelineClip timelineClipOverride = null)
        {
            TimelineClip sourceClip = timelineClipOverride ?? FindTimelineClipForAsset(asset);
            if (sourceClip == null)
            {
                return null;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                return null;
            }

            if (!KimodoInOutConstraintAdapter.TryResolveDirector(
                    sourceClip,
                    track,
                    out PlayableDirector director,
                    out _))
            {
                return null;
            }

            TrackAsset currentTrack = track;
            while (currentTrack != null)
            {
                UnityEngine.Object binding = director.GetGenericBinding(currentTrack);
                if (binding is Animator animator && animator != null)
                {
                    return animator.gameObject;
                }

                if (binding is GameObject go && go != null)
                {
                    return go;
                }

                currentTrack = currentTrack.parent as TrackAsset;
            }

            return null;
        }
        private static KimodoConstraintExportContext ResolveExportContext(
            TimelineClip timelineClip,
            KimodoTimelineInOutConstraintContext resolvedContext = null)
        {
            KimodoTimelineInOutConstraintContext context = resolvedContext;
            if (context == null && timelineClip != null)
            {
                KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip,
                    out context,
                    out _);
            }
            if (context == null)
            {
                return new KimodoConstraintExportContext();
            }

            return new KimodoConstraintExportContext
            {
                projectedPoseProjector = KimodoConstraintExportProjector.Create(context)
            };
        }

        private static void CaptureTrackOffset(KimodoTimelineInOutConstraintContext context)
        {
            if (context == null)
            {
                return;
            }

            KimodoTimelineTrackOffsetUtility.CaptureWorldOffset(
                context.Track,
                context.Animator,
                out context.TrackOffsetPosition,
                out context.TrackOffsetRotation,
                out _);
            context.HasTrackOffsetSnapshot = true;
        }
    }

}
