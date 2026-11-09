using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoConstraintSelectionPreviewTool
    {
        private const string EntryPrefix = "selection:";
        private static readonly Dictionary<string, ConstraintPreviewContext> RenderedContexts =
            new Dictionary<string, ConstraintPreviewContext>();
        private static readonly Dictionary<string, EditPreviewRegistration> EditPreviews =
            new Dictionary<string, EditPreviewRegistration>(StringComparer.Ordinal);
        private static bool refreshQueued;
        private static readonly Color SelectionPreviewColor = new Color(0.48f, 0.76f, 1f);

        static KimodoConstraintSelectionPreviewTool()
        {
            Selection.selectionChanged += SchedulePreviewUpdate;
            Undo.undoRedoPerformed += SchedulePreviewUpdate;
            EditorApplication.quitting += Clear;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            SchedulePreviewUpdate();
        }

        internal static void SchedulePreviewUpdate()
        {
            if (refreshQueued) return;
            refreshQueued = true;
            EditorApplication.delayCall += UpdateSelectionPreview;
        }

        internal static bool TryBeginEditPreview(
            KimodoConstraintMarker marker,
            out ConstraintPreviewContext context,
            out string entryId,
            out string error)
        {
            context = default;
            entryId = string.Empty;
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                    marker,
                    out context,
                    out error))
            {
                return false;
            }

            entryId = KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker);
            if (EditPreviews.TryGetValue(context.PreviewKey, out _))
            {
                KimodoConstraintPreviewRenderer.DestroyScope(context);
                EditPreviews.Remove(context.PreviewKey);
            }

            // Populate the marker's canonical SampleResult before the Window
            // creates the shared Inspector editor. Otherwise the first draw
            // can expose default/empty Root2D and effector fields even though
            // the Timeline pose is available for sampling.
            if (marker.autoSample &&
                !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(marker, out error))
            {
                return false;
            }

            if (!KimodoConstraintMarkerPosePreview.TryRenderMarkerPreview(
                    marker,
                    context,
                    out error))
            {
                return false;
            }

            EditPreviews[context.PreviewKey] = new EditPreviewRegistration(context);
            return true;
        }

        internal static bool TryRenderEditPreview(
            KimodoConstraintMarker marker,
            ConstraintPreviewContext context,
            out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!EditPreviews.ContainsKey(context.PreviewKey))
            {
                error = "edit preview is not registered";
                return false;
            }

            return KimodoConstraintMarkerPosePreview.TryRenderMarkerPreview(
                marker,
                context,
                out error);
        }

        internal static bool TryUpdateMarkerPreview(
            KimodoConstraintMarker marker,
            ConstraintPreviewContext editContext,
            bool renderEditPreview,
            out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (marker.autoSample &&
                !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(marker, out error))
            {
                return false;
            }

            if (renderEditPreview)
            {
                return TryRenderEditPreview(marker, editContext, out error);
            }

            SchedulePreviewUpdate();
            return true;
        }

        internal static void EndEditPreview(
            ConstraintPreviewContext context,
            string entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return;
            }

            KimodoConstraintPreviewRenderer.DestroyScope(context);
            EditPreviews.Remove(context.PreviewKey);
            SceneView.RepaintAll();
        }

        private static void UpdateSelectionPreview()
        {
            refreshQueued = false;
            var groups = new Dictionary<string, List<ConstraintPreviewItem>>(StringComparer.Ordinal);
            var contexts = new Dictionary<string, ConstraintPreviewContext>(StringComparer.Ordinal);
            List<KimodoConstraintMarker> selectedMarkers = CollectSelectedConstraintMarkers();
            for (int i = 0; i < selectedMarkers.Count; i++)
            {
                KimodoConstraintMarker marker = selectedMarkers[i];
                if (marker == null || !marker.constraintEnabled ||
                    KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker) ||
                    !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(marker, out _ ) ||
                    !KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                        marker, out ConstraintPreviewContext context, out _) ||
                    !KimodoConstraintMarkerPosePreview.TryBuildMarkerPreviewRequest(
                        marker,
                        context,
                        "marker:" + KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker),
                        SelectionPreviewColor,
                        false,
                        out ConstraintPreviewRequest item,
                        out _))
                {
                    continue;
                }

                if (!groups.TryGetValue(context.PreviewKey, out List<ConstraintPreviewItem> items))
                {
                    groups.Add(context.PreviewKey, items = new List<ConstraintPreviewItem>());
                    contexts.Add(context.PreviewKey, context);
                }
                items.Add(item);
            }

            AppendSelectedBoundaryPreviews(groups, contexts);
            foreach (KeyValuePair<string, ConstraintPreviewContext> previous in RenderedContexts)
            {
                if (!contexts.ContainsKey(previous.Key))
                {
                    KimodoConstraintPreviewRenderer.DestroyScope(previous.Value);
                }
            }

            RenderedContexts.Clear();
            foreach (KeyValuePair<string, List<ConstraintPreviewItem>> group in groups)
            {
                ConstraintPreviewContext context = contexts[group.Key];
                if (KimodoConstraintPreviewRenderer.RenderPreview(
                        context, group.Value, out _, EntryPrefix))
                {
                    RenderedContexts[group.Key] = context;
                }
            }
            SceneView.RepaintAll();
        }

        private static void AppendSelectedBoundaryPreviews(
            Dictionary<string, List<ConstraintPreviewItem>> groups,
            Dictionary<string, ConstraintPreviewContext> contexts)
        {
            foreach (TimelineClip timelineClip in TimelineEditor.selectedClips ?? Array.Empty<TimelineClip>())
            {
                if (timelineClip?.asset is not KimodoPlayableClip playable || !playable.showConstraint) continue;
                bool inside = playable.insideConstraintEnabled;
                bool outside = playable.inOutConstraintMode == KimodoInOutConstraintMode.Outside;
                if (!inside && !outside) continue;
                if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(timelineClip, out var source, out _)) continue;
                TimelineClip sampleClip = timelineClip;
                if (inside && (playable.insideConstraintSourceTrack != null || playable.insideConstraintSourceAsset != null) &&
                    !KimodoInOutConstraintAdapter.TryResolveSourceClip(
                        timelineClip,
                        playable.insideConstraintSourceTrack,
                        playable.insideConstraintSourceAsset,
                        playable.insideConstraintSourceClipStart,
                        out sampleClip,
                        out string sourceError))
                {
                    Debug.LogWarning($"[Kimodo][InsideConstraint] Preview: {sourceError}");
                    continue;
                }
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(playable.bridgeModelName);
                int frameCount = KimodoFrameTimeUtility.SecondsToFrameCount(timelineClip.duration, frameRate);
                var begins = new List<KimodoMarkerSampleResult>();
                var ends = new List<KimodoMarkerSampleResult>();
                if (inside)
                {
                    if (!KimodoInOutConstraintAdapter.TryResolveAnimationClip(sampleClip, out AnimationClip animation, out _)) continue;
                    if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(sampleClip, out var insideContext, out _)) continue;
                    var sourceAvatar = KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(insideContext.Track, insideContext.Animator);
                    int available = Mathf.Min(frameCount, Mathf.FloorToInt(animation.length * frameRate + 1e-5f));
                    if (available <= 0) continue;
                    int count = Mathf.Min(Mathf.Max(1, playable.insideKeyframeSampleCount), available);
                    foreach (int frame in KimodoInOutConstraintTools.BuildWindowSampleFrames(available, count, true))
                    {
                        if (KimodoRetargetToolsEditor.TrySampleMarkerForClip(animation, "fullbody", frame / (double)frameRate,
                                sourceAvatar.Avatar, null, playable.bridgeModelName, out var pose, out _))
                            begins.Add(pose);
                    }
                }
                else if (!KimodoInOutConstraintAdapter.TryBuildBoundarySamplesForPreview(sampleClip,
                    KimodoInOutConstraintMode.Outside, playable.enableInConstraint, playable.enableOutConstraint,
                    frameCount, out begins, out ends, out string warning))
                {
                    Debug.LogWarning($"[Kimodo][OutsideConstraint] Preview: {warning}");
                    continue;
                }
                if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(sampleClip, out var sampleContext, out _))
                    continue;
                var avatar = KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(sampleContext.Track, sampleContext.Animator);
                var context = new ConstraintPreviewContext(KimodoUnityObjectIdUtility.IdHash(playable),
                    KimodoUnityObjectIdUtility.IdHash(sampleContext.Animator), KimodoUnityObjectIdUtility.IdHash(sampleContext.Track),
                    sampleContext.ModelName, KimodoRigProfileDatabase.ResolveRigTypeFromModelName(sampleContext.ModelName), avatar.Avatar);
                if (!groups.TryGetValue(context.PreviewKey, out var items))
                {
                    groups.Add(context.PreviewKey, items = new List<ConstraintPreviewItem>());
                    contexts.Add(context.PreviewKey, context);
                }
                AddBoundaryItems(items, begins, inside ? "inside" : "in", new Color(0.3f, 0.8f, 1f));
                AddBoundaryItems(items, ends, "out", new Color(1f, 0.65f, 0.3f));
            }
        }

        private static void AddBoundaryItems(List<ConstraintPreviewItem> items,
            List<KimodoMarkerSampleResult> samples, string boundary, Color color)
        {
            if (samples == null) return;
            for (int i = 0; i < samples.Count; i++)
                items.Add(new ConstraintPreviewItem
                {
                    EntryId = boundary + ":" + i, SampleData = samples[i], ConstraintType = "fullbody",
                    PreviewSemantic = ConstraintPreviewSemantic.InOutPosePreview,
                    HandlesEnabled = false, PreviewColor = color, ColorMode = PreviewColorMode.Override
                });
        }

        private static List<KimodoConstraintMarker> CollectSelectedConstraintMarkers()
        {
            var result = new List<KimodoConstraintMarker>();
            var seen = new HashSet<int>();

            UnityEngine.Object[] selected = Selection.objects;
            for (int i = 0; i < selected.Length; i++)
            {
                if (selected[i] is KimodoConstraintMarker marker &&
                    marker.ConstraintPreviewEnabled &&
                    seen.Add(KimodoUnityObjectIdUtility.IdHash(marker)))
                {
                    result.Add(marker);
                }
            }

            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips == null)
            {
                return result;
            }

            for (int i = 0; i < selectedClips.Length; i++)
            {
                TimelineClip clip = selectedClips[i];
                KimodoPlayableClip playable = clip?.asset as KimodoPlayableClip;
                TrackAsset track = clip?.GetParentTrack();
                if (playable == null || !playable.ConstraintPreviewEnabled || track == null)
                {
                    continue;
                }

                List<KimodoConstraintMarker> references =
                    KimodoTimelineConstraintMarkerSampler.CollectMarkersForClip(track, clip);
                for (int markerIndex = 0; markerIndex < references.Count; markerIndex++)
                {
                    KimodoConstraintMarker marker = references[markerIndex];
                    if (marker != null && seen.Add(KimodoUnityObjectIdUtility.IdHash(marker)))
                    {
                        result.Add(marker);
                    }
                }
            }

            return result;
        }

        private static void OnSceneClosing(Scene _, bool __)
        {
            Clear();
        }

        private static void OnActiveSceneChanged(Scene _, Scene __)
        {
            Clear();
            SchedulePreviewUpdate();
        }

        private static void Clear()
        {
            foreach (KeyValuePair<string, ConstraintPreviewContext> context in RenderedContexts)
            {
                KimodoConstraintPreviewRenderer.DestroyScope(context.Value);
            }
            RenderedContexts.Clear();

            foreach (EditPreviewRegistration edit in EditPreviews.Values)
            {
                KimodoConstraintPreviewRenderer.DestroyScope(edit.Context);
            }
            EditPreviews.Clear();
        }

        private sealed class EditPreviewRegistration
        {
            internal readonly ConstraintPreviewContext Context;

            internal EditPreviewRegistration(ConstraintPreviewContext context)
            {
                Context = context;
            }
        }
    }
}
