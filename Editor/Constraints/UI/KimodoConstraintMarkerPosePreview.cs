using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintMarkerPosePreview
    {
        private static int dragMuscleSnapshotId;

public static bool TryBuildRenderContextForMarker(KimodoConstraintMarker marker, out PoseCacheRenderContext context, out string error)
        {
            context = default;
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryGetMarkerTrack(marker, out TrackAsset track))
            {
                error = "parent track not found";
                return false;
            }

            KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip clipRange);

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "animation track has no animator binding";
                return false;
            }

            TimelineClip referenceClip = KimodoConstraintMarkerSampling.FindReferenceClip(track, marker.time, clipRange);
            KimodoPlayableClip playableClip = referenceClip?.asset as KimodoPlayableClip;
            string modelName = KimodoConstraintMarkerSampling.ResolveModelName(referenceClip);
            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, animator);
            if (!avatarResult.IsHumanoid || avatarResult.Avatar == null)
            {
                error = $"Resolve source avatar failed: {avatarResult.Error}";
                return false;
            }
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            int clipContextId = playableClip != null
                ? KimodoUnityObjectIdUtility.IdHash(playableClip)
                : ((referenceClip?.asset as UnityEngine.Object) != null
                    ? KimodoUnityObjectIdUtility.IdHash(referenceClip.asset as UnityEngine.Object)
                    : KimodoUnityObjectIdUtility.IdHash(track));
            context = new PoseCacheRenderContext(
                clipContextId,
                KimodoUnityObjectIdUtility.IdHash(animator),
                KimodoUnityObjectIdUtility.IdHash(track),
                modelName,
                rigType,
                avatarResult.Avatar);
            return true;
        }

internal static void LogDragMuscleSnapshot(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext renderContext,
            string entryId)
        {
            try
            {
                if (marker == null)
                {
                    return;
                }

                if (!TryCaptureDragMuscleSnapshot(
                        marker,
                        renderContext,
                        entryId,
                        out MuscleSample timelinePose,
                        out float timelinePoseScale,
                        out MuscleSample virtualSkeleton,
                        out float virtualSkeletonScale,
                        out MuscleSample targetCharacter,
                        out float targetCharacterScale,
                        out double timelineSampleTime,
                        out string details,
                        out string error))
                {
                    Debug.LogWarning($"[Kimodo][ConstraintDragMuscles] capture failed: {error}");
                    return;
                }

                dragMuscleSnapshotId++;
                KimodoConstraintPoseDiagnostics.LogDragMuscleSnapshot(
                    dragMuscleSnapshotId,
                    marker.ConstraintType,
                    timelineSampleTime,
                    timelinePose,
                    timelinePoseScale,
                    virtualSkeleton,
                    virtualSkeletonScale,
                    targetCharacter,
                    targetCharacterScale,
                    details);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][ConstraintDragMuscles] capture failed: {ex.Message}");
            }
        }

private static bool TryCaptureDragMuscleSnapshot(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext renderContext,
            string entryId,
            out MuscleSample timelinePose,
            out float timelinePoseScale,
            out MuscleSample virtualSkeleton,
            out float virtualSkeletonScale,
            out MuscleSample targetCharacter,
            out float targetCharacterScale,
            out double timelineSampleTime,
            out string details,
            out string error)
        {
            timelinePose = null;
            timelinePoseScale = 0f;
            virtualSkeleton = null;
            virtualSkeletonScale = 0f;
            targetCharacter = null;
            targetCharacterScale = 0f;
            timelineSampleTime = marker != null ? marker.time : 0d;
            details = string.Empty;
            error = string.Empty;

            if (!KimodoConstraintMarkerEditorUtility.TryGetMarkerTrack(marker, out TrackAsset track))
            {
                error = "marker track is unavailable.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            Animator animator = director != null ? director.GetGenericBinding(track) as Animator : null;
            if (director == null || animator == null || !KimodoRetargetCoreUtility.IsValidHumanoid(renderContext.SourceAvatar))
            {
                error = "Timeline director, binding Animator, or source Avatar is unavailable.";
                return false;
            }

            var timelineContext = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = null,
                Track = track,
                Director = director,
                Animator = animator,
                SourceAvatar = renderContext.SourceAvatar,
                ModelName = renderContext.ModelName
            };
            float timelineFrameRate = KimodoTimelineConstraintClipCache.ResolveTimelineFrameRate(timelineContext);
            timelineSampleTime = KimodoTimelineConstraintClipCache.ResolveTimelineSampleTime(marker.time, timelineFrameRate);

            if (!KimodoTimelineSamplingSession.TryCreate(
                    timelineContext,
                    renderContext.ModelName,
                    out KimodoTimelineSamplingSession sampler,
                    out error))
            {
                return false;
            }
            try
            {
                if (!sampler.TryCaptureMuscleSample(
                        timelineSampleTime,
                        normalizeRootToAnchor: false,
                        Vector3.zero,
                        Quaternion.identity,
                        out timelinePose,
                        out error))
                {
                    return false;
                }
                timelinePoseScale = sampler.SourceHumanScale;
            }
            finally
            {
                sampler.Dispose();
            }

            if (!KimodoConstraintPoseCache.TryCaptureDragMuscleSamples(
                    renderContext,
                    entryId,
                    out virtualSkeleton,
                    out virtualSkeletonScale,
                    out targetCharacter,
                    out targetCharacterScale,
                    out error))
            {
                return false;
            }

            details = $"timelinePoseClip=true animator='{animator.name}' entry='{entryId ?? string.Empty}'";
            return true;
        }

