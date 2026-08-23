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
        private static bool forceRefreshRequested;

        static KimodoConstraintSelectionPreviewTool()
        {
            Selection.selectionChanged += ScheduleRefresh;
            Undo.undoRedoPerformed += ScheduleRefresh;
            EditorApplication.quitting += Clear;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            ScheduleRefresh();
        }

        internal static void ScheduleRefresh()
        {
            if (refreshQueued) return;
            refreshQueued = true;
            EditorApplication.delayCall += Refresh;
        }

        internal static void ForceRefresh()
        {
            forceRefreshRequested = true;
            ScheduleRefresh();
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

            if (!KimodoConstraintMarkerPosePreview.TryRenderMarkerToPoseCache(
                    marker,
                    context,
                    out error))
            {
                return false;
            }

            EditPreviews[context.ContextKey] = new EditPreviewRegistration(context, entryId);
            return true;
        }

        internal static bool TryRefreshEditPreview(
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

            return KimodoConstraintMarkerPosePreview.TryRenderMarkerToPoseCache(
                marker,
                context,
                out error);
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

        private static void Refresh()
        {
            refreshQueued = false;
            if (forceRefreshRequested)
            {
                forceRefreshRequested = false;
            }

            var groups = new Dictionary<string, List<PoseCacheRenderItem>>(StringComparer.Ordinal);
            var contexts = new Dictionary<string, PoseCacheRenderContext>(StringComparer.Ordinal);
            UnityEngine.Object[] selected = Selection.objects;
            for (int i = 0; i < selected.Length; i++)
            {
                KimodoConstraintMarker marker = selected[i] as KimodoConstraintMarker;
                if (marker == null || !marker.constraintEnabled ||
                    KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker) ||
                    !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(
                        marker, forceRefresh: false, out _ ) ||
                    !KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                        marker, out PoseCacheRenderContext context, out _) ||
                    !KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(
                        marker, marker.SampleData, out KimodoMarkerSampleResult sample, out _))
                {
                    continue;
                }

                if (!groups.TryGetValue(context.ContextKey, out List<PoseCacheRenderItem> items))
                {
                    groups.Add(context.ContextKey, items = new List<PoseCacheRenderItem>());
                    contexts.Add(context.ContextKey, context);
                }
                items.Add(new PoseCacheRenderItem
                {
                    EntryId = "marker:" + KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker),
                    SampleData = sample,
                    ConstraintType = marker.ConstraintType,
                    ConstraintMode = marker.ConstraintMode,
                    HighlightJoints = KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                    PreviewColor = new Color(0.48f, 0.76f, 1f),
                    Visible = true,
                    SourceMarker = marker
                });
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

        private static void OnSceneClosing(Scene _, bool __)
        {
            Clear();
        }

        private static void OnActiveSceneChanged(Scene _, Scene __)
        {
            Clear();
            ScheduleRefresh();
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
