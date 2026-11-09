using System;
using System.Collections.Generic;
using System.Threading;
using TimelineInject;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineClipConstraintBuilder
    {
        internal static List<KimodoClipConstraint> Build(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip,
            string modelName,
            float frameRate,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            bool includeTimelineInConstraint,
            CancellationToken token)
        {
            var result = new List<KimodoClipConstraint>();
            if (playableClip == null || timelineClip == null || targetFrameCount <= 0 || frameRate <= 0f ||
                !includeTimelineInConstraint) return result;
            if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip, out KimodoTimelineInOutConstraintContext context, out string error))
                throw new InvalidOperationException($"ClipConstraint requires Timeline sampling: {error}");

            if (playableClip.insideConstraintEnabled)
            {
                TimelineClip sourceClip = ResolveInsideSourceClip(playableClip, context);
                if (sourceClip != null)
                {
                    AnimationClip animation = (sourceClip.asset as AnimationPlayableAsset)?.clip;
                    if (animation == null) throw new InvalidOperationException("Inside requires an original AnimationClip.");
                    int availableFrames = Mathf.Max(0, Mathf.FloorToInt(animation.length * frameRate + 1e-5f));
                    int sampleFrames = Mathf.Min(targetFrameCount, availableFrames);
                    if (sampleFrames <= 0)
                        throw new InvalidOperationException("Inside ClipConstraint source has no sampleable frames.");
                    if (availableFrames != targetFrameCount)
                        Debug.LogWarning(
                            $"[Kimodo][Inside] Reference clip has {availableFrames} frames while generation requests " +
                            $"{targetFrameCount}; only the overlapping range will be constrained.",
                            playableClip);

                    var times = new double[sampleFrames];
                    var targetFrames = new List<int>(sampleFrames);
                    for (int frame = 0; frame < sampleFrames; frame++)
                    {
                        targetFrames.Add(runtimeTrimStartFrame + frame);
                        times[frame] = sourceClip.start + frame / (double)frameRate;
                    }
                    KimodoClipConstraint inside = BuildConstraint(
                        sourceClip, modelName, times, targetFrames, frameRate, "inside", token,
                        originalAnimation: true);
                    inside.postApply = playableClip.postApplyInsideConstraint;
                    inside.samplingMode = playableClip.insideKeyframeMode switch
                    {
                        KimodoInsideKeyframeMode.FootExchange => "foot_exchange",
                        KimodoInsideKeyframeMode.Mixed => "mixed",
                        _ => "uniform"
                    };
                    inside.sampleCount = Mathf.Max(1, playableClip.insideKeyframeSampleCount);
                    result.Add(inside);
                }
            }

            if (playableClip.inOutConstraintMode != KimodoInOutConstraintMode.Outside) return result;
            int inFrames = ResolveWindowFrames(
                playableClip.inConstraintWindowFrames60, playableClip.inConstraintSampleCount, frameRate, out int inSamples);
            int outFrames = ResolveWindowFrames(
                playableClip.outConstraintWindowFrames60, playableClip.outConstraintSampleCount, frameRate, out int outSamples);
            if (playableClip.enableInConstraint)
            {
                TimelineClip source = ResolveSourceClip(playableClip, context, isIn: true);
                if (source != null)
                    result.Add(BuildOutsideWindow(
                        source, modelName, frameRate, inFrames, inSamples,
                        runtimeTrimStartFrame - inFrames, true,
                        playableClip.inConstraintSourceFrame,
                        playableClip.postApplyInConstraint,
                        token));
            }
            if (playableClip.enableOutConstraint)
            {
                TimelineClip source = ResolveSourceClip(playableClip, context, isIn: false);
                if (source != null)
                    result.Add(BuildOutsideWindow(
                        source, modelName, frameRate, outFrames, outSamples,
                        runtimeTrimStartFrame + targetFrameCount, false,
                        playableClip.outConstraintSourceFrame,
                        playableClip.postApplyOutConstraint,
                        token));
            }
            return result;
        }

        private static TimelineClip ResolveInsideSourceClip(
            KimodoPlayableClip playableClip,
            KimodoTimelineInOutConstraintContext context)
        {
            if (playableClip.insideConstraintSourceTrack == null && playableClip.insideConstraintSourceAsset == null)
                return context.SourceClip;
            if (!KimodoInOutConstraintAdapter.TryResolveSourceClip(
                    context.SourceClip,
                    playableClip.insideConstraintSourceTrack,
                    playableClip.insideConstraintSourceAsset,
                    playableClip.insideConstraintSourceClipStart,
                    out TimelineClip selected,
                    out string error))
                throw new InvalidOperationException(error);
            return selected;
        }

        private static int ResolveWindowFrames(int commandFrames60, int requestedSamples, float frameRate, out int samples)
        {
            KimodoConstraintWindowSpec spec = KimodoConstraintWindowSpec.FromCommandFrames60(
                Mathf.Max(1, commandFrames60), Mathf.Max(1, requestedSamples), frameRate);
            samples = Mathf.Max(1, spec.SampleCount);
            return Mathf.Max(1, spec.ModelFrameCount);
        }

        private static TimelineClip ResolveSourceClip(
            KimodoPlayableClip playableClip,
            KimodoTimelineInOutConstraintContext context,
            bool isIn)
        {
            TrackAsset track = isIn ? playableClip.inConstraintSourceTrack : playableClip.outConstraintSourceTrack;
            PlayableAsset asset = isIn ? playableClip.inConstraintSourceAsset : playableClip.outConstraintSourceAsset;
            double clipStart = isIn
                ? playableClip.inConstraintSourceClipStart
                : playableClip.outConstraintSourceClipStart;
            if (track != null || asset != null)
            {
                if (!KimodoInOutConstraintAdapter.TryResolveSourceClip(
                        context.SourceClip, track, asset, clipStart, out TimelineClip selected, out string error))
                    throw new InvalidOperationException(error);
                return selected;
            }
            return isIn ? context.PreviousTimelineClip : context.NextTimelineClip;
        }

        private static KimodoClipConstraint BuildConstraint(
            TimelineClip sourceClip,
            string modelName,
            IReadOnlyList<double> times,
            List<int> targetFrames,
            float frameRate,
            string purpose,
            CancellationToken token,
            bool originalAnimation = false)
        {
            return new KimodoClipConstraint
            {
                motionBytes = originalAnimation
                    ? KimodoClipConstraintEncoder.EncodeOriginalAnimation(sourceClip, modelName, times.Count, frameRate, token)
                    : KimodoClipConstraintEncoder.EncodeTimelineAtTimes(
                    sourceClip, modelName, times, frameRate, token,
                    KimodoMotionModelProfiles.TryGetArdy(modelName, out _)),
                startTime = targetFrames[0] / frameRate,
                duration = targetFrames.Count / frameRate,
                mask = KimodoClipConstraintMask.FullBody(modelName, includeRoot: true),
                purpose = purpose,
                targetFrameIndices = targetFrames
            };
        }

        private static KimodoClipConstraint BuildOutsideWindow(
            TimelineClip sourceClip,
            string modelName,
            float frameRate,
            int windowFrames,
            int sampleCount,
            int targetWindowStart,
            bool isIn,
            int sourceFrame60,
            bool postApply,
            CancellationToken token)
        {
            double frameDuration = 1.0 / frameRate;
            double boundaryTime = sourceFrame60 >= 0
                ? sourceClip.start + sourceFrame60 / 60.0
                : isIn ? sourceClip.end - frameDuration : sourceClip.start;
            int availableFrames = isIn
                ? Mathf.Max(0, Mathf.FloorToInt((float)((boundaryTime - sourceClip.start) * frameRate + 1e-5)) + 1)
                : Mathf.Max(0, Mathf.FloorToInt((float)((sourceClip.end - boundaryTime) * frameRate + 1e-5)));
            int sourceWindowFrames = Mathf.Min(windowFrames, availableFrames);
            if (sourceWindowFrames <= 0)
                throw new InvalidOperationException("Outside ClipConstraint source frame/window is outside its Timeline clip.");
            if (sourceWindowFrames < windowFrames)
                Debug.LogWarning(
                    $"[Kimodo][Outside] Source clip '{sourceClip.displayName}' provides {sourceWindowFrames} " +
                    $"of {windowFrames} requested window frames; the available range will be used.",
                    sourceClip.asset);
            var times = new double[sourceWindowFrames];
            var targetFrames = new List<int>(sourceWindowFrames);
            double firstSourceTime = isIn
                ? boundaryTime - (sourceWindowFrames - 1) * frameDuration
                : boundaryTime;
            if (isIn) targetWindowStart += windowFrames - sourceWindowFrames;
            for (int index = 0; index < sourceWindowFrames; index++)
            {
                times[index] = firstSourceTime + index * frameDuration;
                targetFrames.Add(targetWindowStart + index);
            }
            KimodoClipConstraint constraint = BuildConstraint(
                sourceClip, modelName, times, targetFrames, frameRate,
                isIn ? "outside_in" : "outside_out", token);
            constraint.postApply = postApply;
            constraint.sampleCount = Mathf.Min(sampleCount, sourceWindowFrames);
            return constraint;
        }
    }
}
