using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
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
        private const double PollIntervalSeconds = 0.2d;
        private static readonly Color SelectedMarkerPreviewColor = new Color(0.48f, 0.76f, 1f);
        private static readonly Color UnselectedMarkerPreviewColor = new Color(0.66f, 0.66f, 0.66f);

        private sealed class PreviewSource
        {
            public IKimodoConstraintPreviewSelectable Selectable;
            public UnityEngine.Object Object;
            public TimelineClip TimelineClip;
            public double Time;
        }

        private sealed class PreviewGroup
        {
            public PoseCacheRenderContext Context;
            public readonly List<PoseCacheRenderItem> Items = new List<PoseCacheRenderItem>();
        }

        private sealed class PreviewLabel
        {
            public PoseCacheRenderContext Context;
            public string EntryId;
            public string Text;
            public Color Color;
            public int Priority;
            public double Time;
        }

        private static readonly Dictionary<string, PoseCacheRenderContext> RenderedContexts =
            new Dictionary<string, PoseCacheRenderContext>(StringComparer.Ordinal);
        private static readonly List<PreviewLabel> Labels = new List<PreviewLabel>();
        private static bool refreshQueued;
        private static bool forceRefreshRequested;
        private static double nextPollTime;
        private static int selectionSignature;

        static KimodoConstraintSelectionPreviewTool()
        {
            Selection.selectionChanged += ScheduleRefresh;
            Undo.undoRedoPerformed += ScheduleRefresh;
            EditorApplication.update += PollSelection;
            EditorApplication.quitting += Clear;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            SceneView.duringSceneGui += DrawLabels;
            ScheduleRefresh();
        }

        internal static void ScheduleRefresh()
        {
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            EditorApplication.delayCall += Refresh;
        }

        internal static void ForceRefresh()
        {
            forceRefreshRequested = true;
            ScheduleRefresh();
        }

        private static void PollSelection()
        {
            if (EditorApplication.timeSinceStartup < nextPollTime)
            {
                return;
            }

            nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            if (ComputeSelectionSignature() != selectionSignature)
            {
                ScheduleRefresh();
            }
        }

        private static void OnSceneClosing(Scene _, bool __)
        {
            ClearSceneSamplingState();
        }

        private static void OnActiveSceneChanged(Scene _, Scene __)
        {
            ClearSceneSamplingState();
            ScheduleRefresh();
        }

        private static void ClearSceneSamplingState()
        {
            KimodoTimelineConstraintClipCache.Clear();
            KimodoConstraintMarkerEditorUtility.ClearSamplingCaches();
            Clear();
            selectionSignature = 0;
        }

        private static void Refresh()
        {
            refreshQueued = false;
            if (forceRefreshRequested)
            {
                forceRefreshRequested = false;
                KimodoTimelineConstraintClipCache.Clear();
                KimodoConstraintMarkerEditorUtility.ClearSamplingCaches();
                Clear();
            }

            List<PreviewSource> sources = CollectSources();
            sources.Sort(CompareSources);

            var groups = new Dictionary<string, PreviewGroup>(StringComparer.Ordinal);
            Labels.Clear();
            for (int i = 0; i < sources.Count; i++)
            {
                PreviewSource source = sources[i];
                if (source.Object is KimodoConstraintMarker marker)
                {
                    AddMarkerPreview(groups, marker, ResolveMarkerPreviewColor(marker));
                }
                else if (source.Object is KimodoPlayableClip playable)
                {
                    AddClipPreview(groups, playable, source.TimelineClip);
                }
            }

            foreach (KeyValuePair<string, PoseCacheRenderContext> previous in RenderedContexts)
            {
                if (!groups.ContainsKey(previous.Key))
                {
                    KimodoConstraintPoseCache.DestroyEntriesInScope(previous.Value, EntryPrefix);
                }
            }

            RenderedContexts.Clear();
            foreach (KeyValuePair<string, PreviewGroup> pair in groups)
            {
                PreviewGroup group = pair.Value;
                if (!KimodoConstraintPoseCache.RenderBatch(
                        group.Context,
                        group.Items,
                        out string error,
                        EntryPrefix))
                {
                    Debug.LogWarning($"[Kimodo][ConstraintPreview] {error}");
                    KimodoConstraintPoseCache.DestroyEntriesInScope(group.Context, EntryPrefix);
                    continue;
                }

                RenderedContexts[pair.Key] = group.Context;
            }

            Labels.Sort(CompareLabels);
            selectionSignature = ComputeSelectionSignature();
            SceneView.RepaintAll();
        }

        private static List<PreviewSource> CollectSources()
        {
            var result = new List<PreviewSource>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips != null)
            {
                for (int i = 0; i < selectedClips.Length; i++)
                {
                    TimelineClip timelineClip = selectedClips[i];
                    if (timelineClip?.asset is KimodoPlayableClip playable)
                    {
                        AddSource(result, keys, playable, timelineClip, GetTimelineClipKey(timelineClip));
                    }
                }
            }

            UnityEngine.Object[] selectedObjects = Selection.objects;
            for (int i = 0; i < selectedObjects.Length; i++)
            {
                UnityEngine.Object selected = selectedObjects[i];
                if (selected is KimodoConstraintMarker marker)
                {
                    if (KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip markerClip) &&
                        markerClip?.asset is KimodoPlayableClip markerPlayable)
                    {
                        AddSource(result, keys, markerPlayable, markerClip, GetTimelineClipKey(markerClip));
                    }
                    else
                    {
                        AddSource(result, keys, marker, null,
                            "marker:" + KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker));
                    }
                }
                else if (selected is KimodoPlayableClip playable)
                {
                    TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(playable);
                    AddSource(result, keys, playable, timelineClip, GetTimelineClipKey(timelineClip));
                }
            }

            return result;
        }

        private static void AddSource(
            List<PreviewSource> result,
            HashSet<string> keys,
            UnityEngine.Object sourceObject,
            TimelineClip timelineClip,
            string key)
        {
            if (!(sourceObject is IKimodoConstraintPreviewSelectable selectable) ||
                !selectable.ConstraintPreviewEnabled ||
                !keys.Add(key))
            {
                return;
            }

            result.Add(new PreviewSource
            {
                Selectable = selectable,
                Object = sourceObject,
                TimelineClip = timelineClip,
                Time = sourceObject is KimodoConstraintMarker marker
                    ? marker.time
                    : timelineClip?.start ?? 0.0
            });
        }

        private static string GetTimelineClipKey(TimelineClip timelineClip)
        {
            if (timelineClip == null)
            {
                return "clip:(none)";
            }

            return $"clip:{KimodoUnityObjectIdUtility.IdHash(timelineClip.GetParentTrack())}:" +
                $"{KimodoUnityObjectIdUtility.IdHash(timelineClip.asset as UnityEngine.Object)}:" +
                $"{timelineClip.start:R}:{timelineClip.duration:R}";
        }

        private static void AddMarkerPreview(
            Dictionary<string, PreviewGroup> groups,
            KimodoConstraintMarker marker,
            Color color)
        {
            if (marker == null || KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker))
            {
                return;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(
                    marker,
                    forceRefresh: false,
                    out _))
            {
                return;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                    marker,
                    out PoseCacheRenderContext context,
                    out _) ||
                !KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(
                    marker,
                    marker.SampleData,
                    out KimodoMarkerSampleResult sample,
                    out _))
            {
                return;
            }

            string entryId = "marker:" + KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker);
            AddItem(
                groups,
                context,
                entryId,
                sample,
                marker.ConstraintType,
                KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                marker.ConstraintPreviewName,
                marker.time,
                marker.ConstraintPreviewPriority,
                color,
                marker);
        }

        private static void AddClipPreview(
            Dictionary<string, PreviewGroup> groups,
            KimodoPlayableClip playable,
            TimelineClip timelineClip)
        {
            if (playable == null || timelineClip == null ||
                !KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForPlayableClip(
                    playable,
                    out PoseCacheRenderContext context,
                    out _,
                    out _,
                    timelineClip))
            {
                return;
            }

            TrackAsset track = timelineClip.GetParentTrack();
            if (track == null)
            {
                return;
            }

            List<KimodoConstraintMarker> references =
                KimodoTimelineConstraintMarkerSampler.CollectMarkersForClip(track, timelineClip);
            string clipKey = GetTimelineClipKey(timelineClip);
            for (int i = 0; i < references.Count; i++)
            {
                KimodoConstraintMarker marker = references[i];
                if (marker == null ||
                    KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker) ||
                    !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(
                        marker,
                        forceRefresh: false,
                        out _))
                {
                    continue;
                }

                if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(
                        marker,
                        marker.SampleData,
                        out KimodoMarkerSampleResult sample,
                        out _))
                {
                    continue;
                }

                AddItem(
                    groups,
                    context,
                    $"{clipKey}:marker:{KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker)}",
                    sample,
                    marker.ConstraintType,
                    KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                    $"{playable.ConstraintPreviewName} · {marker.ConstraintType}",
                    marker.time,
                    playable.ConstraintPreviewPriority,
                    ResolveMarkerPreviewColor(marker),
                    marker);
            }

            int frameCount = Mathf.Max(
                1,
                KimodoFrameTimeUtility.SecondsToFrameCount(
                    timelineClip.duration,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(playable.bridgeModelName)));
            if (playable.inOutConstraintMode == KimodoInOutConstraintMode.None ||
                !KimodoInOutConstraintAdapter.TryBuildBoundarySamplesForPreview(
                    timelineClip,
                    playable.inOutConstraintMode,
                    playable.enableInConstraint,
                    playable.enableOutConstraint,
                    KimodoInOutConstraintTools.ClampFrameCount(frameCount),
                    out KimodoMarkerSampleResult begin,
                    out KimodoMarkerSampleResult end,
                    out _))
            {
                return;
            }

            if (begin != null)
            {
                AddItem(
                    groups,
                    context,
                    $"{clipKey}:in",
                    begin,
                    "fullbody",
                    null,
                    $"{playable.ConstraintPreviewName} · In",
                    timelineClip.start,
                    playable.ConstraintPreviewPriority,
                    UnselectedMarkerPreviewColor);
            }
            if (end != null)
            {
                AddItem(
                    groups,
                    context,
                    $"{clipKey}:out",
                    end,
                    "fullbody",
                    null,
                    $"{playable.ConstraintPreviewName} · Out",
                    timelineClip.start + end.sampleTime,
                    playable.ConstraintPreviewPriority,
                    UnselectedMarkerPreviewColor);
            }
        }

        private static Color ResolveMarkerPreviewColor(KimodoConstraintMarker marker)
        {
            if (marker == null)
            {
                return UnselectedMarkerPreviewColor;
            }

            UnityEngine.Object[] selectedObjects = Selection.objects;
            for (int i = 0; i < selectedObjects.Length; i++)
            {
                if (ReferenceEquals(selectedObjects[i], marker))
                {
                    return SelectedMarkerPreviewColor;
                }
            }

            return UnselectedMarkerPreviewColor;
        }

        private static void AddItem(
            Dictionary<string, PreviewGroup> groups,
            PoseCacheRenderContext context,
            string entryId,
            KimodoMarkerSampleResult sample,
            string constraintType,
            List<string> highlightJoints,
            string name,
            double time,
            int priority,
            Color color,
            KimodoConstraintMarker sourceMarker = null)
        {
            if (!groups.TryGetValue(context.ContextKey, out PreviewGroup group))
            {
                group = new PreviewGroup { Context = context };
                groups.Add(context.ContextKey, group);
            }

            group.Items.Add(new PoseCacheRenderItem
            {
                EntryId = entryId,
                SampleData = sample,
                ConstraintType = constraintType,
                HighlightJoints = highlightJoints,
                PreviewColor = color,
                Visible = true,
                SourceMarker = sourceMarker
            });
            Labels.Add(new PreviewLabel
            {
                Context = context,
                EntryId = EntryPrefix + entryId,
                Text = $"{time:F3}s · {name}",
                Color = color,
                Priority = priority,
                Time = time
            });
        }

        private static void DrawLabels(SceneView _)
        {
            var positions = new List<Vector3>();
            for (int i = 0; i < Labels.Count; i++)
            {
                PreviewLabel label = Labels[i];
                if (!KimodoConstraintPoseCache.TryGetPreviewRoot(
                        label.Context,
                        label.EntryId,
                        out Transform root))
                {
                    continue;
                }

                int overlap = 0;
                for (int p = 0; p < positions.Count; p++)
                {
                    if ((positions[p] - root.position).sqrMagnitude < 0.04f)
                    {
                        overlap++;
                    }
                }
                positions.Add(root.position);

                var style = new GUIStyle(EditorStyles.boldLabel);
                style.alignment = TextAnchor.MiddleCenter;
                style.normal.textColor = label.Color;
                Handles.Label(
                    root.position + Vector3.down * (0.1f + overlap * 0.08f),
                    label.Text,
                    style);
            }
        }

        private static int CompareSources(PreviewSource left, PreviewSource right)
        {
            int compare = left.Selectable.ConstraintPreviewPriority.CompareTo(right.Selectable.ConstraintPreviewPriority);
            if (compare != 0)
            {
                return compare;
            }
            compare = left.Time.CompareTo(right.Time);
            return compare != 0
                ? compare
                : string.CompareOrdinal(
                    KimodoUnityObjectIdUtility.NameKey(left.Object),
                    KimodoUnityObjectIdUtility.NameKey(right.Object));
        }

        private static int CompareLabels(PreviewLabel left, PreviewLabel right)
        {
            int compare = left.Priority.CompareTo(right.Priority);
            return compare != 0 ? compare : left.Time.CompareTo(right.Time);
        }

        private static int ComputeSelectionSignature()
        {
            unchecked
            {
                int hash = 17;
                UnityEngine.Object[] selectedObjects = Selection.objects;
                for (int i = 0; i < selectedObjects.Length; i++)
                {
                    UnityEngine.Object selected = selectedObjects[i];
                    hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(selected);
                    hash = hash * 31 + (selected != null ? EditorUtility.GetDirtyCount(selected) : 0);
                }

                TimelineClip[] selectedClips = TimelineEditor.selectedClips;
                if (selectedClips != null)
                {
                    for (int i = 0; i < selectedClips.Length; i++)
                    {
                        TimelineClip timelineClip = selectedClips[i];
                        UnityEngine.Object asset = timelineClip?.asset as UnityEngine.Object;
                        hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(asset);
                        hash = hash * 31 + (asset != null ? EditorUtility.GetDirtyCount(asset) : 0);
                        hash = hash * 31 + (timelineClip?.start.GetHashCode() ?? 0);
                        hash = hash * 31 + (timelineClip?.duration.GetHashCode() ?? 0);
                        hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(timelineClip?.GetParentTrack());
                    }
                }

                hash = hash * 31 + (TimelineEditor.inspectedDirector != null
                    ? KimodoUnityObjectIdUtility.IdHash(TimelineEditor.inspectedDirector)
                    : 0);
                return hash;
            }
        }

        private static void Clear()
        {
            foreach (KeyValuePair<string, PoseCacheRenderContext> context in RenderedContexts)
            {
                KimodoConstraintPoseCache.DestroyEntriesInScope(context.Value, EntryPrefix);
            }
            RenderedContexts.Clear();
            Labels.Clear();
        }
    }
}

