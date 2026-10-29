using System;
using System.Collections.Generic;
using System.Linq;
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
        public Avatar TargetAvatar;
        public string ModelName = KimodoMotionModelProfiles.DefaultModelName;
        public TimelineClip PreviousTimelineClip;
        public TimelineClip NextTimelineClip;
        // Captured only when AutoBegin is needed; preview is suspended when
        // this context is the inspected Timeline so the scene root is stable.
        public Vector3 OriginalRootPosition;
        public Quaternion OriginalRootRotation = Quaternion.identity;
        public bool HasOriginalRootSnapshot;
        // Frozen before any Timeline evaluation so generation projection uses
        // the scene origin rather than the animated Animator pose.
        public Vector3 TrackOffsetPosition;
        public Quaternion TrackOffsetRotation = Quaternion.identity;
        public bool HasTrackOffsetSnapshot;
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
            IReadOnlyList<KimodoMarkerSampleResult> additionalManualSamples = null,
            bool deferEndConstraint = false,
            bool deferBeginConstraint = false,
            int outputStartFrame = 0,
            int outputFrameCount = 0)
        {
            result = null;
            error = string.Empty;

            if (!TryResolveTimelineContext(sourceClip, out KimodoTimelineInOutConstraintContext context, out error))
            {
                return false;
            }
            KimodoTimelineTrackOffsetUtility.CaptureWorldOffset(
                context.Track,
                context.Animator,
                out context.TrackOffsetPosition,
                out context.TrackOffsetRotation,
                out _);
            context.HasTrackOffsetSnapshot = true;

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
                sampleTimeOffsetSeconds,
                deferEndConstraint,
                deferBeginConstraint,
                outputStartFrame,
                outputFrameCount);
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
            out List<KimodoMarkerSampleResult> beginBoundaryPose,
            out List<KimodoMarkerSampleResult> endBoundaryPose,
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
            if (request != null && mode == KimodoInOutConstraintMode.Outside)
            {
                KimodoInOutConstraintTools.ResolveOutsideContextFrames(sourceClip, out int before, out int after);
                request.OutputStartFrame = before;
                request.OutputFrameCount = KimodoInOutConstraintTools.ClampFrameCount(generationFrames);
                request.GenerationFrames += before + after;
            }
            if (request == null)
            {
                return true;
            }

            if (!KimodoInOutConstraintTools.TrySampleBoundaries(
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

        internal static bool TryResolveSourceClip(
            TimelineClip targetClip,
            TrackAsset sourceTrack,
            PlayableAsset sourceAsset,
            double sourceClipStart,
            out TimelineClip resolvedClip,
            out string error)
        {
            resolvedClip = null;
            error = string.Empty;
            if (sourceTrack == null && sourceAsset == null)
            {
                return true;
            }
            if (sourceTrack == null || sourceAsset == null)
            {
                error = "In/Out source Clip reference is incomplete.";
                return false;
            }
            if (targetClip?.GetParentTrack()?.timelineAsset != sourceTrack.timelineAsset)
            {
                error = "In/Out source Clip must belong to the generation Timeline.";
                return false;
            }

            TimelineClip[] matches = sourceTrack.GetClips()
                .Where(item => item != null && ReferenceEquals(item.asset, sourceAsset) &&
                    (sourceClipStart < 0.0 || Math.Abs(item.start - sourceClipStart) <= 1e-6))
                .ToArray();
            if (matches.Length != 1)
            {
                error = matches.Length == 0
                    ? "The selected In/Out source Clip is no longer present on its Timeline track. Select it again."
                    : "The selected In/Out PlayableAsset appears more than once on its track and cannot be resolved uniquely.";
                return false;
            }
            if (!IsUsableSourceClip(matches[0]))
            {
                error = "The selected In/Out source Clip does not contain a usable animation.";
                return false;
            }
            resolvedClip = matches[0];
            return true;
        }

        internal static bool IsUsableSourceClip(TimelineClip timelineClip)
        {
            if (timelineClip?.asset is KimodoPlayableClip playable)
            {
                return playable.clip != null;
            }
            return TryResolveAnimationClip(timelineClip, out _, out _);
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

            if (!KimodoConstraintMarkerEditorUtility.TryGetTrackAnimatorBinding(
                    director,
                    track,
                    out Animator animator))
            {
                error = "Animation track and its parent tracks have no Animator binding.";
                return false;
            }

            TryResolveNeighborTimelineClips(sourceClip, out TimelineClip previousTimelineClip, out TimelineClip nextTimelineClip);

            context = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = sourceClip,
                Track = track,
                Director = director,
                Animator = animator,
                ModelName = KimodoMotionModelProfiles.NormalizeName((sourceClip.asset as KimodoPlayableClip)?.bridgeModelName),
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

            return KimodoDirectorResolver.TryResolve(timelineAsset, out director, out error);
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
            double sampleTimeOffsetSeconds = 0.0,
            bool deferEndConstraint = false,
            bool deferBeginConstraint = false,
            int outputStartFrame = 0,
            int outputFrameCount = 0)
        {
            if (context == null)
            {
                return null;
            }

            bool hasManualSamples = manualSamples != null && manualSamples.Count > 0;
            var playable = context.SourceClip?.asset as KimodoPlayableClip;
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(context.ModelName, out _);
            if (isArdy && (playable?.inConstraintSourceAsset != null || playable?.outConstraintSourceAsset != null))
            {
                throw new InvalidOperationException("Explicit In/Out source Clips currently require a standard motion model; ARDY uses Timeline history constraints.");
            }

            KimodoLocalAvatarUtility.AvatarResolveResult targetAvatarResult = context.Track != null && context.Animator != null
                ? KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(context.Track, context.Animator)
                : default;
            Avatar targetAvatar = targetAvatarResult.IsHumanoid ? targetAvatarResult.Avatar : context.TargetAvatar;
            KimodoTimelineInOutConstraintContext ResolveSource(
                bool isBegin,
                TrackAsset selectedTrack,
                PlayableAsset selectedAsset,
                double selectedClipStart)
            {
                TimelineClip source = null;
                if (selectedTrack != null || selectedAsset != null)
                {
                    if (!TryResolveSourceClip(context.SourceClip, selectedTrack, selectedAsset, selectedClipStart, out source, out string sourceError))
                    {
                        throw new InvalidOperationException(sourceError);
                    }
                }
                else if (mode == KimodoInOutConstraintMode.Inside)
                {
                    source = context.SourceClip;
                }
                else if (mode == KimodoInOutConstraintMode.Outside)
                {
                    source = isBegin ? context.PreviousTimelineClip : context.NextTimelineClip;
                }

                if (source == null)
                {
                    return null;
                }
                if (!TryResolveTimelineContext(source, out KimodoTimelineInOutConstraintContext sourceContext, out string sourceContextError))
                {
                    throw new InvalidOperationException(sourceContextError);
                }
                sourceContext.TargetAvatar = targetAvatar;
                return sourceContext;
            }

            KimodoTimelineInOutConstraintContext beginSource = ResolveSource(
                true,
                playable?.inConstraintSourceTrack,
                playable?.inConstraintSourceAsset,
                playable?.inConstraintSourceClipStart ?? -1.0);
            KimodoTimelineInOutConstraintContext endSource = ResolveSource(
                false,
                playable?.outConstraintSourceTrack,
                playable?.outConstraintSourceAsset,
                playable?.outConstraintSourceClipStart ?? -1.0);
            bool enableBegin = enableIn && beginSource != null;
            bool enableEnd = enableOut && endSource != null;
            if (!enableBegin && !enableEnd && !hasManualSamples && !autoBeginAnchor)
            {
                return null;
            }
            return new KimodoInOutConstraintRequest
            {
                Mode = mode,
                EnableBegin = enableBegin,
                EnableEnd = enableEnd,
                ModelName = context.ModelName,
                GenerationFrames = KimodoInOutConstraintTools.ClampFrameCount(generationFrames),
                OutputStartFrame = Mathf.Max(0, outputStartFrame),
                OutputFrameCount = Mathf.Max(0, outputFrameCount),
                BeginWindowModelFrameCount = isArdy ? 1 : new KimodoFrameWindow(
                    playable?.inConstraintWindowFrames60 ?? 1,
                    playable?.inConstraintSampleCount ?? 1).ResolveModelFrameCount(
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(context.ModelName)),
                BeginSampleCount = isArdy ? 1 : playable?.inConstraintSampleCount ?? 1,
                EndWindowModelFrameCount = isArdy ? 1 : new KimodoFrameWindow(
                    playable?.outConstraintWindowFrames60 ?? 1,
                    playable?.outConstraintSampleCount ?? 1).ResolveModelFrameCount(
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(context.ModelName)),
                EndSampleCount = isArdy ? 1 : playable?.outConstraintSampleCount ?? 1,
                AutoBeginAnchor = autoBeginAnchor,
                DeferNormalization = deferNormalization,
                DeferBeginConstraint = deferBeginConstraint,
                DeferEndConstraint = deferEndConstraint,
                TimelineContext = context,
                BeginTimelineContext = beginSource,
                EndTimelineContext = endSource,
                HasExplicitBeginSource = playable?.inConstraintSourceAsset != null,
                HasExplicitEndSource = playable?.outConstraintSourceAsset != null,
                BeginSourceFrame = playable?.inConstraintSourceFrame ?? -1,
                EndSourceFrame = playable?.outConstraintSourceFrame ?? -1,
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
