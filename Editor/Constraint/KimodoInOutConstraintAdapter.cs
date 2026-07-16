using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
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
            if (!TryBuildConstraints(
                    sourceClip,
                    mode,
                    normalizeConstraintOrigin,
                    generationFrames,
                    out KimodoInOutConstraintResult result,
                    out error))
            {
                return false;
            }

            constraintsJson = result != null ? result.ConstraintsJson ?? string.Empty : string.Empty;
            return true;
        }

        internal static bool TryBuildConstraints(
            TimelineClip sourceClip,
            KimodoInOutConstraintMode mode,
            bool normalizeConstraintOrigin,
            int generationFrames,
            out KimodoInOutConstraintResult result,
            out string error)
        {
            result = null;
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
                result = new KimodoInOutConstraintResult();
                return true;
            }

            if (!KimodoInOutConstraintComposer.TryBuild(
                    request,
                    out result,
                    out string warning,
                    out error))
            {
                return false;
            }

            EmitNeighborWarningsIfNeeded(context, mode, warning);
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
                    out _,
                    out string buildWarning,
                    out string buildError))
            {
                warning = buildError;
                return false;
            }

            if (!KimodoInOutConstraintTools.TrySampleBoundaryPair(
                    request,
                    out beginBoundaryPose,
                    out endBoundaryPose,
                    out string sampleWarning,
                    out string sampleError))
            {
                warning = sampleError;
                return false;
            }

            warning = mode == KimodoInOutConstraintMode.Outside
                ? FirstNonEmpty(context.PreviousClipWarning, context.NextClipWarning, buildWarning, sampleWarning)
                : FirstNonEmpty(buildWarning, sampleWarning);
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

            if (!TryResolveDirector(sourceClip, track, out PlayableDirector director, out error))
            {
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

        internal static bool TryResolveDirector(
            TimelineClip sourceClip,
            TrackAsset track,
            out PlayableDirector director,
            out string error)
        {
            director = null;
            error = string.Empty;

            PlayableDirector inspectedDirector = TimelineEditor.inspectedDirector;
            if (inspectedDirector != null)
            {
                director = inspectedDirector;
                return true;
            }

            TimelineAsset timelineAsset = ResolveTimelineAsset(sourceClip, track);
            if (timelineAsset == null)
            {
                error = "Timeline inspected director is null and TimelineAsset cannot be resolved.";
                return false;
            }

            var candidates = new List<PlayableDirector>();
            PlayableDirector[] directors = Resources.FindObjectsOfTypeAll<PlayableDirector>();
            for (int i = 0; i < directors.Length; i++)
            {
                PlayableDirector candidate = directors[i];
                if (candidate == null ||
                    candidate.playableAsset != timelineAsset ||
                    EditorUtility.IsPersistent(candidate) ||
                    candidate.gameObject == null ||
                    !candidate.gameObject.scene.IsValid())
                {
                    continue;
                }

                candidates.Add(candidate);
            }

            if (candidates.Count == 0)
            {
                error = "Timeline inspected director is null and no scene PlayableDirector references this TimelineAsset.";
                return false;
            }

            PlayableDirector selectedDirector = TryResolveSelectedDirector(candidates);
            if (selectedDirector != null)
            {
                director = selectedDirector;
                return true;
            }

            if (candidates.Count == 1)
            {
                director = candidates[0];
                return true;
            }

            error = "Timeline inspected director is null and multiple scene PlayableDirectors reference this TimelineAsset. Select/open the correct PlayableDirector in Timeline.";
            return false;
        }

        private static TimelineAsset ResolveTimelineAsset(TimelineClip sourceClip, TrackAsset track)
        {
            TrackAsset resolvedTrack = track != null ? track : sourceClip?.GetParentTrack();
            if (resolvedTrack != null && resolvedTrack.timelineAsset != null)
            {
                return resolvedTrack.timelineAsset;
            }

            return TimelineEditor.inspectedAsset;
        }

        private static PlayableDirector TryResolveSelectedDirector(List<PlayableDirector> candidates)
        {
            GameObject selectedGameObject = Selection.activeGameObject;
            if (selectedGameObject == null)
            {
                return null;
            }

            PlayableDirector selectedDirector = selectedGameObject.GetComponent<PlayableDirector>();
            if (selectedDirector != null && candidates.Contains(selectedDirector))
            {
                return selectedDirector;
            }

            selectedDirector = selectedGameObject.GetComponentInParent<PlayableDirector>();
            return selectedDirector != null && candidates.Contains(selectedDirector)
                ? selectedDirector
                : null;
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

                if (clip.start < sourceClip.start &&
                    (previousClip == null || clip.start > previousClip.start))
                {
                    previousClip = clip;
                }

                if (clip.start > sourceClip.start &&
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
            if (context == null)
            {
                return null;
            }

            bool hasManualSamples = manualSamples != null && manualSamples.Count > 0;
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
                    enableBegin = context.PreviousTimelineClip != null;
                    enableEnd = context.NextTimelineClip != null;
                    break;
            }

            if (KimodoMotionModelProfiles.TryGetArdy(context.ModelName, out _))
            {
                enableBegin = false;
            }

            if (!enableBegin && !enableEnd && !hasManualSamples)
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
                // Normalize against the first available first-frame constraint anchor.
                NormalizeConstraintOrigin = normalizeConstraintOrigin,
                IsLoop = false,
                TimelineContext = context,
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
