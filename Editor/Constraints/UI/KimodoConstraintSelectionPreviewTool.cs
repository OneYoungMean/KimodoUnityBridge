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
        }
    }
}
