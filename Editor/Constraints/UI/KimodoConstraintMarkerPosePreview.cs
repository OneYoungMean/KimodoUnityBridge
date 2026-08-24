using KimodoUnityBridge;
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

        internal static bool TryRenderMarkerPreview(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            out string error)
        {
            return TryRenderMarkerPreview(marker, context, out _, out error);
        }

        internal static bool TryBuildMarkerPreviewRequest(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            string entryId,
            Color previewColor,
            bool handlesEnabled,
            out ConstraintPreviewRequest item,
            out string error)
        {
            item = null;
            if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(
                    marker,
                    marker.SampleData,
                    out KimodoMarkerSampleResult normalizedSample,
                    out error))
            {
                return false;
            }

            item = new ConstraintPreviewRequest
            {
                EntryId = entryId,
                SampleData = normalizedSample,
                ConstraintType = marker.ConstraintType,
                ConstraintMode = marker.ConstraintMode,
                AutoSample = marker.autoSample,
                HandlesEnabled = handlesEnabled,
                HighlightJoints = KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                PreviewColor = previewColor,
                Visible = true,
                OnSampleChanged = changedSample =>
                {
                    if (changedSample == null) return;
                    bool switchedFromAutoSample = marker.autoSample;
                    Undo.RecordObject(marker, "Edit Kimodo Constraint Handle");
                    if (switchedFromAutoSample)
                    {
                        marker.autoSample = false;
                    }
                    if (KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker,
                        changedSample,
                        out _))
                    {
                        // Selection previews are intentionally suppressed while
                        // the edit window is open. Re-render that registered
                        // entry immediately so handle edits feed FK -> root
                        // override -> IK on the same drag event.
                        if (!KimodoConstraintSelectionPreviewTool.TryRenderEditPreview(
                                marker,
                                context,
                                out _))
                        {
                            KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
                        }
                        SceneView.RepaintAll();
                    }
                },
            };
            return true;
        }

        private static bool TryRenderMarkerPreview(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (!TryBuildMarkerPreviewRequest(
                    marker,
                    context,
                    KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker),
                    Color.white,
                    true,
                    out ConstraintPreviewRequest item,
                    out error))
            {
                return false;
            }
            sample = item.SampleData;
            if (!KimodoConstraintPoseCache.RenderConstraintPreview(context, item, out error))
            {
                return false;
            }
            return true;
        }

    }
}
