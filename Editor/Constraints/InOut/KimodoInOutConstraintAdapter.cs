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
        public TimelineClip PreviousTimelineClip;
        public TimelineClip NextTimelineClip;
    }

    internal static class KimodoInOutConstraintAdapter
    {
        internal static bool TryBuildConstraints(
            TimelineClip sourceClip,
            KimodoInOutConstraintMode mode,
            bool autoBeginAnchor,
            bool deferNormalization,
            bool enableIn,
            bool enableOut,
            int generationFrames,
            double sampleTimeOffsetSeconds,
            out KimodoInOutConstraintResult result,
            out string error,
            IReadOnlyList<KimodoMarkerSampleResult> additionalManualSamples = null)
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
            KimodoInOutConstraintComposer.AppendSamples(additionalManualSamples, manualSamples);

            KimodoInOutConstraintRequest request = BuildTimelineRequest(
                context,
                mode,
                autoBeginAnchor,
                deferNormalization,
                enableIn,
                enableOut,
                generationFrames,
                manualSamples,
                sampleTimeOffsetSeconds);
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

            EmitNeighborWarningsIfNeeded(context, mode, enableIn, enableOut, warning);
            return true;
        }

        internal static bool TryBuildBoundarySamplesForPreview(
            TimelineClip sourceClip,
            KimodoInOutConstraintMode mode,
            bool enableIn,
            bool enableOut,
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
                autoBeginAnchor: false,
                deferNormalization: true,
                enableIn,
                enableOut,
                generationFrames,
                manualSamples: null);
            if (request == null)
            {
                return true;
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

            warning = sampleWarning;
            return true;
        }

        internal static bool HasPreviousNeighbor(TimelineClip clip)
        {
            TryResolveNeighborTimelineClips(clip, out TimelineClip previousClip, out _);
            return previousClip != null;
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

            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, animator);
            Avatar sourceAvatar = avatarResult.Avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = $"Resolve source avatar failed: {avatarResult.Error}";
                return false;
            }

            TryResolveNeighborTimelineClips(sourceClip, out TimelineClip previousTimelineClip, out TimelineClip nextTimelineClip);

            context = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = sourceClip,
                Track = track,
                Director = director,
                Animator = animator,
                SourceAvatar = sourceAvatar,
                ModelName = KimodoPlayableClip.NormalizeBridgeModelName(((KimodoPlayableClip)sourceClip.asset)?.bridgeModelName),
                PreviousTimelineClip = previousTimelineClip,
                NextTimelineClip = nextTimelineClip
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

            TimelineAsset timelineAsset = ResolveTimelineAsset(sourceClip, track);
            if (timelineAsset == null)
            {
                error = "Timeline inspected director is null and TimelineAsset cannot be resolved.";
                return false;
            }

            PlayableDirector inspectedDirector = TimelineEditor.inspectedDirector;
            if (inspectedDirector != null && inspectedDirector.playableAsset == timelineAsset)
            {
                director = inspectedDirector;
                return true;
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

        internal static KimodoInOutConstraintRequest BuildTimelineRequest(
            KimodoTimelineInOutConstraintContext context,
            KimodoInOutConstraintMode mode,
            bool autoBeginAnchor,
            bool deferNormalization,
            bool enableIn,
            bool enableOut,
            int generationFrames,
            List<KimodoMarkerSampleResult> manualSamples,
            double sampleTimeOffsetSeconds = 0.0)
        {
            if (context == null)
            {
                return null;
            }

            bool hasManualSamples = manualSamples != null && manualSamples.Count > 0;
            bool enableBegin = false;
            bool enableEnd = false;

            switch (mode)
            {
                case KimodoInOutConstraintMode.Inside:
                    enableBegin = enableIn && context.SourceClip != null;
                    enableEnd = enableOut && context.SourceClip != null;
                    break;

                case KimodoInOutConstraintMode.Outside:
                    enableBegin = enableIn && context.PreviousTimelineClip != null;
                    enableEnd = enableOut && context.NextTimelineClip != null;
                    break;
            }

            if (!enableBegin && !enableEnd && !hasManualSamples && !autoBeginAnchor)
            {
                return null;
            }

            float sourceHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(context.SourceAvatar);
            float kimodoHumanScale = 1f;
            if (KimodoRetargetMarkerSamplingUtility.TryResolveTargetAvatar(
                    null,
                    context.Animator,
                    context.ModelName,
                    out Avatar targetAvatar,
                    out _))
            {
                kimodoHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(targetAvatar);
            }

            return new KimodoInOutConstraintRequest
            {
                Mode = mode,
                EnableBegin = enableBegin,
                EnableEnd = enableEnd,
                SourceAvatar = context.SourceAvatar,
                ModelName = context.ModelName,
                SourceHumanScale = sourceHumanScale,
                KimodoHumanScale = kimodoHumanScale,
                GenerationFrames = KimodoInOutConstraintTools.ClampFrameCount(generationFrames),
                AutoBeginAnchor = autoBeginAnchor,
                DeferNormalization = deferNormalization,
                IsLoop = false,
                TimelineContext = context,
                ManualSamples = KimodoInOutConstraintTools.BuildLocalManualSamples(
                    manualSamples,
                    context.SourceClip != null ? context.SourceClip.start : 0.0,
                    sampleTimeOffsetSeconds)
            };
        }

        private static void EmitNeighborWarningsIfNeeded(
            KimodoTimelineInOutConstraintContext context,
            KimodoInOutConstraintMode mode,
            bool enableIn,
            bool enableOut,
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

            if (enableIn && context?.PreviousTimelineClip == null)
            {
                Debug.LogWarning("[Kimodo][InOutConstraint] Previous Timeline clip is missing.");
            }

            if (enableOut && context?.NextTimelineClip == null)
            {
                Debug.LogWarning("[Kimodo][InOutConstraint] Next Timeline clip is missing.");
            }

            if (!string.IsNullOrWhiteSpace(buildWarning))
            {
                Debug.LogWarning($"[Kimodo][InOutConstraint] {buildWarning}");
            }
        }
    }
}
