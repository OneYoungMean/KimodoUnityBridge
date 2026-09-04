using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorConstraintProvider
    {
        public KimodoInOutConstraintResult BuildGenerationConstraintsOrThrow(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            int runtimeFrameCount,
            float runtimeLengthSeconds,
            float frameRate,
            bool disableTimelineInOut,
            bool deferNormalization,
            bool enableAutoBeginAnchor,
            double sampleTimeOffsetSeconds,
            TimelineClip timelineClip)
        {
            bool includeTimeline = externalConstraint?.Enabled != true ||
                externalConstraint.IncludeTimelineConstraints;
            KimodoTimelineInOutConstraintContext generationContext = null;
            if (timelineClip != null)
            {
                KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip,
                    out generationContext,
                    out _);
                CaptureTrackOffset(generationContext);
            }
            KimodoInOutConstraintResult result;
            if (includeTimeline)
            {
                result = BuildConstraintDataOrThrow(
                    clip,
                    runtimeFrameCount,
                    disableTimelineInOut,
                    deferNormalization,
                    enableAutoBeginAnchor,
                    sampleTimeOffsetSeconds,
                    timelineClip);
                if (result.BeginBoundarySample != null)
                {
                    result.CombinedSamples.Remove(result.BeginBoundarySample);
                }
            }
            else
            {
                result = new KimodoInOutConstraintResult
                {
                    ConstraintsJson = externalConstraint.ConstraintsJson ?? string.Empty
                };
            }

            if (externalConstraint?.Enabled == true)
            {
                int externalSampleStart = result.CombinedSamples.Count;
                KimodoInOutConstraintComposer.AppendSamples(
                    externalConstraint.ConstraintSamples,
                    result.CombinedSamples);
                for (int i = externalSampleStart; i < result.CombinedSamples.Count; i++)
                {
                    result.CombinedSamples[i].sampleTime += sampleTimeOffsetSeconds;
                }

                if (result.HasSyntheticAutoBeginConstraint &&
                    result.CombinedSamples.Count > 0 &&
                    KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                        result.CombinedSamples,
                        1.0,
                        result.CombinedSamples[0]))
                {
                    result.CombinedSamples.RemoveAt(0);
                    result.HasSyntheticAutoBeginConstraint = false;
                }
            }

            if (includeTimeline || result.CombinedSamples.Count > 0)
            {
                result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                    result.CombinedSamples,
                    ResolveExportContext(timelineClip, generationContext),
                    0.0,
                    runtimeLengthSeconds,
                    frameRate,
                    result.DenseRootPath);
            }
            return result;
        }

        public KimodoInOutConstraintResult BuildConstraintDataOrThrow(
            KimodoPlayableClip clip,
            int? generationFramesOverride = null,
            bool disableTimelineInOut = false,
            bool deferNormalization = false,
            bool enableAutoBeginAnchor = true,
            double sampleTimeOffsetSeconds = 0.0,
            TimelineClip timelineClipOverride = null)
        {
            TimelineClip sourceClip = timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (sourceClip == null)
            {
                return new KimodoInOutConstraintResult();
            }

            KimodoTimelineInOutConstraintContext generationContext = null;
            KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                sourceClip,
                out generationContext,
                out _);
            CaptureTrackOffset(generationContext);

            int generationFrames = generationFramesOverride ?? clip.generationFrames;
            var splinePathSamples = new List<KimodoMarkerSampleResult>();
            bool denseSplinePath = false;
            if (!KimodoSplinePathEditorBridge.TryBuildConstraintSamples(
                    clip,
                    sourceClip,
                    generationFrames,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName),
                    out splinePathSamples,
                    out denseSplinePath,
                    out string splinePathError))
            {
                throw new InvalidOperationException($"Build spline path constraints failed: {splinePathError}");
            }

            bool ok = KimodoInOutConstraintAdapter.TryBuildConstraints(
                sourceClip,
                disableTimelineInOut ? KimodoInOutConstraintMode.None : clip.inOutConstraintMode,
                enableAutoBeginAnchor && clip.autoBeginAnchor,
                deferNormalization,
                // Mode=None prevents boundary sampling; true keeps manual-marker normalization independent of the In toggle.
                disableTimelineInOut || clip.enableInConstraint,
                !disableTimelineInOut && clip.enableOutConstraint,
                generationFrames,
                sampleTimeOffsetSeconds,
                out KimodoInOutConstraintResult result,
                out string error,
                splinePathSamples);

            if (!ok)
            {
                throw new InvalidOperationException($"Build constraints failed: {error}");
            }

            result ??= new KimodoInOutConstraintResult();
            if (splinePathSamples.Count > 0)
            {
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName);
                result.DenseRootPath = denseSplinePath;
                result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                    result.CombinedSamples,
                    ResolveExportContext(sourceClip, generationContext),
                    clipStartSeconds: 0.0,
                    clipDurationSeconds: KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(generationFrames, frameRate),
                    exportFps: frameRate,
                    denseRootPath: denseSplinePath);
            }
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
//touch 7ec98321-518c-4133-8a2b-0e9dcc4436b4
