using System;
using System.Collections.Generic;
using System.Threading;
using TimelineInject;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineClipConstraintBuilder
    {
        internal static List<KimodoClipConstraint> Build(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip,
            string modelName,
            int runtimeFrameCount,
            float frameRate,
            int runtimeTrimStartFrame,
            bool includeTimelineInConstraint,
            CancellationToken token)
        {
            var result = new List<KimodoClipConstraint>();
            if (playableClip == null) return result;
            if (includeTimelineInConstraint &&
                playableClip.inOutConstraintMode != KimodoInOutConstraintMode.None &&
                playableClip.enableInConstraint &&
                HasBeginBoundary(playableClip, timelineClip))
            {
                result.Add(BuildBegin(
                    playableClip,
                    timelineClip,
                    modelName,
                    frameRate,
                    runtimeTrimStartFrame / frameRate,
                    token));
            }
            if (!playableClip.enableClipConstraint) return result;
            if (playableClip.clipConstraintAvatarMask == null)
            {
                throw new InvalidOperationException("Clip Constraint is enabled but Avatar Mask is not assigned.");
            }
            result.Add(new KimodoClipConstraint
            {
                motionBytes = KimodoClipConstraintEncoder.EncodeTimeline(
                    timelineClip,
                    modelName,
                    runtimeFrameCount,
                    frameRate,
                    runtimeTrimStartFrame,
                    playableClip.inOutConstraintMode,
                    playableClip.enableInConstraint,
                    playableClip.enableOutConstraint,
                    token),
                startTime = 0f,
                duration = runtimeFrameCount / frameRate,
                mask = KimodoClipConstraintMask.FromAvatarMask(modelName, playableClip.clipConstraintAvatarMask)
            });
            return result;
        }

        internal static KimodoClipConstraint BuildBegin(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip,
            string modelName,
            float frameRate,
            float startTime,
            CancellationToken token)
        {
            if (!HasBeginBoundary(playableClip, timelineClip))
            {
                throw new InvalidOperationException("ClipConstraint begin boundary is unavailable on this Timeline clip.");
            }
            return new KimodoClipConstraint
            {
                motionBytes = KimodoClipConstraintEncoder.EncodeTimeline(
                    timelineClip,
                    modelName,
                    1,
                    frameRate,
                    0,
                    playableClip.inOutConstraintMode,
                    true,
                    false,
                    token),
                startTime = startTime,
                duration = 1f / frameRate,
                mask = KimodoClipConstraintMask.FullBody(modelName, includeRoot: true)
            };
        }

        private static bool HasBeginBoundary(KimodoPlayableClip playableClip, TimelineClip timelineClip)
        {
            if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out _))
            {
                return false;
            }
            return playableClip.inOutConstraintMode == KimodoInOutConstraintMode.Inside
                ? context.SourceClip != null
                : playableClip.inOutConstraintMode == KimodoInOutConstraintMode.Outside &&
                  context.PreviousTimelineClip != null;
        }
    }
}
