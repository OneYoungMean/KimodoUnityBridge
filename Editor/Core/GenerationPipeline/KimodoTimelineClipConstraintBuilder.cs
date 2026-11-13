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
                AnimationClip directAnimation = playableClip.insideConstraintSourceClip;
                TimelineClip sourceClip = directAnimation == null
                    ? ResolveInsideSourceClip(playableClip, context)
                    : null;
                if (sourceClip != null || directAnimation != null)
                {
                    // Unity missing references compare equal to null but are not handled by ??.
                    AnimationClip animation = directAnimation != null
                        ? directAnimation
                        : (sourceClip.asset as AnimationPlayableAsset)?.clip;
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
                        times[frame] = (sourceClip != null ? sourceClip.start : 0.0) + frame / (double)frameRate;
                    }
                    KimodoClipConstraint inside = sourceClip != null
                        ? BuildConstraint(sourceClip, modelName, times, targetFrames, frameRate, "inside", token, originalAnimation: true)
                        : BuildConstraint(
                            animation,
                            ResolveSourceAvatar(context),
                            modelName,
                            times,
                            targetFrames,
                            frameRate,
                            "inside",
                            token,
                            includeFootContacts: true);
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
                AnimationClip direct = playableClip.inConstraintSourceClip;
                TimelineClip source = direct == null ? ResolveSourceClip(playableClip, context, isIn: true) : null;
                if (direct != null)
                    result.Add(BuildOutsideWindow(
                        direct, ResolveSourceAvatar(context), modelName, frameRate, inFrames, inSamples,
                        runtimeTrimStartFrame - inFrames, true,
                        playableClip.inConstraintSourceFrame,
                        playableClip.postApplyInConstraint,
                        token));
                else if (source != null)
                    result.Add(BuildOutsideWindow(
                        source, modelName, frameRate, inFrames, inSamples,
                        runtimeTrimStartFrame - inFrames, true,
                        playableClip.inConstraintSourceFrame,
                        playableClip.postApplyInConstraint,
                        token));
            }
            if (playableClip.enableOutConstraint)
            {
                AnimationClip direct = playableClip.outConstraintSourceClip;
                TimelineClip source = direct == null ? ResolveSourceClip(playableClip, context, isIn: false) : null;
                if (direct != null)
                    result.Add(BuildOutsideWindow(
                        direct, ResolveSourceAvatar(context), modelName, frameRate, outFrames, outSamples,
                        runtimeTrimStartFrame + targetFrameCount, false,
                        playableClip.outConstraintSourceFrame,
                        playableClip.postApplyOutConstraint,
                        token));
                else if (source != null)
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

        private static Avatar ResolveSourceAvatar(KimodoTimelineInOutConstraintContext context)
        {
            KimodoLocalAvatarUtility.AvatarResolveResult result =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(context.Track, context.Animator);
            if (!result.IsHumanoid || !KimodoRetargetCoreUtility.IsValidHumanoid(result.Avatar))
                throw new InvalidOperationException($"External Clip constraint requires a valid source Avatar: {result.Error}");
            return result.Avatar;
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

        private static KimodoClipConstraint BuildConstraint(
            AnimationClip animation,
            Avatar sourceAvatar,
            string modelName,
            IReadOnlyList<double> times,
            List<int> targetFrames,
            float frameRate,
            string purpose,
            CancellationToken token,
            bool includeFootContacts)
        {
            return new KimodoClipConstraint
            {
                motionBytes = KimodoClipConstraintEncoder.EncodeOriginalAnimationAtTimes(
                    animation, sourceAvatar, modelName, times, frameRate, token, includeFootContacts),
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

        private static KimodoClipConstraint BuildOutsideWindow(
            AnimationClip animation,
            Avatar sourceAvatar,
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
            if (animation == null)
                throw new InvalidOperationException("Outside source AnimationClip is missing.");
            double frameDuration = 1.0 / frameRate;
            int availableFrames = Mathf.Max(0, Mathf.FloorToInt(animation.length * frameRate + 1e-5f));
            double boundaryTime = sourceFrame60 >= 0
                ? sourceFrame60 / 60.0
                : isIn ? Math.Max(0, availableFrames - 1) * frameDuration : 0.0;
            int boundaryFrame = Mathf.Clamp(Mathf.RoundToInt((float)(boundaryTime * frameRate)), 0, Math.Max(0, availableFrames - 1));
            int actualFrames = Mathf.Min(windowFrames, availableFrames);
            if (actualFrames <= 0)
                throw new InvalidOperationException("Outside source AnimationClip has no sampleable frames.");
            if (actualFrames < windowFrames)
                Debug.LogWarning(
                    $"[Kimodo][Outside] Source AnimationClip '{animation.name}' provides {actualFrames} " +
                    $"of {windowFrames} requested window frames; the available range will be used.", animation);
            int firstFrame = isIn ? Math.Max(0, boundaryFrame - actualFrames + 1) : boundaryFrame;
            if (!isIn)
                actualFrames = Math.Min(actualFrames, Math.Max(0, availableFrames - firstFrame));
            if (actualFrames <= 0)
                throw new InvalidOperationException("Outside source frame/window is outside its AnimationClip.");
            if (isIn) targetWindowStart += windowFrames - actualFrames;
            var times = new double[actualFrames];
            var targetFrames = new List<int>(actualFrames);
            for (int index = 0; index < actualFrames; index++)
            {
                times[index] = (firstFrame + index) * frameDuration;
                targetFrames.Add(targetWindowStart + index);
            }
            KimodoClipConstraint constraint = BuildConstraint(
                animation, sourceAvatar, modelName, times, targetFrames, frameRate,
                isIn ? "outside_in" : "outside_out", token, includeFootContacts: false);
            constraint.postApply = postApply;
            constraint.sampleCount = Mathf.Min(sampleCount, actualFrames);
            return constraint;
        }
    }
}