public static bool TryBuildRenderContextForPlayableClip(
            KimodoPlayableClip playableClip,
            out PoseCacheRenderContext context,
            out TimelineClip timelineClip,
            out string error,
            TimelineClip timelineClipOverride = null)
        {
            context = default;
            timelineClip = null;
            error = string.Empty;
            if (playableClip == null)
            {
                error = "playable clip is null";
                return false;
            }

            timelineClip = timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(playableClip);
            if (timelineClip == null)
            {
                error = "timeline clip not found for playable clip";
                return false;
            }

            TrackAsset track = timelineClip.GetParentTrack();
            if (track == null)
            {
                error = "parent track not found";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "animation track has no animator binding";
                return false;
            }

            string modelName = string.IsNullOrWhiteSpace(playableClip.bridgeModelName)
                ? "Kimodo-SOMA-RP-v1"
                : playableClip.bridgeModelName.Trim();
            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, animator);
            if (!avatarResult.IsHumanoid || avatarResult.Avatar == null)
            {
                error = $"Resolve source avatar failed: {avatarResult.Error}";
                return false;
            }
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            context = new PoseCacheRenderContext(
                KimodoUnityObjectIdUtility.IdHash(playableClip),
                KimodoUnityObjectIdUtility.IdHash(animator),
                KimodoUnityObjectIdUtility.IdHash(track),
                modelName,
                rigType,
                avatarResult.Avatar);
            return true;
        }

public static bool TryRenderMarkerToPoseCache(KimodoConstraintMarker marker, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!marker.constraintEnabled)
            {
                KimodoConstraintMarkerEditorUtility.ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
                return true;
            }

            if (!TryBuildRenderContextForMarker(marker, out PoseCacheRenderContext context, out error))
            {
                return false;
            }

            return TryRenderMarkerToPoseCache(marker, context, out _, out error);
        }

        internal static bool TryRenderMarkerToPoseCache(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            out string error)
        {
            return TryRenderMarkerToPoseCache(marker, context, out _, out error);
        }

        private static bool TryRenderMarkerToPoseCache(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            string entryId = KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker);

            if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(marker, marker.SampleData, out KimodoMarkerSampleResult normalizedSample, out error))
            {
                return false;
            }

            sample = normalizedSample;

            var item = new PoseCacheRenderItem
            {
                EntryId = entryId,
                SampleData = normalizedSample,
                ConstraintType = marker.ConstraintType,
                HighlightJoints = KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                Visible = true,
                SourceMarker = marker
            };
            var batch = new List<PoseCacheRenderItem>(1) { item };
            if (!KimodoConstraintPoseCache.RenderBatch(context, batch, out error))
            {
                return false;
            }
            return true;
        }

        public static bool TryRenderMarkersBatchToPoseCache(
            PoseCacheRenderContext context,
            IReadOnlyList<KimodoConstraintMarker> markers,
            out string error)
        {
            error = string.Empty;
            if (markers == null || markers.Count == 0)
            {
                KimodoConstraintPoseCache.SetGroupState(context, visible: false, selectable: false);
                return true;
            }

            var items = new List<PoseCacheRenderItem>(markers.Count);
            for (int i = 0; i < markers.Count; i++)
            {
                KimodoConstraintMarker marker = markers[i];
                if (marker == null || !marker.constraintEnabled)
                {
                    continue;
                }

                if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(marker, marker.SampleData, out KimodoMarkerSampleResult sample, out string normalizeError))
                {
                    error = normalizeError;
                    return false;
                }

                items.Add(new PoseCacheRenderItem
                {
                    EntryId = KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker),
                    SampleData = sample,
                    ConstraintType = marker.ConstraintType,
                    HighlightJoints = KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                    Visible = true,
                    SourceMarker = marker
                });
            }

            return KimodoConstraintPoseCache.RenderBatch(context, items, out error);
        }
    }
}
