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
            TimelineClip timelineClip,
            bool deferOutConstraint = false,
            bool deferInConstraint = false,
            int outputStartFrame = 0,
            int outputFrameCount = 0)
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
                    timelineClip,
                    deferEndConstraint: deferOutConstraint,
                    deferBeginConstraint: deferInConstraint,
                    outputStartFrame: outputStartFrame,
                    outputFrameCount: outputFrameCount);
                if (KimodoMotionModelProfiles.TryGetArdy(clip.bridgeModelName, out _) &&
                    result.BeginBoundarySample != null)
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
                IReadOnlyList<KimodoMarkerSampleResult> immediateSamples = externalConstraint.ConstraintSamples;
                var deferredSet = new HashSet<KimodoMarkerSampleResult>();
                if (deferInConstraint && externalConstraint.DeferredInConstraintSamples != null)
                    deferredSet.UnionWith(externalConstraint.DeferredInConstraintSamples);
                if (deferOutConstraint && externalConstraint.DeferredOutConstraintSamples != null)
                    deferredSet.UnionWith(externalConstraint.DeferredOutConstraintSamples);
                if (deferredSet.Count > 0)
                {
                    immediateSamples = externalConstraint.ConstraintSamples
                        .Where(sample => sample != null && !deferredSet.Contains(sample))
                        .ToList();
                }
                int externalSampleStart = result.CombinedSamples.Count;
                KimodoInOutConstraintComposer.AppendSamples(
                    immediateSamples,
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

                if (deferOutConstraint && externalConstraint.DeferredOutConstraintSamples != null &&
                    externalConstraint.DeferredOutConstraintSamples.Count > 0)
                {
                    var deferredSamples = new List<KimodoMarkerSampleResult>();
                    KimodoInOutConstraintComposer.AppendSamples(
                        externalConstraint.DeferredOutConstraintSamples,
                        deferredSamples);
                    for (int i = 0; i < deferredSamples.Count; i++)
                    {
                        deferredSamples[i].sampleTime += sampleTimeOffsetSeconds;
                    }

                    result.DeferredOutConstraintJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                        deferredSamples,
                        ResolveExportContext(timelineClip, generationContext),
                        0.0,
                        runtimeLengthSeconds,
                        frameRate);
                }
                if (deferInConstraint && externalConstraint.DeferredInConstraintSamples != null &&
                    externalConstraint.DeferredInConstraintSamples.Count > 0)
                {
                    var deferredSamplesForExport = new List<KimodoMarkerSampleResult>();
                    KimodoInOutConstraintComposer.AppendSamples(
                        externalConstraint.DeferredInConstraintSamples,
                        deferredSamplesForExport);
                    for (int i = 0; i < deferredSamplesForExport.Count; i++)
                        deferredSamplesForExport[i].sampleTime += sampleTimeOffsetSeconds;
                    result.DeferredInConstraintJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                        deferredSamplesForExport,
                        ResolveExportContext(timelineClip, generationContext),
                        0.0,
                        runtimeLengthSeconds,
                        frameRate);
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
            if (!KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    result.ConstraintsJson,
                    frameRate,
                    out string rootMotionError))
            {
                if (result.HasSyntheticAutoBeginConstraint && result.CombinedSamples.Count > 0)
                {
                    result.CombinedSamples.RemoveAt(0);
                    result.HasSyntheticAutoBeginConstraint = false;
                    result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                        result.CombinedSamples,
                        ResolveExportContext(timelineClip, generationContext),
                        0.0,
                        runtimeLengthSeconds,
                        frameRate,
                        result.DenseRootPath);
                    if (KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                            result.ConstraintsJson,
                            frameRate,
                            out _))
                    {
                        return result;
                    }
                }
                throw new InvalidOperationException($"Generation boundary constraints violate root motion limits: {rootMotionError}");
            }

            // Keep the deferred payload and its window payload in one request
            // contract.  The window is the exact authored frame/sample set; the
            // stage-3 route may reposition it without rebuilding the samples.
            result.InConstraintWindowJson = result.BeginBoundarySamples.Count > 0
                ? KimodoConstraintJsonExporter.ToConstraintsJson(
                    result.BeginBoundarySamples,
                    ResolveExportContext(timelineClip, generationContext),
                    0.0,
                    runtimeLengthSeconds,
                    frameRate)
                : string.Empty;
            result.OutConstraintWindowJson = result.EndBoundarySamples.Count > 0
                ? KimodoConstraintJsonExporter.ToConstraintsJson(
                    result.EndBoundarySamples,
                    ResolveExportContext(timelineClip, generationContext),
                    0.0,
                    runtimeLengthSeconds,
                    frameRate)
                : string.Empty;
            return result;
        }

        internal static void AppendDeferredOutSamplesForRootMotionValidation(
            IReadOnlyList<KimodoMarkerSampleResult> deferredSamples,
            List<KimodoMarkerSampleResult> samplesForValidation,
            double sampleTimeOffsetSeconds)
        {
            if (deferredSamples == null || samplesForValidation == null)
            {
                return;
            }

            var validationSamples = new List<KimodoMarkerSampleResult>();
            KimodoInOutConstraintComposer.AppendSamples(deferredSamples, validationSamples);
            for (int i = 0; i < validationSamples.Count; i++)
            {
                KimodoMarkerSampleResult sample = validationSamples[i];
                sample.sampleTime += sampleTimeOffsetSeconds;
                // Deferred Out roots still use the source clip's local baseline.
                // Post-apply calibration owns that root trajectory; only the root
                // channel is excluded from this check, while the pose stays intact.
                sample.rootOverride = null;
            }

            samplesForValidation.AddRange(validationSamples);
        }

        public KimodoInOutConstraintResult BuildDeferredOutConstraintOrThrow(
            KimodoPlayableClip clip,
            int runtimeFrameCount,
            bool disableTimelineInOut,
            double sampleTimeOffsetSeconds,
            TimelineClip timelineClip,
            int outputStartFrame = 0,
            int outputFrameCount = 0)
        {
            KimodoInOutConstraintResult result = BuildConstraintDataOrThrow(
                clip,
                runtimeFrameCount,
                disableTimelineInOut,
                deferNormalization: true,
                enableAutoBeginAnchor: false,
                sampleTimeOffsetSeconds: sampleTimeOffsetSeconds,
                timelineClipOverride: timelineClip,
                enableInOverride: false,
                enableOutOverride: true,
                deferEndConstraint: true,
                outputStartFrame: outputStartFrame,
                outputFrameCount: outputFrameCount);
            if (result.EndBoundarySamples.Count == 0)
            {
                result.ConstraintsJson = string.Empty;
                return result;
            }

            KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                timelineClip,
                out KimodoTimelineInOutConstraintContext context,
                out _);
            result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                result.EndBoundarySamples,
                ResolveExportContext(timelineClip, context),
                0.0,
                KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(
                    runtimeFrameCount,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName)),
                KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName));
            return result;
        }

        public KimodoInOutConstraintResult BuildDeferredInConstraintOrThrow(
            KimodoPlayableClip clip,
            int runtimeFrameCount,
            bool disableTimelineInOut,
            double sampleTimeOffsetSeconds,
            TimelineClip timelineClip,
            int outputStartFrame = 0,
            int outputFrameCount = 0)
        {
            KimodoInOutConstraintResult result = BuildConstraintDataOrThrow(
                clip,
                runtimeFrameCount,
                disableTimelineInOut,
                deferNormalization: true,
                enableAutoBeginAnchor: false,
                sampleTimeOffsetSeconds: sampleTimeOffsetSeconds,
                timelineClipOverride: timelineClip,
                enableInOverride: true,
                enableOutOverride: false,
                deferEndConstraint: false,
                deferBeginConstraint: true,
                outputStartFrame: outputStartFrame,
                outputFrameCount: outputFrameCount);
            if (result.BeginBoundarySamples.Count == 0)
            {
                result.ConstraintsJson = string.Empty;
                return result;
            }
            KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                timelineClip,
                out KimodoTimelineInOutConstraintContext context,
                out _);
            result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                result.BeginBoundarySamples,
                ResolveExportContext(timelineClip, context),
                0.0,
                KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(
                    runtimeFrameCount,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName)),
                KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName));
            return result;
        }

        public KimodoInOutConstraintResult BuildConstraintDataOrThrow(
            KimodoPlayableClip clip,
            int? generationFramesOverride = null,
            bool disableTimelineInOut = false,
            bool deferNormalization = false,
            bool enableAutoBeginAnchor = true,
            double sampleTimeOffsetSeconds = 0.0,
            TimelineClip timelineClipOverride = null,
            bool? enableInOverride = null,
            bool? enableOutOverride = null,
            bool deferEndConstraint = false,
            bool deferBeginConstraint = false,
            int outputStartFrame = 0,
            int outputFrameCount = 0)
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
                enableInOverride ?? (disableTimelineInOut || clip.enableInConstraint),
                enableOutOverride ?? (!disableTimelineInOut && clip.enableOutConstraint),
                generationFrames,
                sampleTimeOffsetSeconds,
                out KimodoInOutConstraintResult result,
                out string error,
                splinePathSamples,
                deferEndConstraint,
                deferBeginConstraint,
                outputStartFrame,
                outputFrameCount);

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
