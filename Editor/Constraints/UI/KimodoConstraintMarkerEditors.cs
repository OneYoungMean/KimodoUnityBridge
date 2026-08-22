using System;
using System.Collections.Generic;
using System.Globalization;
using CharacterAnimationCli.Unity;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintMarkerEditorUtility
    {
        public const double KimodoFps = 30.0;
        private static readonly Dictionary<int, string> CachedIntStrings = new Dictionary<int, string>();

        internal static bool TryGetMarkerTrack(IMarker marker, out TrackAsset track)
        {
            track = marker?.parent as TrackAsset;
            return track != null;
        }

        internal static string GetCachedIntString(int value)
        {
            if (!CachedIntStrings.TryGetValue(value, out string cached))
            {
                cached = value.ToString(CultureInfo.InvariantCulture);
                CachedIntStrings[value] = cached;
            }
            return cached;
        }

        public static bool TryGetClipRangeForMarker(IMarker marker, out TimelineClip clipRange)
        {
            clipRange = null;
            if (!TryGetMarkerTrack(marker, out TrackAsset track))
            {
                return false;
            }

            _ = track.end; // Refresh Timeline's calculated pre/post extrapolation spans after clip edits.
            foreach (TimelineClip clip in track.GetClips())
            {
                if (!(clip?.asset is AnimationPlayableAsset) ||
                    !IsTimeInClipFrameRange(marker.time, clip) && !clip.IsExtrapolatedTime(marker.time))
                {
                    continue;
                }

                if (clipRange == null || clip.start > clipRange.start)
                {
                    clipRange = clip;
                }
            }

            return clipRange != null;
        }

        internal static bool IsTimeInClipFrameRange(double time, TimelineClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            double frameRate = clip.GetParentTrack()?.timelineAsset?.editorSettings.frameRate ??
                KimodoMotionModelProfiles.DefaultFrameRate;
            int timeFrame = KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(time, frameRate);
            int startFrame = KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(clip.start, frameRate);
            int endFrame = KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(clip.end, frameRate);
            return KimodoTimelinePreviewRefreshUtility.ApproximatelyTimelineTime(time, clip.start) ||
                timeFrame >= startFrame && timeFrame < endFrame;
        }

        public static bool TryUpdateAutoSampleMarkerData(KimodoConstraintMarker marker, bool forceRefresh, out string error)
        {
            return KimodoConstraintMarkerSampling.TryUpdateAutoSampleMarkerData(marker, forceRefresh, out error);
        }

        public static bool TryRefreshMarkerCache(KimodoConstraintMarker marker, out string error)
        {
            return KimodoConstraintMarkerSampling.TryRefreshMarkerCache(marker, out error);
        }

internal static void DrawEnabledField(SerializedObject so)
        {
            SerializedProperty enabled = so?.FindProperty("constraintEnabled");
            if (enabled == null)
            {
                return;
            }

            bool wasEnabled = enabled.boolValue;
            EditorGUILayout.PropertyField(enabled, new GUIContent("Enabled"));
            if (!wasEnabled && enabled.boolValue)
            {
                KimodoConstraintSelectionPreviewTool.ForceRefresh();
            }
        }

public static void MoveMarkerToTime(IMarker marker, double globalTime)
        {
            if (marker == null)
            {
                return;
            }

            UnityEngine.Object markerObject = marker as UnityEngine.Object;
            UnityEngine.Object parentTrackObject = marker.parent as UnityEngine.Object;
            if (markerObject != null)
            {
                Undo.RecordObject(markerObject, "Move Kimodo Constraint Marker");
            }
            if (parentTrackObject != null)
            {
                Undo.RecordObject(parentTrackObject, "Move Kimodo Constraint Marker");
            }

            if (marker is KimodoConstraintMarker kimodoMarker)
            {
                ClearMarkerEditorCaches(kimodoMarker);
                kimodoMarker.time = globalTime;
                if (kimodoMarker.autoSample)
                {
                    if (!TryUpdateAutoSampleMarkerData(kimodoMarker, forceRefresh: true, out string sampleError))
                    {
                        Debug.LogWarning($"[Kimodo][ConstraintMarker] Auto sample after marker move failed: {sampleError}");
                    }
                }
                // AutoSample=false keeps the authored muscle/IK payload. The
                // preview still needs a render pass after a time edit, but it
                // must not clear/invoke the timeline sampling caches.
                if (kimodoMarker.autoSample)
                {
                    KimodoConstraintSelectionPreviewTool.ForceRefresh();
                }
                else
                {
                    KimodoConstraintSelectionPreviewTool.ScheduleRefresh();
                }
                SceneView.RepaintAll();
            }


            if (markerObject != null)
            {
                EditorUtility.SetDirty(markerObject);
            }
            if (parentTrackObject != null)
            {
                EditorUtility.SetDirty(parentTrackObject);
            }

            if (TimelineEditor.inspectedAsset != null)
            {
                EditorUtility.SetDirty(TimelineEditor.inspectedAsset);
            }

            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsModified);
        }

        internal static double GetMarkerTimeForDisplay(IMarker marker)
        {
            return Math.Max(0.0, marker?.time ?? 0.0);
        }

        public static void DrawMarkerTimeField(SerializedObject so, IMarker marker)
        {
            if (so == null || marker == null)
            {
                return;
            }

            // Constraint marker time is always the absolute Timeline time. A
            // TimelineClip's local evaluation time is only an implementation
            // detail of source animation sampling and must never be shown or
            // written back here.
            double displayMarkerTime = Math.Round(
                GetMarkerTimeForDisplay(marker),
                4,
                MidpointRounding.AwayFromZero);

            double editedTime = EditorGUILayout.DoubleField(
                new GUIContent("Marker Time (seconds)", "The Timeline marker time is the only sampling time."),
                displayMarkerTime);
            double normalizedEdited = Math.Max(0.0, editedTime);
            if (Math.Abs(normalizedEdited - marker.time) > 1e-9)
            {
                MoveMarkerToTime(marker, normalizedEdited);
            }
        }

