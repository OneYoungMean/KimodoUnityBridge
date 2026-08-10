using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private const string AutomaticTimelineSessionName = "__KimodoAuto__";
        private const string TimelineDirectorNamePrefix = "Kimodo_CommandSession_";
        private const string GeneratedTimelineFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Timelines";
        private static readonly Dictionary<string, TimelineSessionRecord> TimelineSessions =
            new Dictionary<string, TimelineSessionRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly object TimelineSessionsLock = new object();
        private static TimelineSessionRecord currentTimelineSession;

        public static string SessionOpenTimeline(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                EnsureTimelineSessionsRestored();
                RejectTimelineSessionId(arguments);
                EnsureCanManageServer();
                string sessionName = arguments.Value<string>("session_name")?.Trim();
                if (!string.IsNullOrWhiteSpace(sessionName) && TryGetTimelineSession(sessionName, out TimelineSessionRecord existing))
                {
                    CloseCurrentTimelineSessionBeforeOpening(existing);
                    existing.AutoCloseWhenIdle = false;
                    currentTimelineSession = existing;
                    ActivateTimelineSession(existing);
                    PersistTimelineSessionMetadata(existing);
                    OpenTimelineWindow(existing.Director);
                    return Ok(DescribeSession(existing));
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
                return Ok(DescribeSession(record));
            });
        }

        public static string SessionCloseTimeline(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                RejectTimelineSessionId(arguments);
                return CloseCurrentTimelineSession();
            });
        }

        private static string CloseCurrentTimelineSession()
        {
            TimelineSessionRecord record = currentTimelineSession;
            if (record == null)
            {
                throw new InvalidOperationException("There is no current Timeline Session.");
            }
            if (HasRunningTimelineGeneration(record.Id))
            {
                throw new InvalidOperationException("Timeline Session still has a running generation. Cancel or wait for it before closing.");
            }

            currentTimelineSession = null;
            DeactivateTimelineSession(record);
            PersistTimelineSessionMetadata(record);
            CloseTimelineWindow(record.TimelineAsset);
            EditorUtility.SetDirty(record.TimelineAsset);
            if (record.Director != null)
            {
                EditorUtility.SetDirty(record.Director);
            }
            AssetDatabase.SaveAssets();
            return Ok(new JObject
            {
                ["session_name"] = record.Name,
                ["session_saved"] = true,
                ["session_retained"] = true,
                ["closed"] = true
            });
        }

        private static void CloseCurrentTimelineSessionBeforeOpening(TimelineSessionRecord next)
        {
            TimelineSessionRecord current = currentTimelineSession;
            if (current == null || ReferenceEquals(current, next))
            {
                return;
            }
            if (HasRunningTimelineGeneration(current.Id))
            {
                throw new InvalidOperationException("Current Timeline Session still has a running generation. Cancel or wait for it before opening another Session.");
            }

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
                throw new InvalidOperationException("session_name cannot be empty.");
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
            foreach (Animator animator in FindSceneAnimators())
            {
                AddCharacterTrack(record, animator.gameObject, animator, tryGenerateAvatar: true, out _, requireAvatar: true);
            }

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
            FlattenAnimatorClips(session, character);
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

        private static void FlattenAnimatorClips(TimelineSessionRecord session, TimelineCharacterRecord character)
        {
            if (character.Animator?.runtimeAnimatorController is UnityEditor.Animations.AnimatorController)
                ImportAnimator(session, character, character.Animator);
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
            character.NextStartSeconds += duration;
            EditorUtility.SetDirty(character.Track);
            return animation;
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
            if (arguments?["timeline_session_id"] != null)
            {
                throw new InvalidOperationException("timeline_session_id is no longer accepted; all operations use the current Session.");
            }
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord target = ResolveSessionCharacter(session, character.Root, character.Name);
            if (target == null)
            {
                if (!AddCharacterTrack(session, character.Root, character.Animator, true, out string addError, requireAvatar: true))
                {
                    throw new InvalidOperationException($"Character is not in the current Session and could not be added: {addError}");
                }
                target = ResolveSessionCharacter(session, character.Root, character.Name);
            }
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

            double lastSampleTime = Math.Max(0.0, trace.DurationSeconds - 1.0 / Math.Max(1f, frameRate));
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null)
                {
                    continue;
                }

                double localTime = Math.Max(0.0, Math.Min(lastSampleTime, sample.sampleTime));
                KimodoConstraintMarkerBase marker;
                switch ((sample.constraintType ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "root2d":
                        marker = trace.Character.Track.CreateMarker<KimodoRoot2DConstraintMarker>(trace.StartSeconds + localTime);
                        break;
                    case "fullbody":
                        marker = trace.Character.Track.CreateMarker<KimodoFullBodyConstraintMarker>(trace.StartSeconds + localTime);
                        break;
                    case "left_hand":
                        marker = trace.Character.Track.CreateMarker<KimodoLeftHandConstraintMarker>(trace.StartSeconds + localTime);
                        break;
                    case "right_hand":
                        marker = trace.Character.Track.CreateMarker<KimodoRightHandConstraintMarker>(trace.StartSeconds + localTime);
                        break;
                    case "left_foot":
                        marker = trace.Character.Track.CreateMarker<KimodoLeftFootConstraintMarker>(trace.StartSeconds + localTime);
                        break;
                    case "right_foot":
                        marker = trace.Character.Track.CreateMarker<KimodoRightFootConstraintMarker>(trace.StartSeconds + localTime);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported Timeline constraint type '{sample.constraintType}'.");
                }

                KimodoMarkerSampleResult markerSample = sample.Clone();
                markerSample.sampleTime = trace.StartSeconds + localTime;
                marker.SampleData = markerSample;
                marker.name = MakeUniqueConstraintPoseSource(trace.Session, $"{trace.Character.Name}.Constraint");
                marker.useOverride = true;
                marker.constraintEnabled = true;
            }

            EditorUtility.SetDirty(trace.Character.Track);
        }

        private static string MakeUniqueConstraintPoseSource(TimelineSessionRecord session, string requestedName)
        {
            var names = new HashSet<string>(session.Characters
                .SelectMany(character => character.Track.GetMarkers().OfType<KimodoConstraintMarkerBase>())
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
            foreach (KimodoConstraintMarkerBase marker in character.Track.GetMarkers().OfType<KimodoConstraintMarkerBase>()
                .Where(item => item is not KimodoUntypedConstraintMarker))
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
                trace.Character.NextStartSeconds = trace.StartSeconds + trace.DurationSeconds;
            }
        }

        private static void FinalizePlayableClipTrace(TimelineGenerationTrace trace, command_generate_result result)
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
                throw new InvalidOperationException("No current Session. Call session_open first.");
            }
            if (currentTimelineSession.Director == null || currentTimelineSession.TimelineAsset == null)
            {
                throw new InvalidOperationException("Current Timeline Session is no longer valid.");
            }
            return currentTimelineSession;
        }

        private static void RejectTimelineSessionId(JObject arguments)
        {
            if (arguments?["timeline_session_id"] != null)
            {
                throw new InvalidOperationException("timeline_session_id is no longer accepted; all operations use the current Session.");
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
            RejectTimelineSessionId(arguments);
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            string name = RequiredStringValue(arguments, "character");
            TimelineCharacterRecord match = session.Characters.FirstOrDefault(character =>
                string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new InvalidOperationException("The character is not in the current Timeline Session.");
            }
            return match;
        }

        public static string QueryCurrentSession(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                RejectTimelineSessionId(arguments);
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                string query = RequiredStringValue(arguments, "query").ToLowerInvariant();
                if (query == "characters")
                {
                    return Ok(new JObject { ["characters"] = new JArray(session.Characters.Select(item => item.Name)) });
                }
                TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                if (query == "character_animations")
                {
                    return Ok(new JObject { ["character"] = character.Name, ["animations"] = new JArray(character.Animations.Select(DescribeAnimation)) });
                }
                if (query == "character_constraints")
                {
                    EnsureConstraintPoseSources(session);
                    return Ok(new JObject
                    {
                        ["character"] = character.Name,
                        ["constraints"] = new JArray(character.Track.GetMarkers().OfType<KimodoConstraintMarkerBase>()
                            .Where(marker => marker is not KimodoUntypedConstraintMarker)
                            .Select(marker => DescribeTimelineConstraint(marker, 0)))
                    });
                }
                TimelineAnimationRecord animation = ResolveAnimation(arguments, character);
                if (query == "animation_transitions")
                {
                    return Ok(new JObject
                    {
                        ["character"] = character.Name,
                        ["animation"] = animation.Name,
                        ["incoming"] = new JArray(character.Animations.Where(item =>
                            string.Equals(item.ToAnimation, animation.Name, StringComparison.OrdinalIgnoreCase)).Select(DescribeTransition)),
                        ["outgoing"] = new JArray(character.Animations.Where(item =>
                            string.Equals(item.FromAnimation, animation.Name, StringComparison.OrdinalIgnoreCase)).Select(DescribeTransition))
                    });
                }
                if (query == "transition")
                {
                    if (!string.Equals(animation.Source, "animator_transition", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Animation '{animation.Name}' is not an Animator transition.");
                    return Ok(new JObject { ["character"] = character.Name, ["transition"] = DescribeTransition(animation) });
                }
                if (query == "animation")
                {
                    return Ok(new JObject { ["character"] = character.Name, ["animation"] = DescribeAnimation(animation) });
                }
                if (query == "animation_constraints")
                {
                    EnsureConstraintPoseSources(session);
                    int startFrame = Mathf.RoundToInt((float)(animation.TimelineClip.start * SessionFrameRate));
                    int endFrame = Mathf.RoundToInt((float)(animation.TimelineClip.end * SessionFrameRate));
                    return Ok(new JObject
                    {
                        ["character"] = character.Name,
                        ["animation"] = animation.Name,
                        ["constraints"] = new JArray(character.Track.GetMarkers().OfType<KimodoConstraintMarkerBase>()
                            .Where(marker => marker is not KimodoUntypedConstraintMarker)
                            .Where(marker =>
                            {
                                int frame = Mathf.RoundToInt((float)(marker.time * SessionFrameRate));
                                return frame >= startFrame && frame < endFrame;
                            })
                            .Select(marker => DescribeTimelineConstraint(marker, startFrame)))
                    });
                }
                throw new InvalidOperationException("query must be characters, character_animations, animation, character_constraints, animation_constraints, animation_transitions, or transition.");
            });
        }

        public static string SessionTryAdd(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
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
                    return Ok(ImportAnimator(session, target, matches[0]));
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

        public static string SessionTryRemove(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                string kind = (arguments.Value<string>("kind") ?? string.Empty).Trim().ToLowerInvariant();
                if (kind == "clip")
                {
                    TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                    TimelineAnimationRecord animation = ResolveAnimation(arguments, character);
                    character.Track.DeleteClip(animation.TimelineClip);
                    character.Animations.Remove(animation);
                    SaveTimelineSession(session);
                    return Ok(new JObject { ["removed"] = true, ["kind"] = kind, ["character"] = character.Name, ["animation"] = animation.Name });
                }
                if (kind == "character")
                {
                    TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                    session.TimelineAsset.DeleteTrack(character.Track);
                    session.Characters.Remove(character);
                    SaveTimelineSession(session);
                    return Ok(new JObject { ["removed"] = true, ["kind"] = kind, ["character"] = character.Name });
                }
                throw new InvalidOperationException("kind must be character or clip.");
            });
        }

        public static string KimodoAnalyzeTimelineRange(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                RejectTimelineSessionId(arguments);
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                bool animationMode = arguments["animation"] != null;
                bool rangeMode = arguments["start_frame"] != null || arguments["end_frame"] != null;
                if (animationMode == rangeMode)
                {
                    throw new InvalidOperationException("Provide exactly one analysis source: animation, or start_frame with end_frame.");
                }
                int startFrame;
                int endFrame;
                TimelineAnimationRecord animation;
                AnimationClip transientClip = null;
                TimelineClip transientTimelineClip = null;
                try
                {
                    if (animationMode)
                    {
                        animation = ResolveAnimation(arguments, character);
                        startFrame = Mathf.RoundToInt((float)(animation.TimelineClip.start * SessionFrameRate));
                        endFrame = startFrame + Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineClip.duration * SessionFrameRate)));
                    }
                    else
                    {
                        startFrame = RequiredNonNegativeFrame(arguments, "start_frame");
                        endFrame = RequiredNonNegativeFrame(arguments, "end_frame");
                        if (endFrame <= startFrame)
                            throw new InvalidOperationException("The analysis range must satisfy 0 <= start_frame < end_frame.");
                        animation = BakeTransientAnalysisRange(session, character, startFrame, endFrame, out transientClip, out transientTimelineClip);
                    }

                    JObject analysis = AnalyzeClipWithServer(arguments, session, animation);
                    JArray poses = BuildAnalysisPoses(character, startFrame, endFrame, analysis);
                    analysis.Remove("keyframes");
                    string analysisId = CacheAnalysisResult(session, character, startFrame / SessionFrameRate, endFrame / SessionFrameRate, poses, analysis);
                    var response = new JObject
                    {
                        ["analysis_id"] = analysisId,
                        ["character"] = character.Name,
                        ["poses"] = poses,
                        ["analysis"] = analysis
                    };
                    if (animationMode) response["animation"] = animation.Name;
                    else
                    {
                        response["start_frame"] = startFrame;
                        response["end_frame"] = endFrame;
                    }
                    return Ok(response);
                }
                finally
                {
                    if (transientTimelineClip != null) character.Track.DeleteClip(transientTimelineClip);
                    if (transientClip != null) UnityEngine.Object.DestroyImmediate(transientClip);
                    session.Director.Evaluate();
                    TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
                }
            });
        }

        private static JArray BuildAnalysisPoses(
            TimelineCharacterRecord character,
            int startFrame,
            int endFrame,
            JObject analysis)
        {
            var poses = new JArray();
            foreach (JObject keyframe in (analysis?["keyframes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                double reported = keyframe.Value<double?>("session_time") ?? keyframe.Value<double?>("time") ?? 0.0;
                int reportedFrame = Mathf.RoundToInt((float)(reported * SessionFrameRate));
                int frame = reportedFrame >= startFrame && reportedFrame < endFrame
                    ? reportedFrame
                    : Mathf.Clamp(startFrame + reportedFrame, startFrame, endFrame - 1);
                JObject annotation = (JObject)keyframe.DeepClone();
                annotation.Remove("time");
                annotation.Remove("session_time");
                annotation["frame"] = frame;
                poses.Add(new JObject
                {
                    ["pose"] = new JObject { ["source"] = character.Name, ["frame"] = frame },
                    ["analysis"] = annotation
                });
            }
            return poses;
        }

        private static JObject AnalyzeClipWithServer(
            JObject arguments,
            TimelineSessionRecord session,
            TimelineAnimationRecord animation)
        {
            float frameRate = session.TimelineAsset.editorSettings.frameRate > 0.0
                ? (float)session.TimelineAsset.editorSettings.frameRate
                : KimodoPlayableClip.FIXED_FRAME_RATE;
            byte[] motionBytes = animation.KmbBytes;
            int startFrame = Math.Max(0, animation.StartFrame);
            int frameCount = animation.EndFrameExclusive > animation.StartFrame
                ? animation.EndFrameExclusive - animation.StartFrame
                : Math.Max(1, Mathf.CeilToInt((float)(animation.TimelineClip.duration * frameRate)));
            if (motionBytes == null || motionBytes.Length == 0)
            {
                motionBytes = KimodoClipConstraintEncoder.EncodeTimeline(animation.TimelineClip, ResolveModelName(null), frameCount,
                    frameRate, 0, KimodoInOutConstraintMode.None, false, false);
                startFrame = 0;
            }
            if (KimodoRawMotionUtility.TryParseFlatBuffer(motionBytes, out KimodoRawMotionData motion, out _) && motion.FrameCount > 0)
            {
                startFrame = Mathf.Clamp(startFrame, 0, motion.FrameCount - 1);
                frameCount = Mathf.Clamp(frameCount, 1, motion.FrameCount - startFrame);
            }
            var constraints = new List<KimodoKmbClipConstraint>
            {
                new KimodoKmbClipConstraint { motionBytes = motionBytes, startFrame = startFrame, endFrameExclusive = startFrame + frameCount }
            };

            JObject options = arguments["analysis_option"] is JObject supplied
                ? (JObject)supplied.DeepClone()
                : new JObject();
            options["analysis_only"] = true;
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            KimodoBridgeGenerationResult result = KimodoBridgeService.Shared.GenerateAsync(
                new KimodoGenerationRequestDto
                {
                    prompt = string.Empty,
                    model = ResolveModelName(null),
                    text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(settings.DefaultTextEncoderMode),
                    models_root = settings.LocalModelsPath?.Trim() ?? string.Empty,
                    output_format = "kmb_attachments_v1",
                    analysis_option_json = options.ToString(Formatting.None),
                    analysis_clip_constraints = constraints
                },
                System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            JObject analysis = ParseAnalysisObject(result?.AnalysisJson);
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

        public static string KimodoBakeTimelineRange(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                RejectTimelineSessionId(arguments);
                return BakeTimelineRange(arguments);
            });
        }

        private static string BakeTimelineRange(JObject arguments)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
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
                throw new InvalidOperationException("The bake range must satisfy 0 <= start_frame < end_frame.");
            }
            double start = startFrame / SessionFrameRate;
            double end = endFrame / SessionFrameRate;
            TimelineCharacterRecord target = source;
            string targetReference = arguments.Value<string>("retarget_character")?.Trim();
            if (!string.IsNullOrWhiteSpace(targetReference))
            {
                target = ResolveSessionCharacterByReference(session, targetReference, addIfMissing: false);
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(target.Avatar))
                {
                    throw new InvalidOperationException($"Retarget target '{target.Name}' requires a valid humanoid Avatar.");
                }
            }

            float frameRate = session.TimelineAsset.editorSettings.frameRate > 0f
                ? (float)session.TimelineAsset.editorSettings.frameRate
                : KimodoPlayableClip.FIXED_FRAME_RATE;
            int frameCount = Math.Max(2, Mathf.CeilToInt((float)((end - start) / speed * frameRate)) + 1);
            var poses = new List<MuscleSample>(frameCount);
            var boneFrames = new List<BakeBoneFrame>(frameCount);
            Transform[] transforms = source.Root.GetComponentsInChildren<Transform>(true);
            string[] paths = transforms.Select(transform => AnimationUtility.CalculateTransformPath(transform, source.Root.transform)).ToArray();
            AnimationClip output = null;
            try
            {
                RuntimeAnimatorController savedController = source.Animator.runtimeAnimatorController;
                source.Animator.runtimeAnimatorController = null;
                try
                {
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        double time = frame == frameCount - 1 ? end : start + (end - start) * frame / (frameCount - 1);
                        session.Director.time = time;
                        session.Director.Evaluate();
                        poses.Add(CaptureMuscleSample(source));
                        var frameData = new BakeBoneFrame(transforms.Length);
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

                if (removeRootMotion)
                {
                    RemoveBakeRootMotion(poses, boneFrames);
                }

                string assetName = arguments.Value<string>("name")?.Trim();
                if (string.IsNullOrWhiteSpace(assetName))
                {
                    assetName = $"{source.Name}_Bake_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
                }
                string folder = NormalizeOutputFolder(arguments.Value<string>("output_folder"));
                output = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(assetName, folder);
                output.frameRate = frameRate;
                if (target != source)
                {
                    if (!KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(poses, output, out string error))
                    {
                        throw new InvalidOperationException($"Bake retarget failed: {error}");
                    }
                    KimodoEditorClipUtility.ApplyMuscleClipSettings(output);
                }
                else
                {
                    WriteBoneBakeCurves(output, transforms, paths, boneFrames, frameRate);
                }

                TimelineAnimationRecord animation = AppendAnimationClip(session, target, output, "baked", null);
                SaveTimelineSession(session);
                return Ok(new JObject
                {
                    ["baked"] = true,
                    ["character"] = target.Name,
                    ["source_character"] = source.Name,
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

        private static void RemoveBakeRootMotion(List<MuscleSample> poses, List<BakeBoneFrame> boneFrames)
        {
            if (poses.Count > 0)
            {
                Vector3 firstPosition = poses[0].pose.bodyPosition;
                float firstYaw = poses[0].pose.bodyRotation.eulerAngles.y;
                for (int i = 0; i < poses.Count; i++)
                {
                    HumanPose pose = poses[i].pose;
                    pose.bodyPosition = new Vector3(firstPosition.x, pose.bodyPosition.y, firstPosition.z);
                    Vector3 euler = pose.bodyRotation.eulerAngles;
                    pose.bodyRotation = Quaternion.Euler(euler.x, firstYaw, euler.z);
                    poses[i].pose = pose;
                }
            }
            if (boneFrames.Count > 0 && boneFrames[0].Positions.Length > 0)
            {
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
        }

        private static MuscleSample CaptureMuscleSample(TimelineCharacterRecord character)
        {
            var pose = new HumanPose();
            using (var handler = new HumanPoseHandler(character.Avatar, character.Animator.transform))
            {
                handler.GetHumanPose(ref pose);
            }
            return new MuscleSample { pose = pose };
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
                throw new InvalidOperationException($"Character '{reference}' is not in the current Timeline Session and could not be added.");
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
                ["current"] = ReferenceEquals(currentTimelineSession, session),
                ["automatic"] = session.IsAutomatic
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
            return new JObject
            {
                ["name"] = animation.Name,
                ["source"] = animation.Source,
                ["start_frame"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineClip.start * SessionFrameRate)) : 0,
                ["duration_frames"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineClip.duration * SessionFrameRate)) : 0
            };
        }

        private static JObject DescribeTransition(TimelineAnimationRecord animation)
        {
            JObject result = DescribeAnimation(animation);
            result["from_animation"] = animation.FromAnimation;
            result["to_animation"] = animation.ToAnimation;
            return result;
        }

        private static JObject DescribeTimelineConstraint(KimodoConstraintMarkerBase marker, int relativeToFrame)
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
            if (marker.ConstraintType == "root2d" &&
                (marker.SampleData.muscles == null || marker.SampleData.muscles.Count == 0))
            {
                result["position"] = new JArray(marker.SampleData.kimodoRootPosition.x, marker.SampleData.kimodoRootPosition.z);
                result["heading"] = new JArray(marker.SampleData.rootHeading.x, marker.SampleData.rootHeading.y);
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
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static void OpenTimelineWindow(PlayableDirector director)
        {
            if (director == null)
            {
                return;
            }
            TimelineEditorWindow window = TimelineEditor.GetOrCreateWindow();
            window.SetTimeline(director);
            window.locked = true;
            TimelineEditor.selectedClips = Array.Empty<TimelineClip>();
            TryEnableTimelinePreview(window);
            window.Focus();
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static void TryEnableTimelinePreview(TimelineEditorWindow window)
        {
            try
            {
                PropertyInfo property = typeof(TimelineEditorWindow).GetProperty("previewMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                {
                    property.SetValue(window, true, null);
                    return;
                }
                FieldInfo field = typeof(TimelineEditorWindow).GetField("m_PreviewMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(window, true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][Command] Timeline preview could not be enabled automatically: {ex.Message}");
            }
        }

        private static void CloseTimelineWindow(TimelineAsset timelineAsset)
        {
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

        private static void FinishAutomaticTimelineSession(
            TimelineGenerationTrace trace,
            Guid requestId)
        {
            if (trace?.Session == null || !trace.Session.AutoCloseWhenIdle)
            {
                return;
            }

            lock (JobsLock)
            {
                if (Jobs.Values.Any(record => record.Session.IsRunning &&
                    record.Session.RequestId != requestId &&
                    record.TimelineGenerationTrace != null &&
                    ReferenceEquals(record.TimelineGenerationTrace.Session, trace.Session)))
                {
                    return;
                }
            }

            if (ReferenceEquals(currentTimelineSession, trace.Session))
            {
                currentTimelineSession = null;
            }
            trace.Session.AutoCloseWhenIdle = false;
            DeactivateTimelineSession(trace.Session);
            PersistTimelineSessionMetadata(trace.Session);
            CloseTimelineWindow(trace.Session.TimelineAsset);
            EditorUtility.SetDirty(trace.Session.TimelineAsset);
            if (trace.Session.Director != null)
            {
                EditorUtility.SetDirty(trace.Session.Director);
            }
            AssetDatabase.SaveAssets();
        }

        private static TimelineSessionRecord EnsureGenerationTimelineSession()
        {
            if (currentTimelineSession != null)
            {
                return RequireCurrentTimelineSession();
            }

            if (!TryGetTimelineSession(AutomaticTimelineSessionName, out TimelineSessionRecord automatic))
            {
                automatic = CreateTimelineSession(AutomaticTimelineSessionName, isAutomatic: true);
                lock (TimelineSessionsLock)
                {
                    TimelineSessions[automatic.Name] = automatic;
                }
            }

            currentTimelineSession = automatic;
            automatic.AutoCloseWhenIdle = true;
            ActivateTimelineSession(automatic);
            PersistTimelineSessionMetadata(automatic);
            return RequireCurrentTimelineSession();
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
            }

            public Guid Id { get; }
            private readonly string fallbackName;
            public string Name => TimelineClip != null ? TimelineClip.displayName : fallbackName;
            public string Source { get; }
            public AnimationClip Clip { get; private set; }
            public TimelineClip TimelineClip { get; }
            public JObject Analysis { get; private set; }
            public byte[] KmbBytes { get; private set; }
            public int StartFrame { get; private set; }
            public int EndFrameExclusive { get; private set; }
            public string AnimatorImportName { get; set; } = string.Empty;
            public string ImportKey { get; set; } = string.Empty;
            public string FromAnimation { get; set; } = string.Empty;
            public string ToAnimation { get; set; } = string.Empty;

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
            }
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
    }
}
