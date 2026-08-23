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
        private static readonly Dictionary<string, PoseCacheRenderContext> RenderedContexts =
            new Dictionary<string, PoseCacheRenderContext>();
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
            out PoseCacheRenderContext context,
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
            if (EditPreviews.TryGetValue(context.ContextKey, out EditPreviewRegistration previous))
            {
                KimodoConstraintPoseCache.DestroyEntry(context, previous.EntryId);
                EditPreviews.Remove(context.ContextKey);
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

            EditPreviews[context.ContextKey] = new EditPreviewRegistration(context, entryId);
            return true;
        }

        internal static bool TryRenderEditPreview(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!EditPreviews.ContainsKey(context.ContextKey))
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
            PoseCacheRenderContext editContext,
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
            PoseCacheRenderContext context,
            string entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return;
            }

            KimodoConstraintPoseCache.DestroyEntry(context, entryId);
            EditPreviews.Remove(context.ContextKey);
            SceneView.RepaintAll();
        }

        private static void UpdateSelectionPreview()
        {
            refreshQueued = false;
            var groups = new Dictionary<string, List<PoseCacheRenderItem>>(StringComparer.Ordinal);
            var contexts = new Dictionary<string, PoseCacheRenderContext>(StringComparer.Ordinal);
            List<KimodoConstraintMarker> selectedMarkers = CollectSelectedConstraintMarkers();
            for (int i = 0; i < selectedMarkers.Count; i++)
            {
                KimodoConstraintMarker marker = selectedMarkers[i];
                if (marker == null || !marker.constraintEnabled ||
                    KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker) ||
                    !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(marker, out _ ) ||
                    !KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                        marker, out PoseCacheRenderContext context, out _) ||
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

                if (!groups.TryGetValue(context.ContextKey, out List<PoseCacheRenderItem> items))
                {
                    groups.Add(context.ContextKey, items = new List<PoseCacheRenderItem>());
                    contexts.Add(context.ContextKey, context);
                }
                items.Add(item);
            }

            foreach (KeyValuePair<string, PoseCacheRenderContext> previous in RenderedContexts)
            {
                if (!contexts.ContainsKey(previous.Key))
                {
                    KimodoConstraintPoseCache.DestroyEntriesInScope(previous.Value, EntryPrefix);
                }
            }

            RenderedContexts.Clear();
            foreach (KeyValuePair<string, List<PoseCacheRenderItem>> group in groups)
            {
                PoseCacheRenderContext context = contexts[group.Key];
                if (KimodoConstraintPoseCache.RenderBatch(
                        context, group.Value, out _, EntryPrefix))
                {
                    RenderedContexts[group.Key] = context;
                }
            }
            SceneView.RepaintAll();
        }

        private static List<KimodoConstraintMarker> CollectSelectedConstraintMarkers()
        {
            var result = new List<KimodoConstraintMarker>();
            var seen = new HashSet<int>();

            UnityEngine.Object[] selected = Selection.objects;
            for (int i = 0; i < selected.Length; i++)
            {
                if (selected[i] is KimodoConstraintMarker marker &&
                    seen.Add(marker.GetInstanceID()))
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
                    if (marker != null && seen.Add(marker.GetInstanceID()))
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
            foreach (KeyValuePair<string, PoseCacheRenderContext> context in RenderedContexts)
            {
                KimodoConstraintPoseCache.DestroyEntriesInScope(context.Value, EntryPrefix);
            }
            RenderedContexts.Clear();

            foreach (EditPreviewRegistration edit in EditPreviews.Values)
            {
                KimodoConstraintPoseCache.DestroyEntry(edit.Context, edit.EntryId);
            }
            EditPreviews.Clear();
        }

        private sealed class EditPreviewRegistration
        {
            internal readonly PoseCacheRenderContext Context;
            internal readonly string EntryId;

            internal EditPreviewRegistration(PoseCacheRenderContext context, string entryId)
            {
                Context = context;
                EntryId = entryId;
            }
        }
    }
}
