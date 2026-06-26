using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoTimelineInOutConstraintContext
    {
        public TimelineClip SourceClip;
        public TrackAsset Track;
        public PlayableDirector Director;
        public Animator Animator;
        public Avatar SourceAvatar;
        public string ModelName = KimodoPlayableClip.DefaultBridgeModelName;
        public AnimationClip CurrentClip;
        public TimelineClip PreviousTimelineClip;
        public TimelineClip NextTimelineClip;
        public AnimationClip PreviousClip;
        public AnimationClip NextClip;
        public string PreviousClipWarning = string.Empty;
        public string NextClipWarning = string.Empty;
    }

    internal static class KimodoInOutConstraintAdapter
    {
        internal static bool TryBuildConstraintsJson(
            TimelineClip sourceClip,
            KimodoInOutConstraintMode mode,
            bool normalizeConstraintOrigin,
            int generationFrames,
            out string constraintsJson,
            out string error)
        {
            constraintsJson = string.Empty;
            error = string.Empty;

            if (!TryResolveTimelineContext(sourceClip, out KimodoTimelineInOutConstraintContext context, out error))
            {
                return false;
            }

            if (!KimodoTimelineConstraintMarkerSampler.TryBuildMarkerSamplesForExport(
                    context,
                    out List<KimodoMarkerSampleResult> manualSamples,
                    out error))
            {
                return false;
            }

            KimodoInOutConstraintRequest request = BuildTimelineRequest(
                context,
                mode,
                normalizeConstraintOrigin,
                generationFrames,
                manualSamples);
            if (request == null)
            {
                constraintsJson = string.Empty;
                return true;
            }

            if (!KimodoInOutConstraintComposer.TryBuild(
                    request,
                    out KimodoInOutConstraintResult result,
                    out string warning,
                    out error))
            {
                return false;
            }

            EmitNeighborWarningsIfNeeded(context, mode, warning);
            constraintsJson = result != null ? result.ConstraintsJson ?? string.Empty : string.Empty;
            return true;
        }

        internal static bool TryBuildBoundarySamplesForPreview(
            TimelineClip sourceClip,
            KimodoInOutConstraintMode mode,
            int generationFrames,
            out KimodoMarkerSampleResult beginBoundaryPose,
            out KimodoMarkerSampleResult endBoundaryPose,
            out string warning)
        {
            beginBoundaryPose = null;
            endBoundaryPose = null;
            warning = string.Empty;

            if (mode == KimodoInOutConstraintMode.None)
            {
                return true;
            }

            if (!TryResolveTimelineContext(sourceClip, out KimodoTimelineInOutConstraintContext context, out string error))
            {
                warning = error;
                return false;
            }

            KimodoInOutConstraintRequest request = BuildTimelineRequest(
                context,
                mode,
                normalizeConstraintOrigin: false,
                generationFrames,
                manualSamples: null);
            if (request == null)
            {
                warning = FirstNonEmpty(context.PreviousClipWarning, context.NextClipWarning);
                return true;
            }

            if (!KimodoInOutConstraintComposer.TryBuild(
                    request,
                    out KimodoInOutConstraintResult result,
                    out string buildWarning,
                    out string buildError))
            {
                warning = buildError;
                return false;
            }

            beginBoundaryPose = result?.BeginSample;
            endBoundaryPose = result?.EndSample;
            warning = mode == KimodoInOutConstraintMode.Outside
                ? FirstNonEmpty(context.PreviousClipWarning, context.NextClipWarning, buildWarning)
                : buildWarning;
            return true;
        }

        internal static bool HasPreviousNeighbor(TimelineClip clip)
        {
            TryResolveNeighborTimelineClips(clip, out TimelineClip previousClip, out _);
            return previousClip != null;
        }

        internal static int ClampFrameCount(int generationFrames)
        {
            return KimodoInOutConstraintTools.ClampFrameCount(generationFrames);
        }

        internal static int DurationSecondsToFrameCount(float durationSeconds)
        {
            return KimodoInOutConstraintTools.DurationSecondsToFrameCount(durationSeconds);
        }

        internal static float FrameCountToDurationSeconds(int frameCount)
        {
            return KimodoInOutConstraintTools.FrameCountToDurationSeconds(frameCount);
        }

        internal static double ResolveConstraintClipDurationSeconds(int frameCount)
        {
            return KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(frameCount);
        }

        internal static double ResolveConstraintEndSampleTimeSeconds(int frameCount)
        {
            return KimodoInOutConstraintTools.ResolveConstraintEndSampleTimeSeconds(frameCount);
        }

        internal static bool TryResolveTimelineContext(
            TimelineClip sourceClip,
            out KimodoTimelineInOutConstraintContext context,
            out string error)
        {
            context = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "No selected timeline clip for constraint export.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(animator.gameObject);
            Avatar sourceAvatar = avatarResult.Avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = $"Resolve source avatar failed: {avatarResult.Error}";
                return false;
            }

            TryResolveNeighborTimelineClips(sourceClip, out TimelineClip previousTimelineClip, out TimelineClip nextTimelineClip);
            TryResolveAnimationClip(previousTimelineClip, out AnimationClip previousClip, out string previousWarning);
            TryResolveAnimationClip(sourceClip, out AnimationClip currentClip, out _);
            TryResolveAnimationClip(nextTimelineClip, out AnimationClip nextClip, out string nextWarning);

            context = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = sourceClip,
                Track = track,
                Director = director,
                Animator = animator,
                SourceAvatar = sourceAvatar,
                ModelName = KimodoPlayableClip.NormalizeBridgeModelName(((KimodoPlayableClip)sourceClip.asset)?.bridgeModelName),
                CurrentClip = currentClip,
                PreviousTimelineClip = previousTimelineClip,
                NextTimelineClip = nextTimelineClip,
                PreviousClip = previousClip,
                NextClip = nextClip,
                PreviousClipWarning = previousWarning,
                NextClipWarning = nextWarning
            };
            return true;
        }

        internal static void TryResolveNeighborTimelineClips(
            TimelineClip sourceClip,
            out TimelineClip previousClip,
            out TimelineClip nextClip)
        {
            previousClip = null;
            nextClip = null;

            TrackAsset track = sourceClip != null ? sourceClip.GetParentTrack() : null;
            if (track == null)
            {
                return;
            }

            foreach (TimelineClip clip in track.GetClips())
            {
                if (clip == null || clip == sourceClip)
                {
                    continue;
                }

                if (clip.end <= sourceClip.start &&
                    (previousClip == null || clip.end > previousClip.end))
                {
                    previousClip = clip;
                }

                if (clip.start >= sourceClip.end &&
                    (nextClip == null || clip.start < nextClip.start))
                {
                    nextClip = clip;
                }
            }
        }

        internal static bool TryResolveAnimationClip(TimelineClip timelineClip, out AnimationClip clip, out string warning)
        {
            clip = null;
            warning = string.Empty;

            if (timelineClip == null)
            {
                return false;
            }

            if (!KimodoMarkerSamplingUtility.TryResolveAnimationClipFromTimelineClip(timelineClip, out clip, out string error))
            {
                warning = string.IsNullOrWhiteSpace(error)
                    ? "Timeline clip does not contain a usable AnimationClip."
                    : error;
                return false;
            }

            return clip != null;
        }

        internal static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i];
                }
            }

            return string.Empty;
        }

        private static KimodoInOutConstraintRequest BuildTimelineRequest(
            KimodoTimelineInOutConstraintContext context,
            KimodoInOutConstraintMode mode,
            bool normalizeConstraintOrigin,
            int generationFrames,
            List<KimodoMarkerSampleResult> manualSamples)
        {
            if (context == null || mode == KimodoInOutConstraintMode.None)
            {
                return null;
            }

            KimodoInOutConstraintClipSegment beginSegment = null;
            KimodoInOutConstraintClipSegment endSegment = null;
            bool enableBegin = false;
            bool enableEnd = false;

            switch (mode)
            {
                case KimodoInOutConstraintMode.Inside:
                    beginSegment = BuildSegment(context.CurrentClip, context.SourceClip);
                    endSegment = BuildSegment(context.CurrentClip, context.SourceClip);
                    enableBegin = beginSegment != null;
                    enableEnd = endSegment != null;
                    break;

                case KimodoInOutConstraintMode.Outside:
                    beginSegment = BuildSegment(context.PreviousClip, context.PreviousTimelineClip);
                    endSegment = BuildSegment(context.NextClip, context.NextTimelineClip);
                    enableBegin = beginSegment != null;
                    enableEnd = endSegment != null;
                    break;
            }

            if (!enableBegin && !enableEnd)
            {
                return null;
            }

            return new KimodoInOutConstraintRequest
            {
                Mode = mode,
                BeginSegment = beginSegment,
                EndSegment = endSegment,
                EnableBegin = enableBegin,
                EnableEnd = enableEnd,
                SourceAvatar = context.SourceAvatar,
                ModelName = context.ModelName,
                GenerationFrames = ClampFrameCount(generationFrames),
                NormalizeConstraintOrigin = normalizeConstraintOrigin && enableBegin,
                IsLoop = false,
                ManualSamples = KimodoInOutConstraintTools.BuildLocalManualSamples(
                    manualSamples,
                    context.SourceClip != null ? context.SourceClip.start : 0.0)
            };
        }

        private static KimodoInOutConstraintClipSegment BuildSegment(AnimationClip clip, TimelineClip timelineClip)
        {
            if (clip == null)
            {
                return null;
            }

            if (timelineClip == null)
            {
                return new KimodoInOutConstraintClipSegment
                {
                    Clip = clip,
                    StartSeconds = 0.0,
                    DurationSeconds = clip.length,
                    Speed = 1f
                };
            }

            return new KimodoInOutConstraintClipSegment
            {
                Clip = clip,
                StartSeconds = Math.Max(0.0, timelineClip.clipIn),
                DurationSeconds = Math.Max(0.0, timelineClip.duration),
                Speed = (float)Math.Max(1e-6, timelineClip.timeScale)
            };
        }

        private static void EmitNeighborWarningsIfNeeded(
            KimodoTimelineInOutConstraintContext context,
            KimodoInOutConstraintMode mode,
            string buildWarning)
        {
            if (mode != KimodoInOutConstraintMode.Outside)
            {
                if (!string.IsNullOrWhiteSpace(buildWarning))
                {
                    Debug.LogWarning($"[Kimodo][InOutConstraint] {buildWarning}");
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(context?.PreviousClipWarning))
            {
                Debug.LogWarning($"[Kimodo][InOutConstraint] {context.PreviousClipWarning}");
            }

            if (!string.IsNullOrWhiteSpace(context?.NextClipWarning))
            {
                Debug.LogWarning($"[Kimodo][InOutConstraint] {context.NextClipWarning}");
            }

            if (!string.IsNullOrWhiteSpace(buildWarning))
            {
                Debug.LogWarning($"[Kimodo][InOutConstraint] {buildWarning}");
            }
        }
    }
}
