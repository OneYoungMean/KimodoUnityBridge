using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private const int MaxRememberedTimelineSessions = 64;
        private const string GeneratedTimelineFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Timelines";
        private static readonly Dictionary<string, TimelineSessionRecord> TimelineSessions =
            new Dictionary<string, TimelineSessionRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly object TimelineSessionsLock = new object();
        private static TimelineSessionRecord currentTimelineSession;

        public static string SessionOpenTimeline(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                RejectTimelineSessionId(arguments);
                EnsureCanManageServer();
                string sessionName = arguments.Value<string>("session_name")?.Trim();
                if (!string.IsNullOrWhiteSpace(sessionName) && TryGetTimelineSession(sessionName, out TimelineSessionRecord existing))
                {
                    currentTimelineSession = existing;
                    OpenTimelineWindow(existing.Director);
                    return Ok(DescribeSession(existing));
                }

                TimelineSessionRecord record = CreateTimelineSession(
                    string.IsNullOrWhiteSpace(sessionName)
                        ? $"Session_{DateTime.Now:yyyyMMdd_HHmmss_fff}"
                        : sessionName);
                lock (TimelineSessionsLock)
                {
                    PruneTimelineSessionsLocked();
                    TimelineSessions[record.Name] = record;
                }
                currentTimelineSession = record;
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
                ["timeline_asset_path"] = record.TimelineAssetPath,
                ["session_saved"] = true,
                ["session_retained"] = true,
                ["closed"] = true
            });
        }

        private static TimelineSessionRecord CreateTimelineSession(string requestedName)
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
            AssetDatabase.CreateAsset(timelineAsset, assetPath);

            GameObject directorObject = new GameObject($"Kimodo_CommandSession_{safeName}");
            directorObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timelineAsset;
            director.time = 0.0;

            var record = new TimelineSessionRecord(Guid.NewGuid(), name, director, timelineAsset, assetPath);
            foreach (Animator animator in FindSceneAnimators())
            {
                AddCharacterTrack(record, animator.gameObject, animator, tryGenerateAvatar: true, out _);
            }

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

            AnimationTrack track = session.TimelineAsset.CreateTrack<AnimationTrack>(null, $"Kimodo - {root.name}");
            session.Director.SetGenericBinding(track, animator);
            var character = new TimelineCharacterRecord(
                GetObjectReference(root), root, animator, avatar, track, avatarError);
            session.Characters.Add(character);
            FlattenAnimatorClips(session, character);
            EditorUtility.SetDirty(track);
            EditorUtility.SetDirty(session.TimelineAsset);
            return true;
        }

        private static void FlattenAnimatorClips(TimelineSessionRecord session, TimelineCharacterRecord character)
        {
            RuntimeAnimatorController controller = character.Animator != null
                ? character.Animator.runtimeAnimatorController
                : null;
            AnimationClip[] clips = controller != null ? controller.animationClips : Array.Empty<AnimationClip>();
            var seen = new HashSet<int>();
            for (int i = 0; i < clips.Length; i++)
            {
                AnimationClip clip = clips[i];
                if (clip == null || !seen.Add(KimodoUnityObjectIdUtility.IdHash(clip)))
                {
                    continue;
                }
                AppendAnimationClip(session, character, clip, "animator", null);
            }
        }

        private static TimelineAnimationRecord AppendAnimationClip(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            AnimationClip clip,
            string source,
            JObject analysis)
        {
            double duration = Math.Max(0.0001, clip != null ? clip.length : 0.0001);
            TimelineClip timelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            timelineClip.start = character.NextStartSeconds;
            timelineClip.duration = duration;
            timelineClip.displayName = clip != null ? clip.name : "Animation";
            ((AnimationPlayableAsset)timelineClip.asset).clip = clip;
            var animation = new TimelineAnimationRecord(
                Guid.NewGuid(), timelineClip.displayName, source, clip, timelineClip, analysis, null, 0, 0);
            character.Animations.Add(animation);
            character.NextStartSeconds += duration;
            EditorUtility.SetDirty(character.Track);
            return animation;
        }

        private static TimelineReservation PrepareTimelineReservation(JObject arguments, ResolvedCharacter character, double duration)
        {
            if (arguments?["timeline_session_id"] != null)
            {
                throw new InvalidOperationException("timeline_session_id is no longer accepted; all operations use the current Session.");
            }
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord target = ResolveSessionCharacter(session, character.Root, character.Name);
            if (target == null)
            {
                if (!AddCharacterTrack(session, character.Root, character.Animator, true, out string addError))
                {
                    throw new InvalidOperationException($"Character is not in the current Session and could not be added: {addError}");
                }
                target = ResolveSessionCharacter(session, character.Root, character.Name);
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(target.Avatar))
            {
                throw new InvalidOperationException($"Character '{target.Name}' requires a valid humanoid Avatar before generation.");
            }
            return new TimelineReservation(session, target, target.NextStartSeconds, duration);
        }

        private static void CommitTimelineReservation(TimelineReservation reservation)
        {
            if (reservation == null)
            {
                return;
            }
            lock (TimelineSessionsLock)
            {
                if (!TimelineSessions.ContainsKey(reservation.Session.Name) ||
                    !ReferenceEquals(TimelineSessions[reservation.Session.Name], reservation.Session))
                {
                    throw new InvalidOperationException("Timeline Session was closed before generation could be started.");
                }
                reservation.Character.NextStartSeconds = reservation.StartSeconds + reservation.DurationSeconds;
            }
        }

        private static async System.Threading.Tasks.Task<command_generate_result> ExecuteAssetGenerationAsync(
            KimodoEditorGenerateRequest request,
            UnityEngine.Object target,
            command_generation_session session,
            System.Threading.CancellationToken token,
            TimelineReservation reservation)
        {
            command_generate_result result = await ExecuteAssetGenerationAsync(request, target, session, token);
            if (reservation != null)
            {
                WriteGeneratedClipToTimeline(reservation, result);
            }
            return result;
        }

        private static void WriteGeneratedClipToTimeline(TimelineReservation reservation, command_generate_result result)
        {
            if (reservation?.Session == null || reservation.Character == null || result?.GeneratedClip == null)
            {
                throw new InvalidOperationException("Timeline Session writeback requires a generated AnimationClip.");
            }
            TimelineSessionRecord session = reservation.Session;
            TimelineCharacterRecord character = reservation.Character;
            TimelineAsset timelineAsset = session.TimelineAsset;
            if (session.Director == null || character.Animator == null || character.Track == null ||
                timelineAsset == null || character.Track.timelineAsset != timelineAsset ||
                !BindingMatches(session.Director.GetGenericBinding(character.Track), character.Animator))
            {
                throw new InvalidOperationException("Timeline Session target is no longer valid.");
            }

            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { timelineAsset, character.Track, session.Director },
                "Kimodo Command Save Generated Clip To Timeline");
            TimelineClip timelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            timelineClip.start = reservation.StartSeconds;
            timelineClip.duration = reservation.DurationSeconds;
            timelineClip.displayName = string.IsNullOrWhiteSpace(result.Prompt)
                ? result.GeneratedClip.name
                : result.Prompt;
            ((AnimationPlayableAsset)timelineClip.asset).clip = result.GeneratedClip;
            JObject analysis = ParseAnalysisObject(result.AnalysisJson);
            reservation.TimelineClip = timelineClip;
            reservation.Animation = new TimelineAnimationRecord(
                Guid.NewGuid(),
                timelineClip.displayName,
                "generated",
                result.GeneratedClip,
                timelineClip,
                analysis,
                result.MotionBytes,
                result.StartFrame,
                result.EndFrameExclusive);
            character.Animations.Add(reservation.Animation);

            JArray keyframes = analysis?["keyframes"] as JArray ?? new JArray();
            if (keyframes.Count > 0)
            {
                MarkerTrack analysisTrack = character.AnalysisTrack;
                if (analysisTrack == null || analysisTrack.timelineAsset != timelineAsset)
                {
                    analysisTrack = timelineAsset.CreateTrack<MarkerTrack>(null, $"Kimodo Analysis - {character.Name}");
                    character.AnalysisTrack = analysisTrack;
                }
                WriteAnalysisMarkers(analysisTrack, reservation, keyframes);
                reservation.AnalysisTrack = analysisTrack;
                EditorUtility.SetDirty(analysisTrack);
            }

            EditorUtility.SetDirty(character.Track);
            EditorUtility.SetDirty(timelineAsset);
            EditorUtility.SetDirty(session.Director);
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
            if (currentTimelineSession == null)
            {
                throw new InvalidOperationException("No current Timeline Session. Call session_open_timeline first.");
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
            TimelineCharacterRecord match = session.Characters.FirstOrDefault(character =>
                (!string.IsNullOrWhiteSpace(reference) && character.CharacterRef == reference) ||
                (!string.IsNullOrWhiteSpace(name) && string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase)));
            return match;
        }

        internal static TimelineCharacterRecord ResolveCurrentSessionCharacter(JObject arguments)
        {
            RejectTimelineSessionId(arguments);
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            string reference = arguments.Value<string>("character_ref")?.Trim();
            string name = arguments.Value<string>("character_name")?.Trim();
            if (string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("character_ref or character_name is required.");
            }
            TimelineCharacterRecord match = session.Characters.FirstOrDefault(character =>
                (!string.IsNullOrWhiteSpace(reference) && character.CharacterRef == reference) ||
                (!string.IsNullOrWhiteSpace(name) && string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase)));
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
                if (arguments["operation"] == null)
                {
                    return Ok(QueryCurrentSessionLs(session, arguments));
                }

                string operation = (arguments.Value<string>("operation") ?? "session").Trim().ToLowerInvariant();
                TimelineCharacterRecord character = null;
                if (operation == "character" || operation == "animations" || operation == "animation" || operation == "analysis")
                {
                    character = ResolveCurrentSessionCharacter(arguments);
                }
                JObject result = operation switch
                {
                    "session" => DescribeSession(session),
                    "characters" => new JObject { ["characters"] = new JArray(session.Characters.Select(DescribeCharacter)) },
                    "character" => DescribeCharacter(character),
                    "animations" => new JObject { ["character"] = character.Name, ["animations"] = new JArray(character.Animations.Select(DescribeAnimation)) },
                    "animation" => DescribeAnimation(ResolveAnimation(arguments, character)),
                    "analysis" => DescribeAnalysis(character, arguments),
                    _ => throw new InvalidOperationException("operation must be session, characters, character, animations, animation, or analysis.")
                };
                return Ok(result);
            });
        }

        private static JObject QueryCurrentSessionLs(TimelineSessionRecord session, JObject arguments)
        {
            string type = (arguments.Value<string>("type") ?? "session").Trim().ToLowerInvariant();
            if (type != "session" && type != "character" && type != "animation")
            {
                throw new InvalidOperationException("type must be session, character, or animation.");
            }

            string pattern = string.IsNullOrWhiteSpace(arguments.Value<string>("pattern"))
                ? "*"
                : arguments.Value<string>("pattern").Trim();
            JArray objects = arguments["objects"] as JArray;
            bool longResult = arguments.Value<bool?>("long") ?? true;
            bool showType = arguments.Value<bool?>("show_type") ?? false;
            List<JObject> matches = new List<JObject>();

            if (type == "session")
            {
                if (MatchesLsSelectors(session.Name, session.Id.ToString("D"), pattern, objects))
                {
                    matches.Add(longResult
                        ? DescribeSession(session)
                        : new JObject { ["name"] = session.Name });
                }
            }
            else if (type == "character")
            {
                foreach (TimelineCharacterRecord character in session.Characters)
                {
                    if (!MatchesLsSelectors(character.Name, character.CharacterRef, pattern, objects))
                    {
                        continue;
                    }
                    matches.Add(longResult
                        ? DescribeCharacter(character)
                        : new JObject
                        {
                            ["name"] = character.Name,
                            ["character_ref"] = character.CharacterRef
                        });
                }
            }
            else
            {
                IEnumerable<TimelineCharacterRecord> characters = session.Characters;
                string characterRef = arguments.Value<string>("character_ref")?.Trim();
                string characterName = arguments.Value<string>("character_name")?.Trim();
                if (!string.IsNullOrWhiteSpace(characterRef) || !string.IsNullOrWhiteSpace(characterName))
                {
                    characters = characters.Where(character =>
                        (!string.IsNullOrWhiteSpace(characterRef) && character.CharacterRef == characterRef) ||
                        (!string.IsNullOrWhiteSpace(characterName) && string.Equals(character.Name, characterName, StringComparison.OrdinalIgnoreCase)));
                }

                foreach (TimelineCharacterRecord character in characters)
                {
                    foreach (TimelineAnimationRecord animation in character.Animations)
                    {
                        string animationRef = animation.Id.ToString("D");
                        if (!MatchesLsSelectors(animation.Name, animationRef, pattern, objects))
                        {
                            continue;
                        }
                        JObject item = longResult
                            ? DescribeAnimation(animation)
                            : new JObject
                            {
                                ["name"] = animation.Name,
                                ["animation_id"] = animationRef
                            };
                        item["character"] = character.Name;
                        matches.Add(item);
                    }
                }
            }

            matches = ApplyLsLimits(matches, arguments);
            JArray resultObjects = new JArray(matches);
            if (showType)
            {
                foreach (JObject item in resultObjects.Children<JObject>())
                {
                    item["type"] = type;
                }
            }

            return new JObject
            {
                ["session_name"] = session.Name,
                ["type"] = type,
                ["pattern"] = pattern,
                ["count"] = matches.Count,
                ["objects"] = resultObjects
            };
        }

        private static bool MatchesLsSelectors(string name, string reference, string pattern, JArray objects)
        {
            if (objects == null || objects.Count == 0)
            {
                return MatchesLsPattern(name, pattern) || MatchesLsPattern(reference, pattern);
            }

            return objects.Values<string>().Any(selector =>
                MatchesLsPattern(name, selector) || MatchesLsPattern(reference, selector));
        }

        private static bool MatchesLsPattern(string value, string pattern)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(pattern))
            {
                return false;
            }
            string expression = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static List<JObject> ApplyLsLimits(List<JObject> matches, JObject arguments)
        {
            int? head = arguments.Value<int?>("head");
            int? tail = arguments.Value<int?>("tail");
            if (head.HasValue && head.Value < 0)
            {
                throw new InvalidOperationException("head must be non-negative.");
            }
            if (tail.HasValue && tail.Value < 0)
            {
                throw new InvalidOperationException("tail must be non-negative.");
            }

            List<JObject> limited = matches;
            if (head.HasValue)
            {
                limited = limited.Take(head.Value).ToList();
            }
            if (tail.HasValue)
            {
                limited = limited.Skip(Math.Max(0, limited.Count - tail.Value)).ToList();
            }
            return limited;
        }

        public static string SessionLocateAnimation(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                TimelineAnimationRecord animation = ResolveAnimation(arguments, character);
                double time = arguments.Value<double?>("session_global") ?? animation.TimelineClip.start;
                if (double.IsNaN(time) || double.IsInfinity(time) || time < 0.0)
                {
                    throw new InvalidOperationException("session_global must be a non-negative finite number.");
                }
                session.Director.time = time;
                session.Director.Evaluate();
                TimelineEditor.selectedClips = new[] { animation.TimelineClip };
                TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
                return Ok(new JObject
                {
                    ["session_name"] = session.Name,
                    ["character"] = character.Name,
                    ["animation"] = DescribeAnimation(animation),
                    ["session_global"] = time,
                    ["located"] = true
                });
            });
        }

        public static string SessionSamplePose(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                double time = RequiredFiniteDouble(arguments, "session_global");
                if (time < 0.0)
                {
                    throw new InvalidOperationException("session_global must be non-negative.");
                }
                session.Director.time = time;
                session.Director.Evaluate();
                JObject pose = CapturePose(character, time);
                Guid sampleId = Guid.NewGuid();
                session.PoseSamples[sampleId] = pose;
                pose["pose_sample_id"] = sampleId.ToString("D");
                return Ok(pose);
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
                    string reference = RequiredStringValue(arguments, "character_ref");
                    UnityEngine.Object resolved = ResolveObject(reference);
                    GameObject root = resolved as GameObject ?? (resolved as Animator)?.gameObject;
                    if (root == null || !root.scene.IsValid() || EditorUtility.IsPersistent(root))
                    {
                        throw new InvalidOperationException("character_ref must resolve to a scene character.");
                    }
                    Animator animator = root.GetComponentInChildren<Animator>(true);
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
                    AnimationClip clip = ResolveAnimationClip(RequiredStringValue(arguments, "clip_ref"));
                    TimelineAnimationRecord animation = AppendAnimationClip(session, character, clip, "added", null);
                    SaveTimelineSession(session);
                    return Ok(new JObject { ["added"] = true, ["kind"] = kind, ["animation"] = DescribeAnimation(animation) });
                }
                throw new InvalidOperationException("kind must be character or clip.");
            });
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
                    return Ok(new JObject { ["removed"] = true, ["kind"] = kind, ["animation_id"] = animation.Id.ToString("D") });
                }
                if (kind == "character")
                {
                    TimelineCharacterRecord character = ResolveCurrentSessionCharacter(arguments);
                    session.TimelineAsset.DeleteTrack(character.Track);
                    session.Characters.Remove(character);
                    SaveTimelineSession(session);
                    return Ok(new JObject { ["removed"] = true, ["kind"] = kind, ["character_ref"] = character.CharacterRef });
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
                double start = RequiredFiniteDouble(arguments, "start_global");
                double end = RequiredFiniteDouble(arguments, "end_global");
                if (start < 0.0 || end <= start)
                {
                    throw new InvalidOperationException("The analysis range must satisfy 0 <= start_global < end_global.");
                }

                TimelineAnimationRecord[] overlapping = character.Animations
                    .Where(item => Overlaps(item.TimelineClip, start, end))
                    .ToArray();
                var analyses = new JArray();
                foreach (TimelineAnimationRecord animation in overlapping)
                {
                    if (animation.Analysis != null && animation.Analysis.HasValues)
                    {
                        analyses.Add(new JObject
                        {
                            ["animation_id"] = animation.Id.ToString("D"),
                            ["global_start"] = animation.TimelineClip.start,
                            ["analysis"] = animation.Analysis.DeepClone()
                        });
                    }
                }
                JObject analysis = BuildRangeAnalysisFromServer(arguments, session, overlapping);
                if (analysis == null)
                {
                    analysis = new JObject
                    {
                        ["source"] = "session_generation_results",
                        ["keyframes"] = new JArray(analyses.SelectMany(item =>
                            (item["analysis"]?["keyframes"] as JArray ?? new JArray()).Select(frame => frame.DeepClone()))),
                        ["issues"] = new JArray(),
                        ["clips"] = new JArray(analyses.Select(item => item["animation_id"]))
                    };
                }
                JObject result = new JObject
                {
                    ["session_name"] = session.Name,
                    ["character"] = character.Name,
                    ["start_global"] = start,
                    ["end_global"] = end,
                    ["analyses"] = analyses,
                    ["analysis"] = analysis
                };
                return Ok(result);
            });
        }

        private static JObject BuildRangeAnalysisFromServer(
            JObject arguments,
            TimelineSessionRecord session,
            IEnumerable<TimelineAnimationRecord> animations)
        {
            float frameRate = session.TimelineAsset.editorSettings.frameRate > 0.0
                ? (float)session.TimelineAsset.editorSettings.frameRate
                : KimodoPlayableClip.FIXED_FRAME_RATE;
            var constraints = new List<KimodoKmbClipConstraint>();
            foreach (TimelineAnimationRecord animation in animations)
            {
                if (animation.KmbBytes == null || animation.KmbBytes.Length == 0)
                {
                    continue;
                }
                int startFrame = Math.Max(0, Mathf.RoundToInt((float)(animation.TimelineClip.start * frameRate)));
                int frameCount = animation.EndFrameExclusive > animation.StartFrame
                    ? animation.EndFrameExclusive - animation.StartFrame
                    : Math.Max(1, Mathf.CeilToInt((float)(animation.TimelineClip.duration * frameRate)));
                constraints.Add(new KimodoKmbClipConstraint
                {
                    motionBytes = animation.KmbBytes,
                    startFrame = startFrame,
                    endFrameExclusive = startFrame + frameCount
                });
            }
            if (constraints.Count == 0)
            {
                return null;
            }

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
            analysis["attachment_count"] = constraints.Count;
            return analysis;
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
            double start = RequiredFiniteDouble(arguments, "start_global");
            double end = RequiredFiniteDouble(arguments, "end_global");
            if (start < 0.0 || end <= start)
            {
                throw new InvalidOperationException("The bake range must satisfy 0 <= start_global < end_global.");
            }
            TimelineCharacterRecord target = source;
            string targetReference = arguments.Value<string>("retarget_character_ref")?.Trim();
            if (!string.IsNullOrWhiteSpace(targetReference))
            {
                target = ResolveSessionCharacterByReference(session, targetReference, addIfMissing: true);
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(target.Avatar))
                {
                    throw new InvalidOperationException($"Retarget target '{target.Name}' requires a valid humanoid Avatar.");
                }
            }

            float frameRate = session.TimelineAsset.editorSettings.frameRate > 0f
                ? (float)session.TimelineAsset.editorSettings.frameRate
                : KimodoPlayableClip.FIXED_FRAME_RATE;
            int frameCount = Math.Max(2, Mathf.CeilToInt((float)((end - start) * frameRate)) + 1);
            var poses = new List<MuscleSample>(frameCount);
            var boneFrames = new List<BakeBoneFrame>(frameCount);
            Transform[] transforms = source.Root.GetComponentsInChildren<Transform>(true);
            string[] paths = transforms.Select(transform => AnimationUtility.CalculateTransformPath(transform, source.Root.transform)).ToArray();
            AnimationClip output = null;
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

                string assetName = arguments.Value<string>("asset_name")?.Trim();
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
                    ["asset_ref"] = GetObjectReference(output),
                    ["asset_path"] = AssetDatabase.GetAssetPath(output),
                    ["character"] = target.Name,
                    ["source_character"] = source.Name,
                    ["start_global"] = start,
                    ["end_global"] = end,
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
            string idText = arguments.Value<string>("animation_id")?.Trim();
            string name = arguments.Value<string>("animation_name")?.Trim();
            TimelineAnimationRecord animation = null;
            if (!string.IsNullOrWhiteSpace(idText) && Guid.TryParse(idText, out Guid id))
            {
                animation = character.Animations.FirstOrDefault(item => item.Id == id);
            }
            if (animation == null && !string.IsNullOrWhiteSpace(name))
            {
                animation = character.Animations.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            if (animation == null)
            {
                throw new InvalidOperationException("animation_id or animation_name must identify an animation in the current Session.");
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
                ["session_name"] = session.Name,
                ["director_ref"] = GetObjectReference(session.Director),
                ["timeline_asset_ref"] = GetObjectReference(session.TimelineAsset),
                ["timeline_asset_path"] = session.TimelineAssetPath,
                ["characters"] = new JArray(session.Characters.Select(DescribeCharacter)),
                ["current_time"] = session.Director != null ? session.Director.time : 0.0,
                ["current"] = ReferenceEquals(currentTimelineSession, session)
            };
        }

        private static JObject DescribeCharacter(TimelineCharacterRecord character)
        {
            return new JObject
            {
                ["character_ref"] = character.CharacterRef,
                ["name"] = character.Name,
                ["animator_ref"] = GetObjectReference(character.Animator),
                ["avatar_ref"] = GetObjectReference(character.Avatar),
                ["avatar"] = KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar) ? "valid_humanoid" : "avatar_required",
                ["avatar_error"] = character.AvatarError ?? string.Empty,
                ["track_ref"] = GetObjectReference(character.Track),
                ["animation_count"] = character.Animations.Count,
                ["next_start_seconds"] = character.NextStartSeconds,
                ["animations"] = new JArray(character.Animations.Select(DescribeAnimation))
            };
        }

        private static JObject DescribeAnimation(TimelineAnimationRecord animation)
        {
            AnimationClip clip = animation.Clip;
            return new JObject
            {
                ["animation_id"] = animation.Id.ToString("D"),
                ["name"] = animation.Name,
                ["source"] = animation.Source,
                ["global_start"] = animation.TimelineClip != null ? animation.TimelineClip.start : 0.0,
                ["global_end"] = animation.TimelineClip != null ? animation.TimelineClip.end : 0.0,
                ["duration"] = animation.TimelineClip != null ? animation.TimelineClip.duration : 0.0,
                ["clip_in"] = animation.TimelineClip != null ? animation.TimelineClip.clipIn : 0.0,
                ["time_scale"] = animation.TimelineClip != null ? animation.TimelineClip.timeScale : 1.0,
                ["asset_ref"] = GetObjectReference(clip),
                ["asset_path"] = AssetDatabase.GetAssetPath(clip) ?? string.Empty,
                ["frame_rate"] = clip != null ? clip.frameRate : 0.0,
                ["frame_count"] = clip != null ? Mathf.CeilToInt(clip.length * Math.Max(1f, clip.frameRate)) : 0,
                ["is_human_motion"] = clip != null && clip.isHumanMotion,
                ["analysis"] = animation.Analysis?.DeepClone() ?? new JObject()
            };
        }

        private static JObject DescribeAnalysis(TimelineCharacterRecord character, JObject arguments)
        {
            if (arguments.Value<string>("animation_id") != null || arguments.Value<string>("animation_name") != null)
            {
                return new JObject
                {
                    ["animation"] = DescribeAnimation(ResolveAnimation(arguments, character)),
                    ["analysis"] = ResolveAnimation(arguments, character).Analysis?.DeepClone() ?? new JObject()
                };
            }
            return new JObject
            {
                ["character"] = character.Name,
                ["animations"] = new JArray(character.Animations
                    .Where(item => item.Analysis != null && item.Analysis.HasValues)
                    .Select(item => DescribeAnimation(item)))
            };
        }

        private static JObject CapturePose(TimelineCharacterRecord character, double time)
        {
            var pose = new HumanPose();
            using (var handler = new HumanPoseHandler(character.Avatar, character.Animator.transform))
            {
                handler.GetHumanPose(ref pose);
            }
            var bones = new JArray();
            foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone)
                {
                    continue;
                }
                Transform transform = character.Animator.GetBoneTransform(bone);
                if (transform == null)
                {
                    continue;
                }
                bones.Add(new JObject
                {
                    ["bone"] = bone.ToString(),
                    ["position"] = Vector3Json(transform.position),
                    ["rotation"] = QuaternionJson(transform.rotation)
                });
            }
            return new JObject
            {
                ["session_name"] = currentTimelineSession.Name,
                ["character"] = character.Name,
                ["session_global"] = time,
                ["root_position"] = Vector3Json(character.Root.transform.position),
                ["root_rotation"] = QuaternionJson(character.Root.transform.rotation),
                ["body_position"] = Vector3Json(pose.bodyPosition),
                ["body_rotation"] = QuaternionJson(pose.bodyRotation),
                ["muscles"] = new JArray(pose.muscles ?? Array.Empty<float>()),
                ["bones"] = bones
            };
        }

        private static JObject Vector3Json(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
        private static JObject QuaternionJson(Quaternion value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z, ["w"] = value.w };

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
            EditorUtility.SetDirty(session.TimelineAsset);
            AssetDatabase.SaveAssets();
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
                    record.TimelineReservation != null && record.TimelineReservation.Session.Id == timelineSessionId);
            }
        }

        private static void PruneTimelineSessionsLocked()
        {
            while (TimelineSessions.Count >= MaxRememberedTimelineSessions)
            {
                TimelineSessionRecord oldest = TimelineSessions.Values.OrderBy(record => record.CreatedAtUtc).FirstOrDefault();
                if (oldest == null)
                {
                    return;
                }
                TimelineSessions.Remove(oldest.Name);
            }
        }

        private sealed class TimelineSessionRecord
        {
            public TimelineSessionRecord(Guid id, string name, PlayableDirector director, TimelineAsset timelineAsset, string timelineAssetPath)
            {
                Id = id;
                Name = name;
                Director = director;
                TimelineAsset = timelineAsset;
                TimelineAssetPath = timelineAssetPath;
                CreatedAtUtc = DateTime.UtcNow;
            }

            public Guid Id { get; }
            public string Name { get; }
            public DateTime CreatedAtUtc { get; }
            public PlayableDirector Director { get; }
            public TimelineAsset TimelineAsset { get; }
            public string TimelineAssetPath { get; }
            public List<TimelineCharacterRecord> Characters { get; } = new List<TimelineCharacterRecord>();
            public Dictionary<Guid, JObject> PoseSamples { get; } = new Dictionary<Guid, JObject>();
        }

        internal sealed class TimelineCharacterRecord
        {
            public TimelineCharacterRecord(string characterRef, GameObject root, Animator animator, Avatar avatar, AnimationTrack track, string avatarError)
            {
                CharacterRef = characterRef;
                Root = root;
                Animator = animator;
                Avatar = avatar;
                Track = track;
                AvatarError = avatarError ?? string.Empty;
            }

            public string CharacterRef { get; }
            public GameObject Root { get; }
            public Animator Animator { get; }
            public Avatar Avatar { get; set; }
            public AnimationTrack Track { get; }
            public string AvatarError { get; set; }
            public MarkerTrack AnalysisTrack { get; set; }
            public double NextStartSeconds { get; set; }
            public List<TimelineAnimationRecord> Animations { get; } = new List<TimelineAnimationRecord>();
            public string Name => Root != null ? Root.name : string.Empty;
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
                Name = name ?? string.Empty;
                Source = source ?? string.Empty;
                Clip = clip;
                TimelineClip = timelineClip;
                Analysis = analysis;
                KmbBytes = kmbBytes;
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
            }

            public Guid Id { get; }
            public string Name { get; }
            public string Source { get; }
            public AnimationClip Clip { get; }
            public TimelineClip TimelineClip { get; }
            public JObject Analysis { get; }
            public byte[] KmbBytes { get; }
            public int StartFrame { get; }
            public int EndFrameExclusive { get; }
        }

        private sealed class TimelineReservation
        {
            public TimelineReservation(TimelineSessionRecord session, TimelineCharacterRecord character, double startSeconds, double durationSeconds)
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
