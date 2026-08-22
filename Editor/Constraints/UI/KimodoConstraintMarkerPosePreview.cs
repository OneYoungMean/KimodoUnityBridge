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
                rigType);
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
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            context = new PoseCacheRenderContext(
                KimodoUnityObjectIdUtility.IdHash(playableClip),
                KimodoUnityObjectIdUtility.IdHash(animator),
                KimodoUnityObjectIdUtility.IdHash(track),
                modelName,
                rigType);
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
                ConstraintMode = marker.ConstraintMode,
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

    }
}
