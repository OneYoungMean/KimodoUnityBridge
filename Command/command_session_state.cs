using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace CharacterAnimationCli.Unity.Command
{
    internal static partial class command_context
    {
        private const string TimelineDirectorNamePrefix = "Kimodo_CommandSession_";
        internal const int ClipSafeZoneFrames = 4;
        internal const double ClipSafeZoneSeconds = ClipSafeZoneFrames / 60.0;
        private const string GeneratedTimelineFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Timelines";
        private static readonly Dictionary<string, TimelineSessionRecord> TimelineSessions =
            new Dictionary<string, TimelineSessionRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly object TimelineSessionsLock = new object();
        private static TimelineSessionRecord currentTimelineSession;

        public static string SessionGetOrCreate(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                EnsureTimelineSessionsRestored();
                EnsureCanManageServer();
                string sessionName = arguments.Value<string>("name")?.Trim();
                if (string.IsNullOrWhiteSpace(sessionName) && currentTimelineSession != null)
                {
                    return Ok(new JObject { ["created"] = false, ["session"] = DescribeSession(currentTimelineSession) });
                }
                if (!string.IsNullOrWhiteSpace(sessionName) && TryGetTimelineSession(sessionName, out TimelineSessionRecord existing))
                {
                    CloseCurrentTimelineSessionBeforeOpening(existing);
                    existing.AutoCloseWhenIdle = false;
                    currentTimelineSession = existing;
                    ActivateTimelineSession(existing);
                    PersistTimelineSessionMetadata(existing);
                    OpenTimelineWindow(existing.Director);
                    return Ok(new JObject { ["created"] = false, ["session"] = DescribeSession(existing) });
                }

                CloseCurrentTimelineSessionBeforeOpening(null);
                TimelineSessionRecord record = CreateTimelineSession(
                    string.IsNullOrWhiteSpace(sessionName)
                        ? $"Session_{DateTime.Now:yyyyMMdd_HHmmss_fff}"
                        : sessionName,
                    isAutomatic: false);
                lock (TimelineSessionsLock)
                {
                    TimelineSessions[record.Name] = record;
                }
                currentTimelineSession = record;
                ActivateTimelineSession(record);
                PersistTimelineSessionMetadata(record);
                OpenTimelineWindow(record.Director);
                return Ok(new JObject { ["created"] = true, ["session"] = DescribeSession(record) });
            });
        }

        public static string SessionClose(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                return CloseTimelineSession(session);
            });
        }

        private static string CloseTimelineSession(TimelineSessionRecord record)
        {
            if (record == null)
            {
                throw new InvalidOperationException("There is no current Timeline Session.");
            }
            CancelTimelineSessionGenerations(record, "Generation canceled: Session closed.");

            if (ReferenceEquals(currentTimelineSession, record)) currentTimelineSession = null;
            DeactivateTimelineSession(record);
            PersistTimelineSessionMetadata(record);
            CloseTimelineWindow(record.TimelineAsset);
            EditorUtility.SetDirty(record.TimelineAsset);
            if (record.Director != null)
            {
                EditorUtility.SetDirty(record.Director);
            }
            AssetDatabase.SaveAssets();
            return OkForSession(record, new JObject
            {
                ["closed"] = true,
                ["session"] = DescribeSession(record)
            });
        }

        private static void CloseCurrentTimelineSessionBeforeOpening(TimelineSessionRecord next)
        {
            TimelineSessionRecord current = currentTimelineSession;
            if (current == null || ReferenceEquals(current, next))
            {
                return;
            }
            CancelTimelineSessionGenerations(current, "Generation canceled: Session switched.");

            currentTimelineSession = null;
            DeactivateTimelineSession(current);
            PersistTimelineSessionMetadata(current);
            CloseTimelineWindow(current.TimelineAsset);
            EditorUtility.SetDirty(current.TimelineAsset);
            EditorUtility.SetDirty(current.Director);
            AssetDatabase.SaveAssets();
        }

        private static void ActivateTimelineSession(TimelineSessionRecord session)
        {
            foreach (PlayableDirector director in Resources.FindObjectsOfTypeAll<PlayableDirector>())
            {
                if (director == null || director == session.Director || director.gameObject == null ||
                    !director.gameObject.scene.IsValid() ||
                    !director.name.StartsWith(TimelineDirectorNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                director.Stop();
                director.enabled = false;
            }
            session.Director.enabled = true;
        }

        private static void DeactivateTimelineSession(TimelineSessionRecord session)
        {
            if (session?.Director == null)
            {
                return;
            }
            session.Director.Stop();
            session.Director.enabled = false;
        }

        private static TimelineSessionRecord CreateTimelineSession(string requestedName, bool isAutomatic)
        {
            string name = requestedName.Trim();
            if (name.Length == 0)
            {
                throw new InvalidOperationException("name cannot be empty.");
            }
            lock (TimelineSessionsLock)
            {
                if (TimelineSessions.ContainsKey(name))
                {
                    throw new InvalidOperationException($"A Timeline Session named '{name}' already exists.");
                }
            }

            KimodoEditorClipWritebackService.EnsureFolderExists(GeneratedTimelineFolder);
            string safeName = KimodoRuntimeUtility.SanitizeName(name, "Session");
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedTimelineFolder}/Kimodo_CommandSession_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.playable");
            TimelineAsset timelineAsset = ScriptableObject.CreateInstance<TimelineAsset>();
            timelineAsset.editorSettings.frameRate = SessionFrameRate;
            AssetDatabase.CreateAsset(timelineAsset, assetPath);
            var metadata = ScriptableObject.CreateInstance<KimodoCommandSessionMetadata>();
            metadata.name = "Kimodo Session Metadata";
            metadata.sessionId = Guid.NewGuid().ToString("D");
            metadata.sessionName = name;
            metadata.isAutomatic = isAutomatic;
            AssetDatabase.AddObjectToAsset(metadata, timelineAsset);

            GameObject directorObject = new GameObject($"Kimodo_CommandSession_{safeName}");
            directorObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timelineAsset;
            director.time = 0.0;

            var record = new TimelineSessionRecord(Guid.Parse(metadata.sessionId), name, director, timelineAsset, assetPath, isAutomatic, metadata);

            PersistTimelineSessionMetadata(record);
            EditorUtility.SetDirty(timelineAsset);
            EditorUtility.SetDirty(director);
            AssetDatabase.SaveAssets();
            return record;
        }

        private static IEnumerable<Animator> FindSceneAnimators()
        {
            return Resources.FindObjectsOfTypeAll<Animator>()
                .Where(animator => animator != null && !EditorUtility.IsPersistent(animator) &&
                    animator.gameObject != null && animator.gameObject.scene.IsValid())
                .GroupBy(animator => KimodoUnityObjectIdUtility.IdHash(animator))
                .Select(group => group.First())
                .ToArray();
        }

        private static string GetSceneHierarchyPath(GameObject gameObject)
        {
            return gameObject == null
                ? string.Empty
                : string.Join("/", gameObject.transform.GetComponentsInParent<Transform>(true)
                    .Reverse().Select(item => item.name));
        }

        private static bool AddCharacterTrack(
            TimelineSessionRecord session,
            GameObject root,
            Animator animator,
            bool tryGenerateAvatar,
            out string error,
            bool requireAvatar = false)
        {
            error = string.Empty;
            if (session == null || session.TimelineAsset == null || root == null || animator == null)
            {
                error = "Session, character root, and Animator are required.";
                return false;
            }
            if (session.Characters.Any(character => character.Animator == animator))
            {
                error = "Character is already in the current Session.";
                return false;
            }

            Avatar avatar = null;
            string avatarError = string.Empty;
            if (tryGenerateAvatar)
            {
                KimodoLocalAvatarUtility.AvatarResolveResult result =
                    KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                avatar = result.Avatar;
                avatarError = result.Error;
            }
            if (requireAvatar && !KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                error = string.IsNullOrWhiteSpace(avatarError)
                    ? "avatar_required: a valid humanoid Avatar is required."
                    : $"avatar_required: {avatarError}";
                return false;
            }

            string characterName = MakeUniqueCharacterName(session, root.name);
            AnimationTrack track = session.TimelineAsset.CreateTrack<AnimationTrack>(null, characterName);
            AnimationTrack poseCacheTrack = session.TimelineAsset.CreateTrack<AnimationTrack>(
                track,
                MakeUniqueSessionObjectName(session, $"{characterName}.Poses"));
            session.Director.SetGenericBinding(track, animator);
            var character = new TimelineCharacterRecord(
                GetObjectReference(root), root, animator, avatar, track, poseCacheTrack, avatarError);
            session.Characters.Add(character);
            EditorUtility.SetDirty(track);
            EditorUtility.SetDirty(poseCacheTrack);
            EditorUtility.SetDirty(session.TimelineAsset);
            return true;
        }

        private static string MakeUniqueCharacterName(TimelineSessionRecord session, string requestedName)
        {
            string baseName = KimodoRuntimeUtility.SanitizeName(requestedName, "Character");
            string name = baseName;
            for (int suffix = 1; session.Characters.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                name = $"{baseName}_{suffix}";
            }
            return name;
        }

        private static string MakeUniqueSessionObjectName(TimelineSessionRecord session, string requestedName)
        {
            var names = new HashSet<string>(
                session.TimelineAsset.GetRootTracks()
                    .SelectMany(root => new[] { root }.Concat(root.GetChildTracks()))
                    .Select(track => track.name),
                StringComparer.OrdinalIgnoreCase);
            string name = requestedName;
            for (int suffix = 1; names.Contains(name); suffix++)
            {
                name = $"{requestedName}_{suffix}";
            }
            return name;
        }

        private static TimelineAnimationRecord AppendAnimationClip(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            AnimationClip clip,
            string source,
            JObject analysis,
            string requestedName = null)
        {
            double duration = Math.Max(0.0001, clip != null ? clip.length : 0.0001);
            TimelineClip timelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            timelineClip.start = character.NextStartSeconds;
            timelineClip.duration = duration;
            string animationName = MakeUniqueAnimationName(character,
                string.IsNullOrWhiteSpace(requestedName) ? (clip != null ? clip.name : "Animation") : requestedName);
            timelineClip.displayName = animationName;
            ((AnimationPlayableAsset)timelineClip.asset).clip = clip;
            var animation = new TimelineAnimationRecord(
                Guid.NewGuid(), timelineClip.displayName, source, clip, timelineClip, analysis, null, 0, 0);
            character.Animations.Add(animation);
            character.NextStartSeconds = timelineClip.end + ClipSafeZoneSeconds;
            EditorUtility.SetDirty(character.Track);
            return animation;
        }

        private static TimelineClip AppendAnimationTimelineSegment(
            TimelineCharacterRecord character,
            AnimationClip clip,
            double startSeconds,
            double durationSeconds,
            double clipInSeconds,
            string displayName,
            bool loop)
        {
            if (character?.Track == null)
            {
                throw new InvalidOperationException("A character AnimationTrack is required.");
            }
            if (clip == null)
            {
                throw new InvalidOperationException("A source AnimationClip is required.");
            }

            TimelineClip timelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            timelineClip.start = Math.Max(0.0, startSeconds);
            timelineClip.clipIn = Math.Max(0.0, clipInSeconds);
            timelineClip.duration = Math.Max(1.0 / SessionFrameRate, durationSeconds);
            timelineClip.displayName = displayName ?? clip.name;
            AnimationPlayableAsset animationAsset = (AnimationPlayableAsset)timelineClip.asset;
            animationAsset.clip = clip;
            animationAsset.loop = loop
                ? AnimationPlayableAsset.LoopMode.On
                : AnimationPlayableAsset.LoopMode.UseSourceAsset;
            return timelineClip;
        }

        private static string MakeUniqueAnimationName(TimelineCharacterRecord character, string requestedName)
        {
            string baseName = KimodoRuntimeUtility.SanitizeName(requestedName, "Animation");
            string name = baseName;
            for (int suffix = 1; character.Animations.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                name = $"{baseName}_{suffix}";
            }
            return name;
        }

        private static TimelineGenerationTrace PrepareGenerationTrace(JObject arguments, ResolvedCharacter character, double duration)
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            TimelineCharacterRecord target = ResolveSessionCharacter(session, character.Root, character.Name)
                ?? throw new InvalidOperationException($"Character '{character.Name}' is not in the selected Session. Add it with session_add first.");
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(target.Avatar))
            {
                throw new InvalidOperationException($"Character '{target.Name}' requires a valid humanoid Avatar before generation.");
            }
            return new TimelineGenerationTrace(session, target, target.NextStartSeconds, duration);
        }

        private static KimodoPlayableClip CreateGenerationPlayableClip(
            TimelineGenerationTrace trace,
            string prompt)
        {
            if (trace?.Session == null || trace.Character == null || trace.Character.Track == null)
            {
                throw new InvalidOperationException("Timeline generation target is incomplete.");
            }

            TimelineAsset timelineAsset = trace.Session.TimelineAsset;
            if (timelineAsset == null || trace.Character.Track.timelineAsset != timelineAsset ||
                trace.Session.Director == null || trace.Character.Animator == null ||
                !BindingMatches(trace.Session.Director.GetGenericBinding(trace.Character.Track), trace.Character.Animator))
            {
                throw new InvalidOperationException("Timeline Session target is no longer valid.");
            }

            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { timelineAsset, trace.Character.Track, trace.Session.Director },
                "Kimodo Add Generation Clip");
            TimelineClip timelineClip = trace.Character.Track.CreateClip<KimodoPlayableClip>();
            timelineClip.start = trace.StartSeconds;
            timelineClip.duration = trace.DurationSeconds;
            timelineClip.displayName = MakeUniqueAnimationName(
                trace.Character,
                string.IsNullOrWhiteSpace(prompt) ? "Kimodo Generation" : prompt.Trim());

            KimodoPlayableClip playableClip = timelineClip.asset as KimodoPlayableClip;
            if (playableClip == null)
            {
                throw new InvalidOperationException("Timeline could not create a KimodoPlayableClip.");
            }
            playableClip.name = timelineClip.displayName;
            trace.TimelineClip = timelineClip;
            trace.PlayableClip = playableClip;
            trace.Animation = new TimelineAnimationRecord(
                Guid.NewGuid(),
                timelineClip.displayName,
                "generated",
                null,
                timelineClip,
                null,
                null,
                0,
                0);
            trace.Character.Animations.Add(trace.Animation);
            EditorUtility.SetDirty(playableClip);
            EditorUtility.SetDirty(trace.Character.Track);
            EditorUtility.SetDirty(timelineAsset);
            return playableClip;
        }

        private static void WriteGenerationConstraintMarkers(
            TimelineGenerationTrace trace,
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            float frameRate)
        {
            if (trace?.Character?.Track == null || samples == null || samples.Count == 0)
            {
                return;
            }

            List<KimodoMarkerSampleResult> unified =
                KimodoConstraintSampleComposer.ComposeCanonicalSamples(samples, frameRate);
            double lastSampleTime = Math.Max(0.0, trace.DurationSeconds - 1.0 / Math.Max(1f, frameRate));
            for (int i = 0; i < unified.Count; i++)
            {
                KimodoMarkerSampleResult sample = unified[i];
                if (sample == null)
                {
                    continue;
                }

                double localTime = Math.Max(0.0, Math.Min(lastSampleTime, sample.sampleTime));
                double markerTime = trace.StartSeconds + localTime;
                int markerFrame = Mathf.RoundToInt((float)(markerTime * frameRate));
                KimodoConstraintMarker marker = trace.Character.Track.GetMarkers()
                    .OfType<KimodoConstraintMarker>()
                    .FirstOrDefault(existing =>
                        Mathf.RoundToInt((float)(existing.time * frameRate)) == markerFrame);
                bool createdMarker = marker == null;
                if (marker != null)
                {
                    Debug.LogWarning($"[Kimodo] Constraint already exists at frame {markerFrame}; updating the existing marker.");
                    Selection.activeObject = marker;
                }
                else
                {
                    marker = trace.Character.Track.CreateMarker<KimodoConstraintMarker>(markerTime);
                }

                KimodoMarkerSampleResult markerSample = sample.Clone();
                markerSample.mask = KimodoConstraintMask.Resolve(markerSample.mask, sample.constraintType);
                markerSample.constraintType = "constraint";
                marker.SampleData = markerSample;
                if (createdMarker)
                {
                    marker.name = MakeUniqueConstraintPoseSource(trace.Session, $"{trace.Character.Name}.Constraint");
                }
                marker.autoSample = false;
                marker.constraintEnabled = true;
            }

            EditorUtility.SetDirty(trace.Character.Track);
        }

        private static string MakeUniqueConstraintPoseSource(TimelineSessionRecord session, string requestedName)
        {
            var names = new HashSet<string>(session.Characters
                .SelectMany(character => character.Track.GetMarkers().OfType<KimodoConstraintMarker>())
                .Select(marker => marker.name), StringComparer.OrdinalIgnoreCase);
            string name = requestedName;
            for (int suffix = 1; names.Contains(name); suffix++) name = $"{requestedName}_{suffix}";
            return name;
        }

        private static void EnsureConstraintPoseSources(TimelineSessionRecord session)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (TimelineCharacterRecord character in session.Characters)
            foreach (KimodoConstraintMarker marker in character.Track.GetMarkers().OfType<KimodoConstraintMarker>()
                .Where(item => item.constraintEnabled))
            {
                string prefix = $"{character.Name}.Constraint";
                if (!string.IsNullOrWhiteSpace(marker.name) &&
                    marker.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && used.Add(marker.name)) continue;
                marker.name = MakeUniqueConstraintPoseSource(session, prefix);
                used.Add(marker.name);
                EditorUtility.SetDirty(marker);
                changed = true;
            }
            if (changed) SaveTimelineSession(session);
        }

        private static void ReserveGenerationTimelineRange(TimelineGenerationTrace trace)
        {
            if (trace == null)
            {
                return;
            }

            lock (TimelineSessionsLock)
            {
                if (!TimelineSessions.ContainsKey(trace.Session.Name) ||
                    !ReferenceEquals(TimelineSessions[trace.Session.Name], trace.Session))
                {
                    throw new InvalidOperationException("Timeline Session was closed before generation could be started.");
                }
                trace.Character.NextStartSeconds = trace.StartSeconds + trace.DurationSeconds + ClipSafeZoneSeconds;
            }
        }

        private static void FinalizePlayableClipTrace(TimelineGenerationTrace trace, KimodoEditorGenerationResult result)
        {
            if (trace?.Session == null || trace.Character == null || trace.TimelineClip == null || trace.Animation == null)
            {
                throw new InvalidOperationException("Timeline generation trace is incomplete.");
            }

            TimelineAsset timelineAsset = trace.Session.TimelineAsset;
            JObject analysis = ParseAnalysisObject(result.AnalysisJson);
            trace.PlayableClip.clip = result.GeneratedClip;
            trace.Animation.ApplyResult(result.GeneratedClip, analysis, result.MotionBytes, result.StartFrame, result.EndFrameExclusive);

            JArray keyframes = analysis?["keyframes"] as JArray ?? new JArray();
            if (keyframes.Count > 0)
            {
                MarkerTrack analysisTrack = trace.Character.AnalysisTrack;
                if (analysisTrack == null || analysisTrack.timelineAsset != timelineAsset)
                {
                    analysisTrack = timelineAsset.CreateTrack<MarkerTrack>(null, $"Kimodo Analysis - {trace.Character.Name}");
                    trace.Character.AnalysisTrack = analysisTrack;
                }
                WriteAnalysisMarkers(analysisTrack, trace, keyframes);
                trace.AnalysisTrack = analysisTrack;
                EditorUtility.SetDirty(analysisTrack);
            }

            EditorUtility.SetDirty(trace.PlayableClip);
            EditorUtility.SetDirty(trace.Character.Track);
            EditorUtility.SetDirty(timelineAsset);
            EditorUtility.SetDirty(trace.Session.Director);
            AssetDatabase.SaveAssets();
        }

        private static JObject ParseAnalysisObject(string analysisJson)
        {
            try
            {
                return string.IsNullOrWhiteSpace(analysisJson) ? new JObject() : JObject.Parse(analysisJson);
            }
            catch
            {
                return new JObject { ["warnings"] = new JArray("Returned analysis metadata could not be parsed.") };
            }
        }

        private static void WriteAnalysisMarkers(MarkerTrack track, TimelineGenerationTrace trace, JArray keyframes)
        {
            foreach (JToken keyframe in keyframes)
            {
                double localTime = keyframe.Value<double?>("time") ?? 0.0;
                localTime = Math.Max(0.0, Math.Min(trace.DurationSeconds, localTime));
                KimodoAnalysisKeyframeMarker marker = track.CreateMarker<KimodoAnalysisKeyframeMarker>(trace.StartSeconds + localTime);
                marker.frame = keyframe.Value<int?>("frame") ?? 0;
                marker.saliency = keyframe.Value<float?>("saliency") ?? keyframe.Value<float?>("score") ?? 0f;
                marker.reasons = string.Join(", ", (keyframe["reasons"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
            }
        }

        private static string ParseAnalysisOptionsJson(JObject arguments)
        {
            JToken token = arguments?["analysis_option"];
            if (token == null)
            {
                return string.Empty;
            }
            if (token is not JObject options)
            {
                throw new InvalidOperationException("analysis_option must be an object.");
            }
            return options.ToString(Formatting.None);
        }

        private static TimelineSessionRecord RequireCurrentTimelineSession()
        {
            EnsureTimelineSessionsRestored();
            if (currentTimelineSession == null)
            {
                throw new CommandException("session_required", "No current Session. Call session_get_or_create first.");
            }
            if (currentTimelineSession.Director == null || currentTimelineSession.TimelineAsset == null)
            {
                throw new InvalidOperationException("Current Timeline Session is no longer valid.");
            }
            return currentTimelineSession;
        }

        private static TimelineSessionRecord RequireTimelineSession(JObject arguments)
        {
            EnsureTimelineSessionsRestored();
            string sessionId = arguments?.Value<string>("session_id")?.Trim();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return RequireCurrentTimelineSession();
            }
            if (!Guid.TryParse(sessionId, out Guid id))
            {
                throw new InvalidOperationException("session_id is not a valid GUID.");
            }
            TimelineSessionRecord requested;
            lock (TimelineSessionsLock)
            {
                requested = TimelineSessions.Values.FirstOrDefault(item => item.Id == id);
            }
            if (requested == null)
            {
                throw new InvalidOperationException($"Session '{sessionId}' was not found.");
            }
            if (!ReferenceEquals(requested, currentTimelineSession))
            {
                CloseCurrentTimelineSessionBeforeOpening(requested);
                currentTimelineSession = requested;
                ActivateTimelineSession(requested);
                PersistTimelineSessionMetadata(requested);
            }
            return requested;
        }

        private static string OkForSession(TimelineSessionRecord session, JObject result)
        {
            result ??= new JObject();
            result["session_id"] = session.Id.ToString("D");
            result["session_json_path"] = session.Metadata?.sessionJsonPath ?? string.Empty;
            result["session_revision"] = session.Metadata?.sessionRevision ?? 0;
            result["ok"] = true;
            return result.ToString(Formatting.None);
        }

        private static void CancelTimelineSessionGenerations(TimelineSessionRecord session, string reason)
        {
            if (session == null) return;
            Guid[] requests;
            lock (JobsLock)
            {
                requests = Jobs.Values
                    .Where(record => record.Session.IsRunning && record.TimelineGenerationTrace != null &&
                        ReferenceEquals(record.TimelineGenerationTrace.Session, session))
                    .Select(record => record.Session.RequestId)
                    .ToArray();
            }
            foreach (Guid requestId in requests)
            {
                KimodoEditorGenerationJobService.Cancel(requestId, reason);
            }
        }

        private static bool TryGetTimelineSession(string name, out TimelineSessionRecord record)
        {
            EnsureTimelineSessionsRestored();
            lock (TimelineSessionsLock)
            {
                return TimelineSessions.TryGetValue(name, out record);
            }
        }

        private static TimelineCharacterRecord ResolveSessionCharacter(
            TimelineSessionRecord session,
            GameObject root,
            string name)
        {
            if (session == null)
            {
                return null;
            }
            string reference = root != null ? GetObjectReference(root) : string.Empty;
            TimelineCharacterRecord match = !string.IsNullOrWhiteSpace(reference)
                ? session.Characters.FirstOrDefault(character => character.CharacterRef == reference)
                : session.Characters.FirstOrDefault(character =>
                    !string.IsNullOrWhiteSpace(name) &&
                    string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
            return match;
        }

        internal static TimelineCharacterRecord ResolveCurrentSessionCharacter(JObject arguments)
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            string name = RequiredStringValue(arguments, "character");
            TimelineCharacterRecord match = session.Characters.FirstOrDefault(character =>
                string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new InvalidOperationException("The character is not in the current Timeline Session.");
            }
            return match;
        }

        public static string SessionAdd(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                string kind = (arguments.Value<string>("kind") ?? string.Empty).Trim().ToLowerInvariant();
                if (kind == "character")
                {
                    string requestedName = RequiredStringValue(arguments, "character");
                    IEnumerable<Animator> candidates = FindSceneAnimators().Where(item =>
                        session.Characters.All(character => character.Animator != item));
                    bool isPath = requestedName.Contains("/");
                    Animator[] matches = candidates.Where(item => isPath
                        ? string.Equals(GetSceneHierarchyPath(item.gameObject), requestedName, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(item.gameObject.name, requestedName, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1)
                    {
                        throw new InvalidOperationException(matches.Length == 0
                            ? $"Scene character '{requestedName}' was not found."
                            : $"Scene character name '{requestedName}' is ambiguous; rename it before adding.");
                    }
                    Animator animator = matches[0];
                    GameObject root = animator.gameObject;
                    if (animator == null)
                    {
                        throw new InvalidOperationException("avatar_required: character has no Animator.");
                    }
                    if (session.Characters.Any(item => item.Animator == animator))
                    {
                        throw new InvalidOperationException("Character is already in the current Session.");
                    }
                    if (!AddCharacterTrack(session, root, animator, true, out string error, requireAvatar: true))
                    {
                        throw new InvalidOperationException(error);
                    }
                    TimelineCharacterRecord character = session.Characters.Last();
                    SaveTimelineSession(session);
                    return Ok(new JObject { ["added"] = true, ["kind"] = kind, ["character"] = DescribeCharacter(character) });
                }
                if (kind == "clip")
                {
                    TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                    AnimationClip clip = ResolveAnimationClip(RequiredStringValue(arguments, "clip"));
                    bool retargeted = false;
                    if (!clip.isHumanMotion)
                    {
                        clip = RetargetAddedClipToMuscle(character, clip);
                        retargeted = true;
                    }
                    TimelineAnimationRecord animation = AppendAnimationClip(session, character, clip, "added", null);
                    SaveTimelineSession(session);
                    return Ok(new JObject
                    {
                        ["added"] = true,
                        ["kind"] = kind,
                        ["retargeted"] = retargeted,
                        ["animation"] = DescribeAnimation(animation)
                    });
                }
                if (kind == "animator")
                {
                    TimelineCharacterRecord target = ResolveCurrentSessionCharacter(arguments);
                    string requested = RequiredStringValue(arguments, "animator");
                    bool isPath = requested.Contains("/");
                    Animator[] matches = FindSceneAnimators().Where(item => isPath
                        ? string.Equals(GetSceneHierarchyPath(item.gameObject), requested, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(item.gameObject.name, requested, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException(matches.Length == 0
                        ? $"Scene Animator '{requested}' was not found."
                        : $"Scene Animator '{requested}' is ambiguous; use its hierarchy path.");
                    return ImportAnimator(
                        session,
                        target,
                        matches[0],
                        arguments.Value<bool?>("ignore_warning") ?? false);
                }
                throw new InvalidOperationException("kind must be character, clip, or animator.");
            });
        }

        private static AnimationClip RetargetAddedClipToMuscle(TimelineCharacterRecord character, AnimationClip source)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException($"Cannot retarget '{source.name}': character '{character.Name}' has no valid humanoid Avatar.");
            }
            string assetName = $"{source.name}_{character.Name}_Retarget";
            AnimationClip output = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                assetName, KimodoEditorClipWritebackService.GeneratedClipFolder);
            if (KimodoRetargetToolsEditor.TryBakeMuscleClipToClip(source, character.Avatar, output, out string error))
            {
                AssetDatabase.SaveAssets();
                return output;
            }
            string path = AssetDatabase.GetAssetPath(output);
            if (!string.IsNullOrWhiteSpace(path)) AssetDatabase.DeleteAsset(path);
            throw new InvalidOperationException($"Retarget non-muscle clip '{source.name}' failed: {error}");
        }

        public static string AnimationCompare(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                AnimationRange origin = ResolveAnimationRange(arguments["origin"] as JObject, character, "origin");
                AnimationRange target = ResolveAnimationRange(arguments["target"] as JObject, character, "target");
                CharacterPose originPose = CaptureCharacterPose(character, origin.EndFrameExclusive - 1);
                CharacterPose targetPose = CaptureCharacterPose(character, target.StartFrame);

                Vector3 rootDelta = targetPose.root.t - originPose.root.t;
                float yawDelta = Mathf.DeltaAngle(originPose.root.q.eulerAngles.y, targetPose.root.q.eulerAngles.y);
                float poseDelta = 0f;
                for (int index = 0; index < CharacterPose.MuscleCount; index++)
                {
                    poseDelta += Mathf.Abs(targetPose.muscles[index] - originPose.muscles[index]);
                }
                poseDelta /= CharacterPose.MuscleCount;
                var endEffectorDeltas = new JObject();
                string recommended = "left_foot";
                float smallest = float.MaxValue;
                foreach (string endEffector in new[] { "left_hand", "right_hand", "left_foot", "right_foot" })
                {
                    CharacterPoseTransform originTransform = GetEndEffector(originPose, endEffector);
                    CharacterPoseTransform targetTransform = GetEndEffector(targetPose, endEffector);
                    Vector3 delta = (targetPose.root.t + targetTransform.t) - (originPose.root.t + originTransform.t);
                    float distance = delta.magnitude;
                    if (distance < smallest)
                    {
                        smallest = distance;
                        recommended = endEffector;
                    }
                    endEffectorDeltas[endEffector] = new JObject
                    {
                        ["position"] = new JArray(delta.x, delta.y, delta.z),
                        ["distance"] = distance
                    };
                }
                return Ok(new JObject
                {
                    ["direct_concat"] = new JObject
                    {
                        ["recommended"] = false,
                        ["reason"] = "Foot-contact comparison is not available in the current QuickServer analysis contract."
                    },
                    ["root_delta"] = new JObject
                    {
                        ["position"] = new JArray(rootDelta.x, rootDelta.y, rootDelta.z),
                        ["yaw_degrees"] = yawDelta
                    },
                    ["pose_delta"] = new JObject { ["mean_muscle_delta"] = poseDelta },
                    ["end_effector_delta"] = endEffectorDeltas,
                    ["contacts"] = new JObject
                    {
                        ["origin"] = new JObject(),
                        ["target"] = new JObject(),
                        ["compatible_support"] = false
                    },
                    ["trajectory_delta"] = new JObject { ["root_position"] = new JArray(rootDelta.x, rootDelta.y, rootDelta.z) },
                    ["recommended_contract"] = new JObject
                    {
                        ["endeffectors"] = new JArray(recommended),
                        ["mode"] = "align_target_root"
                    }
                });
            });
        }

        private static AnimationRange ResolveAnimationRange(JObject value, TimelineCharacterRecord character, string name)
        {
            if (value == null) throw new InvalidOperationException($"{name} must be an object.");
            TimelineAnimationRecord animation = ResolveAnimation(new JObject { ["animation"] = RequiredStringValue(value, "animation") }, character);
            JArray range = value["range"] as JArray;
            if (range == null || range.Count != 2 || range[0]?.Type != JTokenType.Integer || range[1]?.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException($"{name}.range must be [start_frame,end_frame_exclusive].");
            }
            int localStart = range[0].Value<int>();
            int localEnd = range[1].Value<int>();
            int duration = animation.TimelineClip != null
                ? Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)))
                : Math.Max(1, animation.EndFrameExclusive - animation.StartFrame);
            if (localStart < 0 || localEnd <= localStart || localEnd > duration)
            {
                throw new InvalidOperationException($"{name}.range must be within the animation's [0,{duration}) frame range.");
            }
            int clipStart = animation.TimelineClip != null
                ? Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate))
                : animation.StartFrame;
            return new AnimationRange(clipStart + localStart, clipStart + localEnd);
        }

        private readonly struct AnimationRange
        {
            public AnimationRange(int startFrame, int endFrameExclusive)
            {
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
            }
            public int StartFrame { get; }
            public int EndFrameExclusive { get; }
        }

        public static string AnimationAnalyze(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                JArray requestedClips = arguments["clips"] as JArray;
                if (requestedClips == null || requestedClips.Count < 1 || requestedClips.Count > 2)
                {
                    throw new InvalidOperationException("clips must contain one or two {character,clip,role?} objects.");
                }

                string level = NormalizeAnalysisPictureLevel(arguments.Value<string>("level"));
                int pictureResolution = ResolveAnalysisPictureResolution(arguments["resolution"]);
                JObject analysisOptions = BuildEffectiveAnalysisOptions(level);
                var subjects = new List<AnalysisSubject>(requestedClips.Count);
                var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < requestedClips.Count; index++)
                {
                    if (requestedClips[index] is not JObject requested)
                    {
                        throw new InvalidOperationException($"clips[{index}] must be an object.");
                    }

                    string role = (requested.Value<string>("role") ?? (index == 0 ? "source" : "target")).Trim().ToLowerInvariant();
                    if ((role != "source" && role != "target") || !roles.Add(role))
                    {
                        throw new InvalidOperationException("Each clips item requires a unique role of source or target.");
                    }

                    string characterName = RequiredStringValue(requested, "character");
                    TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                        string.Equals(item.Name, characterName, StringComparison.OrdinalIgnoreCase));
                    if (character == null)
                    {
                        throw new InvalidOperationException($"Character '{characterName}' is not in the current Timeline Session.");
                    }

                    string clipName = RequiredStringValue(requested, "clip");
                    TimelineAnimationRecord animation = ResolveAnimation(new JObject { ["animation"] = clipName }, character);
                    int startFrame = Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate));
                    int endFrame = startFrame + Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)));
                    ThrowIfGenerationRangeLocked(session, character, startFrame, endFrame, AnimationAnalyzeCommand);

                    string inputSignature = BuildAnimationAnalysisSignature(character, animation, analysisOptions);
                    AnalysisCacheRecord record;
                    if (!TryFindCachedAnimationAnalysis(session, character, animation, inputSignature, out record))
                    {
                        JObject analysis = AnalyzeAnimation(session, animation, analysisOptions, out byte[] analysisMotionBytes);
                        JArray poses = BuildAnalysisPoses(character, startFrame, endFrame, analysis, animation.Name, startFrame);
                        NormalizeAnalysisContract(analysis, poses);
                        string id = CacheAnalysisResult(
                            session, character, startFrame / SessionFrameRate, endFrame / SessionFrameRate,
                            poses, analysis, analysisMotionBytes, animation, inputSignature);
                        record = GetCachedAnalysis(session, id);
                    }
                    subjects.Add(new AnalysisSubject(role, character, animation, record, startFrame, endFrame));
                }

                JObject pictures = RenderAnalysisPictures(session, subjects, level, pictureResolution);
                SaveTimelineSession(session);
                return Ok(new JObject
                {
                    ["level"] = level,
                    ["clips"] = new JArray(subjects.Select(subject => new JObject
                    {
                        ["role"] = subject.Role,
                        ["character"] = subject.Character.Name,
                        ["clip"] = subject.Animation.Name,
                        ["keyframes"] = subject.Record.Analysis?["keyframes"]?.DeepClone() ?? new JArray(),
                        ["foot_contacts"] = subject.Record.Analysis?["foot_contacts"]?.DeepClone() ?? new JArray()
                    })),
                    ["pictures"] = pictures
                });
            });
        }

        private static string NormalizeAnalysisPictureLevel(string level)
        {
            string normalized = (level ?? "middle").Trim().ToLowerInvariant();
            if (normalized != "low" && normalized != "middle" && normalized != "high" && normalized != "-test")
            {
                throw new InvalidOperationException("level must be low, middle, high, or -test.");
            }
            return normalized;
        }

        private static int ResolveAnalysisPictureResolution(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return 512;
            if (value.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException("resolution must be a positive integer pixel size.");
            }
            int resolution = value.Value<int>();
            if (resolution < 64 || resolution > 4096)
            {
                throw new InvalidOperationException("resolution must be between 64 and 4096 pixels.");
            }
            return resolution;
        }

        private static JArray BuildAnalysisPoses(
            TimelineCharacterRecord character,
            int startFrame,
            int endFrame,
            JObject analysis,
            string source,
            int sourceStartFrame)
        {
            var poses = new JArray();
            foreach (JObject keyframe in (analysis?["keyframes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                // QuickServer analysis is always relative to the requested
                // segment.  Convert that local frame to the Session frame once;
                // do not infer absolute-vs-local from its numeric value.
                int reportedFrame = keyframe.Value<int?>("frame")
                    ?? Mathf.RoundToInt((float)((keyframe.Value<double?>("time") ?? 0.0) * SessionFrameRate));
                int frame = Mathf.Clamp(startFrame + reportedFrame, startFrame, endFrame - 1);
                JObject annotation = (JObject)keyframe.DeepClone();
                annotation.Remove("time");
                annotation.Remove("session_time");
                annotation["frame"] = frame;
                poses.Add(new JObject
                {
                    ["pose"] = new JObject { ["source"] = source, ["frame"] = frame - sourceStartFrame },
                    ["analysis"] = annotation
                });
            }
            return poses;
        }

        private static void NormalizeAnalysisContract(JObject analysis, JArray poses)
        {
            analysis ??= new JObject();
            var keyframes = new JArray();
            foreach (JObject item in (poses ?? new JArray()).OfType<JObject>())
            {
                JObject keyframe = item["analysis"] is JObject details ? (JObject)details.DeepClone() : new JObject();
                keyframe["frame"] = item["pose"]?.Value<int?>("frame") ?? 0;
                keyframes.Add(keyframe);
            }
            JArray contacts = analysis["foot_contacts"] as JArray
                ?? analysis["foot_contact_changes"] as JArray
                ?? new JArray();
            var normalizedContacts = new JArray();
            foreach (JObject contact in contacts.OfType<JObject>())
            {
                normalizedContacts.Add(new JObject
                {
                    ["clip_index"] = contact.Value<int?>("clip_index") ?? 0,
                    ["foot"] = contact.Value<string>("foot") ?? string.Empty,
                    ["frame"] = contact.Value<int?>("frame") ?? 0,
                    ["contact"] = contact.Value<bool?>("contact") ?? false,
                    ["duration_frames"] = contact.Value<int?>("duration_frames") ?? 0
                });
            }
            analysis.RemoveAll();
            analysis["keyframes"] = keyframes;
            analysis["foot_contacts"] = normalizedContacts;
            analysis["source"] = "quickserver_analysis_only";
        }

        private static JObject BuildEffectiveAnalysisOptions(string level)
        {
            const int keyframeCount = 8;
            return new JObject
            {
                ["keyframe_count"] = keyframeCount,
                ["keyframes"] = new JObject { ["enabled"] = true, ["max_count"] = keyframeCount }
            };
        }

        private static JObject AnalyzeAnimation(
            TimelineSessionRecord session,
            TimelineAnimationRecord animation,
            JObject analysisOptions,
            out byte[] analysisMotionBytes)
        {
            analysisMotionBytes = null;
            float frameRate = session.TimelineAsset.editorSettings.frameRate > 0.0
                ? (float)session.TimelineAsset.editorSettings.frameRate
                : KimodoMotionModelProfiles.DefaultFrameRate;
            byte[] motionBytes = animation.KmbBytes;
            int startFrame = Math.Max(0, animation.StartFrame);
            int frameCount = animation.EndFrameExclusive > animation.StartFrame
                ? animation.EndFrameExclusive - animation.StartFrame
                : Math.Max(1, Mathf.CeilToInt((float)(animation.TimelineDurationSeconds * frameRate)));
            bool requiresFootContactEncoding = motionBytes == null || motionBytes.Length == 0;
            if (!requiresFootContactEncoding &&
                KimodoRawMotionUtility.TryParseFlatBuffer(motionBytes, out KimodoRawMotionData existingMotion, out _) &&
                !existingMotion.HasFootContacts)
            {
                requiresFootContactEncoding = true;
            }
            if (requiresFootContactEncoding)
            {
                motionBytes = KimodoClipConstraintEncoder.EncodeTimeline(animation.TimelineClip, ResolveModelName(null), frameCount,
                    frameRate, 0, KimodoInOutConstraintMode.None, false, false, includeFootContacts: true);
                startFrame = 0;
            }
            if (KimodoRawMotionUtility.TryParseFlatBuffer(motionBytes, out KimodoRawMotionData motion, out _) && motion.FrameCount > 0)
            {
                startFrame = Mathf.Clamp(startFrame, 0, motion.FrameCount - 1);
                frameCount = Mathf.Clamp(frameCount, 1, motion.FrameCount - startFrame);
            }
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            var input = new KimodoEditorAnalysisInput
            {
                MotionBytes = motionBytes,
                StartFrame = startFrame,
                EndFrameExclusive = startFrame + frameCount,
                ModelName = ResolveModelName(null),
                TextEncoderMode = settings.DefaultTextEncoderMode,
                ModelsRoot = settings.LocalModelsPath?.Trim() ?? string.Empty,
                AnalysisOptionsJson = (analysisOptions ?? new JObject()).ToString(Formatting.None)
            };
            if (!KimodoPlayableClipGenerationExecutionService.Analysis(
                    input,
                    out string analysisJson,
                    out analysisMotionBytes,
                    out string error))
            {
                throw new InvalidOperationException(error);
            }
            JObject analysis = ParseAnalysisObject(analysisJson);
            analysis["source"] = "quickserver_analysis_only";
            return analysis;
        }

        private static TimelineAnimationRecord BakeTransientAnalysisRange(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            int startFrame,
            int endFrame,
            out AnimationClip transientClip,
            out TimelineClip transientTimelineClip)
        {
            int frameCount = endFrame - startFrame;
            Transform[] transforms = character.Root.GetComponentsInChildren<Transform>(true);
            string[] paths = transforms.Select(transform => AnimationUtility.CalculateTransformPath(transform, character.Root.transform)).ToArray();
            var frames = new List<BakeBoneFrame>(frameCount);
            double originalTime = session.Director.time;
            for (int frame = 0; frame < frameCount; frame++)
            {
                session.Director.time = (startFrame + frame) / SessionFrameRate;
                session.Director.Evaluate();
                var sample = new BakeBoneFrame(transforms.Length);
                for (int index = 0; index < transforms.Length; index++)
                {
                    sample.Positions[index] = transforms[index].localPosition;
                    sample.Rotations[index] = transforms[index].localRotation;
                }
                frames.Add(sample);
            }
            session.Director.time = originalTime;
            session.Director.Evaluate();

            transientClip = new AnimationClip { name = "__KimodoAnalysisRange__", frameRate = (float)SessionFrameRate };
            transientClip.hideFlags = HideFlags.HideAndDontSave;
            WriteBoneBakeCurves(transientClip, transforms, paths, frames, (float)SessionFrameRate);
            transientTimelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            transientTimelineClip.start = character.NextStartSeconds;
            transientTimelineClip.duration = frameCount / SessionFrameRate;
            transientTimelineClip.displayName = transientClip.name;
            ((AnimationPlayableAsset)transientTimelineClip.asset).clip = transientClip;
            return new TimelineAnimationRecord(Guid.NewGuid(), transientClip.name, "temporary", transientClip,
                transientTimelineClip, null, null, 0, frameCount);
        }

        public static string RecordRange(string argumentsJson)
        {
            return Execute(argumentsJson, arguments => RecordTimelineRange(arguments));
        }

        private static string RecordTimelineRange(JObject arguments)
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            TimelineCharacterRecord source = ResolveCurrentSessionCharacter(arguments);
            int startFrame = RequiredNonNegativeFrame(arguments, "start_frame");
            int endFrame = RequiredNonNegativeFrame(arguments, "end_frame");
            bool removeRootMotion = arguments.Value<bool?>("remove_root_motion") ?? false;
            double speed = arguments.Value<double?>("speed") ?? 1.0;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0.0)
            {
                throw new InvalidOperationException("speed must be a positive finite number.");
            }
            if (endFrame <= startFrame)
            {
                throw new InvalidOperationException("The record range must satisfy 0 <= start_frame < end_frame.");
            }
            ThrowIfGenerationRangeLocked(session, source, startFrame, endFrame, RecordRangeCommand);
            double start = startFrame / SessionFrameRate;
            double end = endFrame / SessionFrameRate;

            float frameRate = session.TimelineAsset.editorSettings.frameRate > 0f
                ? (float)session.TimelineAsset.editorSettings.frameRate
                : KimodoMotionModelProfiles.DefaultFrameRate;
            int frameCount = Math.Max(2, Mathf.CeilToInt((float)((end - start) / speed * frameRate)) + 1);
            var boneFrames = new List<RecordedBoneFrame>(frameCount);
            Transform[] transforms = source.Root.GetComponentsInChildren<Transform>(true);
            string[] paths = transforms.Select(transform => AnimationUtility.CalculateTransformPath(transform, source.Root.transform)).ToArray();
            AnimationClip output = null;
            try
            {
                using (var evaluation = KimodoTimelineEvaluationScope.Begin(session.Director))
                {
                    RuntimeAnimatorController savedController = source.Animator.runtimeAnimatorController;
                    source.Animator.runtimeAnimatorController = null;
                    try
                    {
                        for (int frame = 0; frame < frameCount; frame++)
                        {
                            double time = frame == frameCount - 1 ? end : start + (end - start) * frame / (frameCount - 1);
                            evaluation.EvaluateAt(time);
                            var frameData = new RecordedBoneFrame(transforms.Length);
                            for (int index = 0; index < transforms.Length; index++)
                            {
                                frameData.Positions[index] = transforms[index].localPosition;
                                frameData.Rotations[index] = transforms[index].localRotation;
                            }
                            boneFrames.Add(frameData);
                        }
                    }
                    finally
                    {
                        source.Animator.runtimeAnimatorController = savedController;
                    }
                }

                if (removeRootMotion)
                {
                    RemoveRecordedRootMotion(boneFrames);
                }

                string assetName = arguments.Value<string>("name")?.Trim();
                if (string.IsNullOrWhiteSpace(assetName))
                {
                    assetName = $"{source.Name}_Record_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
                }
                string folder = KimodoEditorOutputPathUtility.NormalizeOutputFolder(arguments.Value<string>("output_folder"));
                output = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(assetName, folder);
                output.frameRate = frameRate;
                WriteRecordedBoneCurves(output, transforms, paths, boneFrames, frameRate);

                TimelineAnimationRecord animation = AppendAnimationClip(session, source, output, "recorded", null);
                SaveTimelineSession(session);
                return Ok(new JObject
                {
                    ["recorded"] = true,
                    ["character"] = source.Name,
                    ["start_frame"] = startFrame,
                    ["end_frame"] = endFrame,
                    ["speed"] = speed,
                    ["remove_root_motion"] = removeRootMotion,
                    ["animation"] = DescribeAnimation(animation)
                });
            }
            catch
            {
                string outputPath = output != null ? AssetDatabase.GetAssetPath(output) : string.Empty;
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    AssetDatabase.DeleteAsset(outputPath);
                    AssetDatabase.SaveAssets();
                }
                throw;
            }
        }

        public static string RetargetAnimation(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                TimelineCharacterRecord source = ResolveSessionCharacterByReference(
                    session, RequiredStringValue(arguments, "source_character"), addIfMissing: false);
                TimelineAnimationRecord sourceAnimation = ResolveAnimation(arguments, source);
                TimelineCharacterRecord target = ResolveSessionCharacterByReference(
                    session,
                    RequiredStringValue(arguments, "target_character"),
                    addIfMissing: false);
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(source.Avatar) ||
                    !KimodoRetargetCoreUtility.IsValidHumanoid(target.Avatar))
                {
                    throw new InvalidOperationException("Retarget requires valid humanoid source and target Avatars.");
                }

                AnimationClip output = null;
                try
                {
                    string assetName = arguments.Value<string>("name")?.Trim();
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        assetName = $"{sourceAnimation.Name}_To_{target.Name}";
                    }
                    output = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                        assetName,
                        KimodoEditorOutputPathUtility.NormalizeOutputFolder(arguments.Value<string>("output_folder")));
                    KimodoEditorClipUtility.CopyClipData(sourceAnimation.Clip, output);
                    AnimationClip providedHumanoidClip = sourceAnimation.Clip.isHumanMotion
                        ? sourceAnimation.Clip
                        : null;
                    if (!KimodoRetargetCoreUtility.TryRetargetClip(
                            output,
                            source.Avatar,
                            target.Avatar,
                            exportMuscleClip: false,
                            providedSourceHumanoidClip: providedHumanoidClip,
                            out AnimationClip retargeted,
                            out string error,
                            debugLog: KimodoPlayableClipGenerationSettings.DebugLog))
                    {
                        throw new InvalidOperationException($"Retarget failed: {error}");
                    }
                    output = retargeted;
                    EditorUtility.SetDirty(output);
                    TimelineAnimationRecord animation = AppendAnimationClip(session, target, output, "retargeted", null);
                    SaveTimelineSession(session);
                    return Ok(new JObject
                    {
                        ["retargeted"] = true,
                        ["source_character"] = source.Name,
                        ["character"] = target.Name,
                        ["animation"] = DescribeAnimation(animation)
                    });
                }
                catch
                {
                    string outputPath = output != null ? AssetDatabase.GetAssetPath(output) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(outputPath))
                    {
                        AssetDatabase.DeleteAsset(outputPath);
                        AssetDatabase.SaveAssets();
                    }
                    throw;
                }
            });
        }

        private static void WriteBoneBakeCurves(
            AnimationClip clip,
            Transform[] transforms,
            string[] paths,
            List<BakeBoneFrame> frames,
            float frameRate)
        {
            for (int index = 0; index < transforms.Length; index++)
            {
                var px = new AnimationCurve();
                var py = new AnimationCurve();
                var pz = new AnimationCurve();
                var rx = new AnimationCurve();
                var ry = new AnimationCurve();
                var rz = new AnimationCurve();
                var rw = new AnimationCurve();
                for (int frame = 0; frame < frames.Count; frame++)
                {
                    float time = frame / frameRate;
                    Vector3 position = frames[frame].Positions[index];
                    Quaternion rotation = frames[frame].Rotations[index];
                    px.AddKey(time, position.x); py.AddKey(time, position.y); pz.AddKey(time, position.z);
                    rx.AddKey(time, rotation.x); ry.AddKey(time, rotation.y); rz.AddKey(time, rotation.z); rw.AddKey(time, rotation.w);
                }
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.x", px);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.y", py);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.z", pz);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.x", rx);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.y", ry);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.z", rz);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.w", rw);
            }
            clip.EnsureQuaternionContinuity();
        }

        private static void RemoveRecordedRootMotion(List<RecordedBoneFrame> boneFrames)
        {
            if (boneFrames.Count == 0 || boneFrames[0].Positions.Length == 0) return;
            Vector3 firstPosition = boneFrames[0].Positions[0];
            float firstYaw = boneFrames[0].Rotations[0].eulerAngles.y;
            for (int i = 0; i < boneFrames.Count; i++)
            {
                Vector3 position = boneFrames[i].Positions[0];
                boneFrames[i].Positions[0] = new Vector3(firstPosition.x, position.y, firstPosition.z);
                Vector3 euler = boneFrames[i].Rotations[0].eulerAngles;
                boneFrames[i].Rotations[0] = Quaternion.Euler(euler.x, firstYaw, euler.z);
            }
        }

        private static void WriteRecordedBoneCurves(AnimationClip clip, Transform[] transforms, string[] paths, List<RecordedBoneFrame> frames, float frameRate)
        {
            for (int index = 0; index < transforms.Length; index++)
            {
                var px = new AnimationCurve(); var py = new AnimationCurve(); var pz = new AnimationCurve();
                var rx = new AnimationCurve(); var ry = new AnimationCurve(); var rz = new AnimationCurve(); var rw = new AnimationCurve();
                for (int frame = 0; frame < frames.Count; frame++)
                {
                    float time = frame / frameRate;
                    Vector3 position = frames[frame].Positions[index];
                    Quaternion rotation = frames[frame].Rotations[index];
                    px.AddKey(time, position.x); py.AddKey(time, position.y); pz.AddKey(time, position.z);
                    rx.AddKey(time, rotation.x); ry.AddKey(time, rotation.y); rz.AddKey(time, rotation.z); rw.AddKey(time, rotation.w);
                }
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.x", px);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.y", py);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.z", pz);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.x", rx);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.y", ry);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.z", rz);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.w", rw);
            }
            clip.EnsureQuaternionContinuity();
        }

        private static TimelineAnimationRecord ResolveAnimation(JObject arguments, TimelineCharacterRecord character)
        {
            string name = RequiredStringValue(arguments, "animation");
            TimelineAnimationRecord animation = character.Animations.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (animation == null)
            {
                throw new InvalidOperationException($"Animation '{name}' is not loaded for character '{character.Name}'.");
            }
            return animation;
        }

        private static TimelineCharacterRecord ResolveSessionCharacterByReference(
            TimelineSessionRecord session,
            string reference,
            bool addIfMissing = false)
        {
            TimelineCharacterRecord match = session.Characters.FirstOrDefault(character =>
                character.CharacterRef == reference || string.Equals(character.Name, reference, StringComparison.OrdinalIgnoreCase));
            if (match == null && addIfMissing)
            {
                UnityEngine.Object resolved = ResolveObject(reference);
                GameObject root = resolved as GameObject ?? (resolved as Animator)?.gameObject;
                Animator animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
                string error = string.Empty;
                bool added = root != null && root.scene.IsValid() && !EditorUtility.IsPersistent(root) && animator != null &&
                    AddCharacterTrack(session, root, animator, true, out error, requireAvatar: true);
                if (added)
                {
                    match = session.Characters.FirstOrDefault(character => character.Animator == animator);
                }
                else if (root != null && animator != null)
                {
                    throw new InvalidOperationException($"Could not create a target AnimationTrack: {error}");
                }
            }
            if (match == null)
            {
                throw new InvalidOperationException($"Character '{reference}' is not in the selected Session.");
            }
            return match;
        }

        private static JObject DescribeSession(TimelineSessionRecord session)
        {
            return new JObject
            {
                ["session"] = session.Name,
                ["characters"] = new JArray(session.Characters.Select(DescribeCharacter)),
                ["current_frame"] = session.Director != null
                    ? Mathf.RoundToInt((float)(session.Director.time * SessionFrameRate))
                    : 0,
                ["current"] = ReferenceEquals(currentTimelineSession, session)
            };
        }

        private static JObject DescribeCharacter(TimelineCharacterRecord character)
        {
            return new JObject
            {
                ["name"] = character.Name,
                ["animations"] = new JArray(character.Animations.Select(DescribeAnimation))
            };
        }

        private static JObject DescribeAnimation(TimelineAnimationRecord animation)
        {
            var result = new JObject
            {
                ["name"] = animation.Name,
                ["source"] = animation.Source,
                ["kind"] = animation.Kind,
                ["start_frame"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate)) : 0,
                ["duration_frames"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)) : 0,
                ["segments"] = new JArray(animation.TimelineSegments.Select(segment => new JObject
                {
                    ["role"] = segment.Role,
                    ["start_frame"] = segment.TimelineClip != null ? Mathf.RoundToInt((float)(segment.TimelineClip.start * SessionFrameRate)) : 0,
                    ["duration_frames"] = segment.TimelineClip != null ? Mathf.RoundToInt((float)(segment.TimelineClip.duration * SessionFrameRate)) : 0
                }))
            };
            if (animation.Transition != null)
            {
                result["transition"] = animation.Transition.DeepClone();
            }
            return result;
        }

        private static JObject DescribeTimelineConstraint(KimodoConstraintMarker marker, int relativeToFrame)
        {
            int globalFrame = Mathf.RoundToInt((float)(marker.time * SessionFrameRate));
            var result = new JObject
            {
                ["frame"] = globalFrame - relativeToFrame,
                ["type"] = marker.ConstraintType
            };
            if (relativeToFrame != 0)
            {
                result["global_frame"] = globalFrame;
            }
            if (marker.ConstraintType == "constraint" &&
                !KimodoConstraintMask.Resolve(marker.SampleData.mask, marker.SampleData.constraintType).muscle &&
                !KimodoConstraintMask.Resolve(marker.SampleData.mask, marker.SampleData.constraintType).AnyEndEffector &&
                marker.SampleData.characterPose != null)
            {
                Vector3 forward = marker.SampleData.characterPose.root.q * Vector3.forward;
                result["position"] = new JArray(
                    marker.SampleData.characterPose.root.t.x,
                    marker.SampleData.characterPose.root.t.z);
                result["heading"] = new JArray(forward.x, forward.z);
            }
            else
            {
                result["pose"] = PoseLocatorJson(marker.name, globalFrame);
            }
            return result;
        }

        private static bool Overlaps(TimelineClip clip, double start, double end)
        {
            return clip != null && clip.end > start && clip.start < end;
        }

        private static double RequiredFiniteDouble(JObject arguments, string name)
        {
            if (!arguments.TryGetValue(name, out JToken token) ||
                (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
            {
                throw new InvalidOperationException($"{name} is required and must be a finite number.");
            }
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException($"{name} must be finite.");
            }
            return value;
        }

        private static void SaveTimelineSession(TimelineSessionRecord session)
        {
            PersistTimelineSessionMetadata(session);
            EditorUtility.SetDirty(session.TimelineAsset);
            AssetDatabase.SaveAssets();
            session.Director.RebuildGraph();
            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsAddedOrRemoved);
        }

        private static void OpenTimelineWindow(PlayableDirector director)
        {
            if (director == null || Application.isBatchMode)
            {
                return;
            }
            TimelineEditorWindow window = TimelineEditor.GetOrCreateWindow();
            window.SetTimeline(director);
            window.locked = true;
            TimelineEditor.selectedClips = Array.Empty<TimelineClip>();
            if (!KimodoTimelinePreviewRefreshUtility.TryEnablePreview())
            {
                Debug.LogWarning("[Kimodo][Command] Timeline preview could not be enabled automatically.");
            }
            window.Focus();
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static void CloseTimelineWindow(TimelineAsset timelineAsset)
        {
            if (Application.isBatchMode)
            {
                return;
            }
            TimelineEditor.selectedClips = Array.Empty<TimelineClip>();
            if (timelineAsset != null && TimelineEditor.inspectedAsset == timelineAsset)
            {
                TimelineEditorWindow window = TimelineEditor.GetWindow();
                if (window != null)
                {
                    window.ClearTimeline();
                }
            }
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static bool HasRunningTimelineGeneration(Guid timelineSessionId)
        {
            lock (JobsLock)
            {
                return Jobs.Values.Any(record => record.Session.IsRunning &&
                    record.TimelineGenerationTrace != null && record.TimelineGenerationTrace.Session.Id == timelineSessionId);
            }
        }

        internal static bool GenerationRangesOverlap(int firstStart, int firstEnd, int secondStart, int secondEnd) =>
            firstStart < secondEnd && secondStart < firstEnd;

        private static void ThrowIfGenerationRangeLocked(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            int startFrame,
            int endFrame,
            string command)
        {
            if (session == null || character?.Track == null || endFrame <= startFrame)
            {
                return;
            }

            lock (JobsLock)
            {
                foreach (JobRecord record in Jobs.Values)
                {
                    TimelineGenerationTrace trace = record.TimelineGenerationTrace;
                    if (!record.Session.IsRunning || trace == null ||
                        !ReferenceEquals(trace.Session, session) ||
                        !ReferenceEquals(trace.Character?.Track, character.Track))
                    {
                        continue;
                    }

                    int lockedStart = Mathf.RoundToInt((float)(trace.StartSeconds * SessionFrameRate));
                    int lockedEnd = lockedStart + Math.Max(1, Mathf.RoundToInt((float)(trace.DurationSeconds * SessionFrameRate)));
                    if (GenerationRangesOverlap(startFrame, endFrame, lockedStart, lockedEnd))
                    {
                        throw new GenerationRangeLockedException(
                            command,
                            record.Session.RequestId,
                            character.Name,
                            character.Track.name,
                            lockedStart,
                            lockedEnd,
                            startFrame,
                            endFrame);
                    }
                }
            }
        }

        private sealed class TimelineSessionRecord
        {
            public TimelineSessionRecord(
                Guid id,
                string name,
                PlayableDirector director,
                TimelineAsset timelineAsset,
                string timelineAssetPath,
                bool isAutomatic,
                KimodoCommandSessionMetadata metadata)
            {
                Id = id;
                Name = name;
                Director = director;
                TimelineAsset = timelineAsset;
                TimelineAssetPath = timelineAssetPath;
                IsAutomatic = isAutomatic;
                Metadata = metadata;
                CreatedAtUtc = DateTime.UtcNow;
            }

            public Guid Id { get; }
            public string Name { get; }
            public DateTime CreatedAtUtc { get; }
            public PlayableDirector Director { get; }
            public TimelineAsset TimelineAsset { get; }
            public string TimelineAssetPath { get; }
            public bool IsAutomatic { get; }
            public KimodoCommandSessionMetadata Metadata { get; }
            public bool AutoCloseWhenIdle { get; set; }
            public List<TimelineCharacterRecord> Characters { get; } = new List<TimelineCharacterRecord>();
        }

        internal sealed class TimelineCharacterRecord
        {
            public TimelineCharacterRecord(
                string characterRef,
                GameObject root,
                Animator animator,
                Avatar avatar,
                AnimationTrack track,
                AnimationTrack poseCacheTrack,
                string avatarError)
            {
                CharacterRef = characterRef;
                Root = root;
                Animator = animator;
                Avatar = avatar;
                Track = track;
                PoseCacheTrack = poseCacheTrack;
                AvatarError = avatarError ?? string.Empty;
            }

            public string CharacterRef { get; }
            public GameObject Root { get; }
            public Animator Animator { get; }
            public Avatar Avatar { get; set; }
            public AnimationTrack Track { get; }
            public AnimationTrack PoseCacheTrack { get; }
            public string AvatarError { get; set; }
            public MarkerTrack AnalysisTrack { get; set; }
            public double NextStartSeconds { get; set; }
            public List<TimelineAnimationRecord> Animations { get; } = new List<TimelineAnimationRecord>();
            public List<AnimatorImportRecord> AnimatorImports { get; } = new List<AnimatorImportRecord>();
            public string Name => Track != null ? Track.name : (Root != null ? Root.name : string.Empty);
        }

        internal sealed class TimelineAnimationRecord
        {
            public TimelineAnimationRecord(
                Guid id,
                string name,
                string source,
                AnimationClip clip,
                TimelineClip timelineClip,
                JObject analysis,
                byte[] kmbBytes,
                int startFrame,
                int endFrameExclusive)
            {
                Id = id;
                fallbackName = name ?? string.Empty;
                Source = source ?? string.Empty;
                Clip = clip;
                TimelineClip = timelineClip;
                Analysis = analysis;
                KmbBytes = kmbBytes;
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
                if (timelineClip != null)
                {
                    timelineSegments.Add(new TimelineAnimationSegment("clip", clip, timelineClip));
                }
            }

            public Guid Id { get; }
            private readonly string fallbackName;
            public string Name => fallbackName;
            public string Source { get; }
            public AnimationClip Clip { get; private set; }
            public TimelineClip TimelineClip { get; }
            public string Kind { get; private set; } = "animation_clip";
            public JObject Transition { get; private set; }
            public IReadOnlyList<TimelineAnimationSegment> TimelineSegments => timelineSegments;
            public JObject Analysis { get; private set; }
            public byte[] KmbBytes { get; private set; }
            public int StartFrame { get; private set; }
            public int EndFrameExclusive { get; private set; }
            public string AnimatorImportName { get; set; } = string.Empty;
            public string ImportKey { get; set; } = string.Empty;

            private readonly List<TimelineAnimationSegment> timelineSegments = new List<TimelineAnimationSegment>();

            public double TimelineStartSeconds => timelineSegments.Count > 0
                ? timelineSegments.Min(item => item.TimelineClip != null ? item.TimelineClip.start : double.MaxValue)
                : TimelineClip != null ? TimelineClip.start : 0.0;

            public double TimelineEndSeconds => timelineSegments.Count > 0
                ? timelineSegments.Max(item => item.TimelineClip != null ? item.TimelineClip.end : 0.0)
                : TimelineClip != null ? TimelineClip.end : 0.0;

            public double TimelineDurationSeconds => Math.Max(0.0, TimelineEndSeconds - TimelineStartSeconds);

            public void ConfigureComposite(
                string kind,
                IEnumerable<TimelineAnimationSegment> segments,
                JObject transition = null)
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "animation_clip" : kind;
                timelineSegments.Clear();
                if (segments != null)
                {
                    timelineSegments.AddRange(segments.Where(item => item?.TimelineClip != null));
                }
                if (timelineSegments.Count == 0 && TimelineClip != null)
                {
                    timelineSegments.Add(new TimelineAnimationSegment("clip", Clip, TimelineClip));
                }
                Transition = transition != null ? (JObject)transition.DeepClone() : null;
            }

            public void ApplyResult(
                AnimationClip clip,
                JObject analysis,
                byte[] kmbBytes,
                int startFrame,
                int endFrameExclusive)
            {
                Clip = clip;
                Analysis = analysis;
                KmbBytes = kmbBytes;
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
                if (timelineSegments.Count > 0)
                {
                    timelineSegments[0].Clip = clip;
                }
            }
        }

        internal sealed class TimelineAnimationSegment
        {
            public TimelineAnimationSegment(string role, AnimationClip clip, TimelineClip timelineClip)
            {
                Role = role ?? string.Empty;
                Clip = clip;
                TimelineClip = timelineClip;
            }

            public string Role { get; }
            public AnimationClip Clip { get; internal set; }
            public TimelineClip TimelineClip { get; }
        }

        internal sealed class AnimatorImportRecord
        {
            public AnimatorImportRecord(string sourceAnimatorRef, string name)
            {
                SourceAnimatorRef = sourceAnimatorRef ?? string.Empty;
                Name = name ?? string.Empty;
            }
            public string SourceAnimatorRef { get; }
            public string Name { get; }
        }

        private sealed class TimelineGenerationTrace
        {
            public TimelineGenerationTrace(TimelineSessionRecord session, TimelineCharacterRecord character, double startSeconds, double durationSeconds)
            {
                Session = session;
                Character = character;
                StartSeconds = startSeconds;
                DurationSeconds = durationSeconds;
            }

            public TimelineSessionRecord Session { get; }
            public TimelineCharacterRecord Character { get; }
            public double StartSeconds { get; }
            public double DurationSeconds { get; }
            public TimelineClip TimelineClip { get; set; }
            public KimodoPlayableClip PlayableClip { get; set; }
            public TimelineAnimationRecord Animation { get; set; }
            public MarkerTrack AnalysisTrack { get; set; }
        }

        private sealed class BakeBoneFrame
        {
            public BakeBoneFrame(int count)
            {
                Positions = new Vector3[count];
                Rotations = new Quaternion[count];
            }
            public Vector3[] Positions { get; }
            public Quaternion[] Rotations { get; }
        }

        private sealed class RecordedBoneFrame
        {
            public RecordedBoneFrame(int count)
            {
                Positions = new Vector3[count];
                Rotations = new Quaternion[count];
            }
            public Vector3[] Positions { get; }
            public Quaternion[] Rotations { get; }
        }
    }
}
