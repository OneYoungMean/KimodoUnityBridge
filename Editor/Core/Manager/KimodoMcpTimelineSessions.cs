using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    public static partial class KimodoMcpTools
    {
        private const int MaxRememberedTimelineSessions = 64;
        private const string GeneratedTimelineFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Timelines";
        private static readonly Dictionary<Guid, TimelineSessionRecord> TimelineSessions = new Dictionary<Guid, TimelineSessionRecord>();
        private static readonly object TimelineSessionsLock = new object();

        public static string OpenTimelineSession(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                ResolvedCharacter character = ResolveCharacter(RequiredStringValue(arguments, "character_ref"));
                if (EditorUtility.IsPersistent(character.Root) || !character.Root.scene.IsValid())
                {
                    throw new InvalidOperationException("Timeline Session requires character_ref to resolve to a scene character.");
                }
                PlayableDirector director = ResolveDirector(RequiredStringValue(arguments, "director_ref"));
                if (!string.IsNullOrWhiteSpace(arguments.Value<string>("track_ref")))
                {
                    throw new InvalidOperationException("track_ref is not supported because a Timeline Session always creates a new TimelineAsset.");
                }
                double start = NonNegativeDouble(arguments, "start_seconds", 0.0);
                TimelineAsset timelineAsset = CreateTimelineSessionAsset(character.Name, out string timelineAssetPath);
                AnimationTrack track = timelineAsset.CreateTrack<AnimationTrack>(null, $"Kimodo MCP - {character.Animator.gameObject.name}");
                PlayableAsset previousPlayableAsset = director.playableAsset;
                double previousTime = director.time;

                Undo.RecordObject(director, "Kimodo MCP Open Timeline Session");
                director.playableAsset = timelineAsset;
                director.time = start;
                director.SetGenericBinding(track, character.Animator);
                EditorUtility.SetDirty(track);
                EditorUtility.SetDirty(timelineAsset);
                EditorUtility.SetDirty(director);
                AssetDatabase.SaveAssets();
                OpenTimelineWindow(director);

                var record = new TimelineSessionRecord(
                    Guid.NewGuid(),
                    director,
                    character.Animator,
                    timelineAsset,
                    timelineAssetPath,
                    track,
                    previousPlayableAsset,
                    previousTime,
                    start);
                lock (TimelineSessionsLock)
                {
                    PruneTimelineSessionsLocked();
                    TimelineSessions[record.Id] = record;
                }
                return Ok(new JObject
                {
                    ["timeline_session_id"] = record.Id.ToString("D"),
                    ["director_ref"] = GetObjectReference(director),
                    ["timeline_asset_ref"] = GetObjectReference(timelineAsset),
                    ["timeline_asset_path"] = timelineAssetPath,
                    ["track_ref"] = GetObjectReference(track),
                    ["next_start_seconds"] = start
                });
            });
        }

        public static string CloseTimelineSession(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                Guid id = RequiredTimelineSessionId(arguments);
                TimelineSessionRecord record;
                lock (TimelineSessionsLock)
                {
                    if (!TimelineSessions.TryGetValue(id, out record))
                    {
                        throw new InvalidOperationException($"Unknown or expired timeline_session_id '{id:D}'.");
                    }
                }
                if (HasRunningTimelineGeneration(id))
                {
                    throw new InvalidOperationException("Timeline Session still has a running generation. Cancel or wait for it before closing the session.");
                }
                lock (TimelineSessionsLock)
                {
                    TimelineSessions.Remove(id);
                }

                CloseTimelineWindow(record.TimelineAsset);
                if (record.Director != null && record.Director.playableAsset == record.TimelineAsset)
                {
                    Undo.RecordObject(record.Director, "Kimodo MCP Close Timeline Session");
                    record.Director.playableAsset = record.PreviousPlayableAsset;
                    record.Director.time = record.PreviousTime;
                    EditorUtility.SetDirty(record.Director);
                }

                string assetPath = AssetDatabase.GetAssetPath(record.TimelineAsset);
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    assetPath = record.TimelineAssetPath;
                }
                bool deleted = !string.IsNullOrWhiteSpace(assetPath) && AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.SaveAssets();
                return Ok(new JObject
                {
                    ["timeline_session_id"] = id.ToString("D"),
                    ["timeline_asset_path"] = assetPath ?? string.Empty,
                    ["asset_deleted"] = deleted,
                    ["closed"] = true
                });
            });
        }

        private static TimelineReservation PrepareTimelineReservation(JObject arguments, ResolvedCharacter character, double duration)
        {
            string value = arguments.Value<string>("timeline_session_id")?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            Guid id = ParseTimelineSessionId(value);
            lock (TimelineSessionsLock)
            {
                if (!TimelineSessions.TryGetValue(id, out TimelineSessionRecord record))
                {
                    throw new InvalidOperationException($"Unknown or expired timeline_session_id '{id:D}'.");
                }
                ValidateTimelineSession(record, character);
                return new TimelineReservation(record, record.NextStartSeconds, duration);
            }
        }

        private static void CommitTimelineReservation(TimelineReservation reservation)
        {
            if (reservation == null)
            {
                return;
            }
            lock (TimelineSessionsLock)
            {
                if (!TimelineSessions.TryGetValue(reservation.Session.Id, out TimelineSessionRecord current) ||
                    !ReferenceEquals(current, reservation.Session))
                {
                    throw new InvalidOperationException("Timeline Session was closed before generation could be started.");
                }
                current.NextStartSeconds = reservation.StartSeconds + reservation.DurationSeconds;
            }
        }

        private static async System.Threading.Tasks.Task<KimodoEditorGenerateResult> ExecuteAssetGenerationAsync(
            KimodoEditorGenerateRequest request,
            UnityEngine.Object target,
            EditorGenerateSession session,
            System.Threading.CancellationToken token,
            TimelineReservation reservation)
        {
            KimodoEditorGenerateResult result = await ExecuteAssetGenerationAsync(request, target, session, token);
            if (reservation != null)
            {
                WriteGeneratedClipToTimeline(reservation, result);
            }
            return result;
        }

        private static void WriteGeneratedClipToTimeline(TimelineReservation reservation, KimodoEditorGenerateResult result)
        {
            if (reservation?.Session == null || result?.GeneratedClip == null)
            {
                throw new InvalidOperationException("Timeline Session writeback requires a generated AnimationClip.");
            }
            TimelineSessionRecord session = reservation.Session;
            TimelineAsset timelineAsset = session.Director != null ? session.Director.playableAsset as TimelineAsset : null;
            if (session.Director == null || session.Animator == null || session.Track == null || timelineAsset == null ||
                timelineAsset != session.TimelineAsset || session.Track.timelineAsset != timelineAsset)
            {
                throw new InvalidOperationException("Timeline Session target is no longer valid.");
            }
            if (!BindingMatches(session.Director.GetGenericBinding(session.Track), session.Animator))
            {
                throw new InvalidOperationException("Timeline Session track is no longer bound to its character.");
            }

            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { timelineAsset, session.Track, session.Director },
                "Kimodo MCP Save Generated Clip To Timeline");
            TimelineClip timelineClip = session.Track.CreateClip<AnimationPlayableAsset>();
            timelineClip.start = reservation.StartSeconds;
            timelineClip.duration = reservation.DurationSeconds;
            timelineClip.displayName = string.IsNullOrWhiteSpace(result.Prompt) ? result.GeneratedClip.name : result.Prompt;
            ((AnimationPlayableAsset)timelineClip.asset).clip = result.GeneratedClip;
            reservation.TimelineClip = timelineClip;

            JArray keyframes = ParseKeyframes(result.AnalysisJson);
            if (keyframes.Count > 0)
            {
                MarkerTrack analysisTrack = session.AnalysisTrack;
                if (analysisTrack == null || analysisTrack.timelineAsset != timelineAsset)
                {
                    analysisTrack = timelineAsset.CreateTrack<MarkerTrack>(null, $"Kimodo Analysis - {session.Animator.gameObject.name}");
                    session.AnalysisTrack = analysisTrack;
                }
                WriteAnalysisMarkers(analysisTrack, reservation, keyframes);
                reservation.AnalysisTrack = analysisTrack;
                EditorUtility.SetDirty(analysisTrack);
            }

            EditorUtility.SetDirty(session.Track);
            EditorUtility.SetDirty(timelineAsset);
            EditorUtility.SetDirty(session.Director);
            AssetDatabase.SaveAssets();
        }

        private static JArray ParseKeyframes(string analysisJson)
        {
            try
            {
                return string.IsNullOrWhiteSpace(analysisJson)
                    ? new JArray()
                    : JObject.Parse(analysisJson)["keyframes"] as JArray ?? new JArray();
            }
            catch
            {
                return new JArray();
            }
        }

        private static void WriteAnalysisMarkers(MarkerTrack track, TimelineReservation reservation, JArray keyframes)
        {
            foreach (JToken keyframe in keyframes)
            {
                double localTime = keyframe.Value<double?>("time") ?? 0.0;
                localTime = Math.Max(0.0, Math.Min(reservation.DurationSeconds, localTime));
                KimodoAnalysisKeyframeMarker marker = track.CreateMarker<KimodoAnalysisKeyframeMarker>(reservation.StartSeconds + localTime);
                marker.frame = keyframe.Value<int?>("frame") ?? 0;
                marker.saliency = keyframe.Value<float?>("saliency") ?? keyframe.Value<float?>("score") ?? 0f;
                marker.reasons = string.Join(", ", (keyframe["reasons"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
            }
        }

        private static string ParseAnalysisOptionsJson(JObject arguments)
        {
            JToken token = arguments?["analysis_options"];
            if (token == null)
            {
                return string.Empty;
            }
            if (token is not JObject options)
            {
                throw new InvalidOperationException("analysis_options must be an object.");
            }
            return options.ToString(Formatting.None);
        }

        private static Guid RequiredTimelineSessionId(JObject arguments)
        {
            return ParseTimelineSessionId(RequiredStringValue(arguments, "timeline_session_id"));
        }

        private static Guid ParseTimelineSessionId(string value)
        {
            if (!Guid.TryParse(value, out Guid id))
            {
                throw new InvalidOperationException("timeline_session_id is not a valid GUID.");
            }
            return id;
        }

        private static void ValidateTimelineSession(TimelineSessionRecord record, ResolvedCharacter character)
        {
            if (record == null || record.Director == null || record.Animator == null || record.Track == null ||
                record.TimelineAsset == null || record.Director.playableAsset is not TimelineAsset timelineAsset ||
                timelineAsset != record.TimelineAsset || record.Track.timelineAsset != timelineAsset)
            {
                throw new InvalidOperationException("Timeline Session target is no longer valid.");
            }
            if (record.Animator != character.Animator)
            {
                throw new InvalidOperationException("timeline_session_id is bound to a different character.");
            }
        }

        private static void PruneTimelineSessionsLocked()
        {
            foreach (Guid id in TimelineSessions
                .Where(pair => pair.Value.Director == null || pair.Value.Animator == null ||
                    pair.Value.TimelineAsset == null || pair.Value.Track == null)
                .Select(pair => pair.Key)
                .ToArray())
            {
                TimelineSessions.Remove(id);
            }
            while (TimelineSessions.Count >= MaxRememberedTimelineSessions)
            {
                TimelineSessions.Remove(TimelineSessions.OrderBy(pair => pair.Value.CreatedAtUtc).First().Key);
            }
        }

        private static TimelineAsset CreateTimelineSessionAsset(string characterName, out string assetPath)
        {
            KimodoEditorClipWritebackService.EnsureFolderExists(GeneratedTimelineFolder);
            string safeName = KimodoRuntimeUtility.SanitizeName(characterName, "Character");
            assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedTimelineFolder}/Kimodo_McpSession_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.playable");
            var timelineAsset = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timelineAsset, assetPath);
            return timelineAsset;
        }

        private static void OpenTimelineWindow(PlayableDirector director)
        {
            TimelineEditorWindow window = TimelineEditor.GetOrCreateWindow();
            window.SetTimeline(director);
            TimelineEditor.selectedClips = Array.Empty<TimelineClip>();
            window.Focus();
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static void CloseTimelineWindow(TimelineAsset timelineAsset)
        {
            if (timelineAsset == null || TimelineEditor.inspectedAsset != timelineAsset)
            {
                return;
            }

            TimelineEditor.selectedClips = Array.Empty<TimelineClip>();
            TimelineEditorWindow window = TimelineEditor.GetWindow();
            if (window != null)
            {
                window.ClearTimeline();
            }
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static bool HasRunningTimelineGeneration(Guid timelineSessionId)
        {
            lock (JobsLock)
            {
                return Jobs.Values.Any(record => record.Session.IsRunning &&
                    record.TimelineReservation != null &&
                    record.TimelineReservation.Session.Id == timelineSessionId);
            }
        }

        private sealed class TimelineSessionRecord
        {
            public TimelineSessionRecord(
                Guid id,
                PlayableDirector director,
                Animator animator,
                TimelineAsset timelineAsset,
                string timelineAssetPath,
                AnimationTrack track,
                PlayableAsset previousPlayableAsset,
                double previousTime,
                double nextStartSeconds)
            {
                Id = id;
                Director = director;
                Animator = animator;
                TimelineAsset = timelineAsset;
                TimelineAssetPath = timelineAssetPath;
                Track = track;
                PreviousPlayableAsset = previousPlayableAsset;
                PreviousTime = previousTime;
                NextStartSeconds = nextStartSeconds;
                CreatedAtUtc = DateTime.UtcNow;
            }

            public Guid Id { get; }
            public DateTime CreatedAtUtc { get; }
            public PlayableDirector Director { get; }
            public Animator Animator { get; }
            public TimelineAsset TimelineAsset { get; }
            public string TimelineAssetPath { get; }
            public AnimationTrack Track { get; }
            public PlayableAsset PreviousPlayableAsset { get; }
            public double PreviousTime { get; }
            public MarkerTrack AnalysisTrack { get; set; }
            public double NextStartSeconds { get; set; }
        }

        private sealed class TimelineReservation
        {
            public TimelineReservation(TimelineSessionRecord session, double startSeconds, double durationSeconds)
            {
                Session = session;
                StartSeconds = startSeconds;
                DurationSeconds = durationSeconds;
            }

            public TimelineSessionRecord Session { get; }
            public double StartSeconds { get; }
            public double DurationSeconds { get; }
            public TimelineClip TimelineClip { get; set; }
            public MarkerTrack AnalysisTrack { get; set; }
        }
    }
}