public static void NotifyInspectorChanged(KimodoConstraintMarker marker)
        {
            if (marker != null)
            {
                if (marker.constraintEnabled)
                {
                    ClearMarkerEditorCaches(marker);
                }
                else
                {
                    ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
                }
                EditorUtility.SetDirty(marker);
            }

            SceneView.RepaintAll();
        }

public static void ClearMarkerPoseCachePreview(KimodoConstraintMarker marker, bool keepIfOverrideWindowOpen)
        {
            if (marker == null)
            {
                return;
            }

            ClearMarkerEditorCaches(marker);

            if (keepIfOverrideWindowOpen && KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker))
            {
                return;
            }

            KimodoConstraintSelectionPreviewTool.ForceRefresh();
            SceneView.RepaintAll();
        }

        internal static void ClearMarkerEditorCaches(KimodoConstraintMarker marker)
        {
            KimodoConstraintMarkerSampling.ClearMarkerCache(marker);
        }

        public static bool TryBuildRenderContextForMarker(KimodoConstraintMarker marker, out PoseCacheRenderContext context, out string error)
        {
            return KimodoConstraintMarkerPosePreview.TryBuildRenderContextForMarker(marker, out context, out error);
        }

        public static bool TryBuildRenderContextForPlayableClip(KimodoPlayableClip playableClip, out PoseCacheRenderContext context, out TimelineClip timelineClip, out string error, TimelineClip timelineClipOverride = null)
        {
            return KimodoConstraintMarkerPosePreview.TryBuildRenderContextForPlayableClip(playableClip, out context, out timelineClip, out error, timelineClipOverride);
        }

        public static bool TryRenderMarkerToPoseCache(KimodoConstraintMarker marker, out string error)
        {
            return KimodoConstraintMarkerPosePreview.TryRenderMarkerToPoseCache(marker, out error);
        }

        internal static bool TryRenderMarkerToPoseCache(KimodoConstraintMarker marker, PoseCacheRenderContext context, out string error)
        {
            return KimodoConstraintMarkerPosePreview.TryRenderMarkerToPoseCache(marker, context, out error);
        }

public static void DrawOverrideEditButton(SerializedObject so, KimodoConstraintMarker marker)
        {
            if (so == null || marker == null)
            {
                return;
            }

            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker);
            using (new EditorGUI.DisabledScope(!marker.constraintEnabled))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Refresh Cache", "Force re-sample the marker pose and rebuild the preview cache."), GUILayout.Height(22f)))
                {
                    if (!TryRefreshMarkerCache(marker, out string refreshError))
                    {
                        Debug.LogWarning($"[Kimodo][ConstraintMarker] Refresh cache failed: {refreshError}");
                    }
                }

                string label = windowOpen ? "Reopen Edit" : "Edit";
                if (GUILayout.Button(new GUIContent(label, "Open the constraint edit window."), GUILayout.Height(22f)))
                {
                    KimodoConstraintMarker markerToOpen = marker;
                    EditorApplication.delayCall += () =>
                    {
                        if (markerToOpen != null && markerToOpen.constraintEnabled)
                        {
                            KimodoConstraintOverrideEditWindow.ShowWindow(markerToOpen);
                        }
                    };
                }
            }
        }

        public static void HandleDeleteCommand(KimodoConstraintMarker marker)
        {
            if (marker == null)
            {
                return;
            }

            Event currentEvent = Event.current;
            if (currentEvent == null)
            {
                return;
            }

            bool isDeleteCommand =
                string.Equals(currentEvent.commandName, "Delete", StringComparison.Ordinal) ||
                string.Equals(currentEvent.commandName, "SoftDelete", StringComparison.Ordinal);
            if (!isDeleteCommand)
            {
                return;
            }

            if (currentEvent.type == EventType.ValidateCommand)
            {
                currentEvent.Use();
                return;
            }

            if (currentEvent.type != EventType.ExecuteCommand)
            {
                return;
            }

            if (TryDeleteMarkerWithUndo(marker, out string error))
            {
                currentEvent.Use();
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.LogWarning($"[Kimodo][ConstraintMarker] Delete failed: {error}");
            }
        }

        public static bool TryDeleteMarkerWithUndo(KimodoConstraintMarker marker, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!(marker.parent is TrackAsset track))
            {
                error = "marker parent track not found";
                return false;
            }

            UnityEngine.Object markerObject = marker;
            UnityEngine.Object inspectedAsset = TimelineEditor.inspectedAsset;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete Kimodo Constraint Marker");

            if (inspectedAsset != null)
            {
                Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { track, inspectedAsset }, "Delete Kimodo Constraint Marker");
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(track, "Delete Kimodo Constraint Marker");
            }

            ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
            track.DeleteMarker(marker);

            if (markerObject != null)
            {
                EditorUtility.SetDirty(markerObject);
            }

            EditorUtility.SetDirty(track);
            if (inspectedAsset != null)
            {
                EditorUtility.SetDirty(inspectedAsset);
            }

            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsAddedOrRemoved);
            Undo.CollapseUndoOperations(undoGroup);
            return true;
        }

internal static string GetMarkerEntryId(KimodoConstraintMarker marker)
        {
            return marker == null ? string.Empty : GetCachedIntString(KimodoUnityObjectIdUtility.IdHash(marker));
        }
    }
}
