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
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal sealed class KimodoCommandSessionMetadata : ScriptableObject
    {
        public string schemaVersion = "vNext";
        public string sessionId;
        public string sessionName;
        public int sessionRevision;
        public string sessionJsonPath;
        public bool isAutomatic;
        public bool isCurrent;
        public string updatedAtUtc;
        public List<KimodoCommandCharacterMetadata> characters = new List<KimodoCommandCharacterMetadata>();
        public List<KimodoCommandAnimationMetadata> animations = new List<KimodoCommandAnimationMetadata>();
        public List<KimodoCommandGenerationMetadata> generations = new List<KimodoCommandGenerationMetadata>();
        public List<KimodoCommandAnimatorImportMetadata> animatorImports = new List<KimodoCommandAnimatorImportMetadata>();
    }

    [Serializable]
    internal sealed class KimodoCommandCharacterMetadata
    {
        public string characterRef;
        public string trackName;
        public string poseCacheTrackName;
    }

    [Serializable]
    internal sealed class KimodoCommandAnimationMetadata
    {
        public string animationId;
        public string name;
        public string characterRef;
        public string timelineClipAssetRef;
        public string kind;
        public List<KimodoCommandAnimationSegmentMetadata> timelineSegments = new List<KimodoCommandAnimationSegmentMetadata>();
        public string transitionJson;
        public string source;
        public string analysisPath;
        public string kmbPath;
        public int logicalStartFrame;
        public int logicalEndFrameExclusive;
        public int startFrame;
        public int endFrameExclusive;
        public string animatorImportName;
        public string importKey;
    }

    [Serializable]
    internal sealed class KimodoCommandAnimationSegmentMetadata
    {
        public string role;
        public string timelineClipAssetRef;
    }

    [Serializable]
    internal sealed class KimodoCommandGenerationMetadata
    {
        public string requestId;
        public string character;
        public string animation;
        public string status;
        public string stage;
        public string message;
        public string error;
        public string startedAtUtc;
        public string updatedAtUtc;
    }

    [Serializable]
    internal sealed class KimodoCommandAnimatorImportMetadata
    {
        public string characterRef;
        public string sourceAnimatorRef;
        public string name;
    }

    internal static partial class command_context
    {
        private const string SessionJsonSchemaVersion = "vNext.transition_clip.1";
        private const string GeneratedSessionsFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Sessions";

        // Rebuilt lazily after every editor domain reload.
        private static bool timelineSessionsRestored;

        private static void PersistTimelineSessionMetadata(TimelineSessionRecord session)
        {
            if (session?.Metadata == null || session.TimelineAsset == null)
            {
                return;
            }

            string sessionFolder = GetSessionGeneratedFolder(session);
            string analysisFolder = Path.Combine(sessionFolder, "Analyses");
            string motionFolder = Path.Combine(sessionFolder, "Motion");
            Directory.CreateDirectory(sessionFolder);
            Directory.CreateDirectory(analysisFolder);
            Directory.CreateDirectory(motionFolder);
            KimodoCommandSessionMetadata metadata = session.Metadata;
            metadata.schemaVersion = SessionJsonSchemaVersion;
            metadata.sessionId = session.Id.ToString("D");
            metadata.sessionName = session.Name;
            metadata.sessionRevision = Math.Max(0, metadata.sessionRevision) + 1;
            metadata.sessionJsonPath = ToProjectRelativePath(GetSessionJsonAbsolutePath(session));
            metadata.isAutomatic = session.IsAutomatic;
            metadata.isCurrent = ReferenceEquals(currentTimelineSession, session);
            metadata.updatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            metadata.characters = session.Characters.Select(character => new KimodoCommandCharacterMetadata
            {
                characterRef = character.CharacterRef,
                trackName = character.Track != null ? character.Track.name : string.Empty,
                poseCacheTrackName = character.PoseCacheTrack != null ? character.PoseCacheTrack.name : string.Empty
            }).ToList();
            metadata.animations = new List<KimodoCommandAnimationMetadata>();
            metadata.animatorImports = session.Characters.SelectMany(character => character.AnimatorImports.Select(imported =>
                new KimodoCommandAnimatorImportMetadata
                {
                    characterRef = character.CharacterRef,
                    sourceAnimatorRef = imported.SourceAnimatorRef,
                    name = imported.Name
                })).ToList();
            foreach (TimelineCharacterRecord character in session.Characters)
            foreach (TimelineAnimationRecord animation in character.Animations)
            {
                string analysisPath = string.Empty;
                string kmbPath = string.Empty;
                if (animation.Analysis != null)
                {
                    analysisPath = Path.Combine(analysisFolder, $"animation_{animation.Id:D}_analysis.json");
                    File.WriteAllText(analysisPath, animation.Analysis.ToString());
                    analysisPath = ToProjectRelativePath(analysisPath);
                }
                if (animation.KmbBytes != null && animation.KmbBytes.Length > 0)
                {
                    kmbPath = Path.Combine(motionFolder, $"motion_{animation.Id:D}.kmb");
                    File.WriteAllBytes(kmbPath, animation.KmbBytes);
                    kmbPath = ToProjectRelativePath(kmbPath);
                }
                metadata.animations.Add(new KimodoCommandAnimationMetadata
                {
                    animationId = animation.Id.ToString("D"),
                    name = animation.Name,
                    characterRef = character.CharacterRef,
                    timelineClipAssetRef = animation.TimelineClip != null ? GetObjectReference(animation.TimelineClip.asset) : string.Empty,
                    kind = animation.Kind,
                    timelineSegments = animation.TimelineSegments.Select(segment => new KimodoCommandAnimationSegmentMetadata
                    {
                        role = segment.Role,
                        timelineClipAssetRef = segment.TimelineClip != null ? GetObjectReference(segment.TimelineClip.asset) : string.Empty
                    }).ToList(),
                    transitionJson = animation.Transition != null ? animation.Transition.ToString(Formatting.None) : string.Empty,
                    source = animation.Source,
                    analysisPath = analysisPath,
                    kmbPath = kmbPath,
                    logicalStartFrame = Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate)),
                    logicalEndFrameExclusive = Mathf.RoundToInt((float)(animation.TimelineEndSeconds * SessionFrameRate)),
                    startFrame = animation.StartFrame,
                    endFrameExclusive = animation.EndFrameExclusive,
                    animatorImportName = animation.AnimatorImportName,
                    importKey = animation.ImportKey,
                });
            }
            PersistSessionJson(session, metadata);
            EditorUtility.SetDirty(metadata);
            EditorUtility.SetDirty(session.TimelineAsset);
        }

        private static string GetSessionGeneratedFolder(TimelineSessionRecord session)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Session is required to resolve its generated folder.");
            }
            string safeName = KimodoRuntimeUtility.SanitizeName(session.Name, "Session");
            return Path.Combine(Directory.GetCurrentDirectory(), GeneratedSessionsFolder.Replace('/', Path.DirectorySeparatorChar), safeName);
        }

        private static string GetSessionJsonAbsolutePath(TimelineSessionRecord session) =>
            Path.Combine(GetSessionGeneratedFolder(session), "session.json");

        private static string ToProjectRelativePath(string absolutePath)
        {
            string root = Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedRoot = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(absolutePath).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedPath.Substring(normalizedRoot.Length).Replace('\\', '/')
                : normalizedPath.Replace('\\', '/');
        }

        private static void PersistSessionJson(TimelineSessionRecord session, KimodoCommandSessionMetadata metadata)
        {
            JObject json = new JObject
            {
                ["schema_version"] = SessionJsonSchemaVersion,
                ["session_id"] = metadata.sessionId,
                ["name"] = metadata.sessionName,
                ["session_revision"] = metadata.sessionRevision,
                ["session_json_path"] = metadata.sessionJsonPath,
                ["timeline_asset_path"] = session.TimelineAssetPath ?? string.Empty,
                ["is_current"] = metadata.isCurrent,
                ["is_automatic"] = metadata.isAutomatic,
                ["characters"] = new JArray(),
                ["animations"] = new JArray(),
                ["poses"] = new JArray(),
                ["analyses"] = GetPersistedAnalysisRecords(session),
                ["generations"] = new JArray((metadata.generations ?? new List<KimodoCommandGenerationMetadata>()).Select(DescribeGeneration))
            };

            JArray characters = (JArray)json["characters"];
            JArray animations = (JArray)json["animations"];
            JArray poses = (JArray)json["poses"];
            foreach (TimelineCharacterRecord character in session.Characters)
            {
                var characterJson = new JObject
                {
                    ["character_ref"] = character.CharacterRef ?? string.Empty,
                    ["name"] = character.Name,
                    ["track_name"] = character.Track != null ? character.Track.name : string.Empty,
                    ["pose_cache_track_name"] = character.PoseCacheTrack != null ? character.PoseCacheTrack.name : string.Empty,
                    ["animations"] = new JArray(),
                    ["constraints"] = new JArray()
                };
                foreach (TimelineAnimationRecord animation in character.Animations)
                {
                    JObject animationJson = DescribeAnimation(animation);
                    animationJson["animation_id"] = animation.Id.ToString("D");
                    animationJson["source"] = animation.Source;
                    KimodoCommandAnimationMetadata persisted = metadata.animations.FirstOrDefault(item =>
                        string.Equals(item.animationId, animation.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    animationJson["analysis_path"] = persisted?.analysisPath ?? string.Empty;
                    animationJson["motion_path"] = persisted?.kmbPath ?? string.Empty;
                    animationJson["analysis_start_frame"] = animation.StartFrame;
                    animationJson["analysis_end_frame_exclusive"] = animation.EndFrameExclusive;
                    animationJson["animator_import_name"] = animation.AnimatorImportName ?? string.Empty;
                    animationJson["kind"] = animation.Kind;
                    if (animation.Transition != null)
                    {
                        animationJson["transition"] = animation.Transition.DeepClone();
                    }
                    animations.Add(animationJson);
                    ((JArray)characterJson["animations"]).Add(animationJson.DeepClone());
                }
                if (character.Track != null)
                {
                    foreach (KimodoConstraintMarker marker in character.Track.GetMarkers().OfType<KimodoConstraintMarker>()
                        .Where(item => item.constraintEnabled))
                    {
                        ((JArray)characterJson["constraints"]).Add(DescribeTimelineConstraint(marker, 0));
                    }
                }
                if (character.PoseCacheTrack != null)
                {
                    foreach (KimodoConstraintMarker marker in character.PoseCacheTrack.GetMarkers().OfType<KimodoConstraintMarker>())
                    {
                        CharacterPose markerPose = marker.SampleData != null &&
                            KimodoSampleResultPoseUtility.TryDecode(marker.SampleData, out CharacterPose decodedPose, out _)
                            ? decodedPose
                            : null;
                        poses.Add(new JObject
                        {
                            ["character"] = character.Name,
                            ["track"] = character.PoseCacheTrack.name,
                            ["marker_id"] = marker.name ?? string.Empty,
                            ["frame"] = Mathf.RoundToInt((float)(marker.time * SessionFrameRate)),
                            ["data"] = markerPose != null
                                ? CharacterPoseJson.ToJson(markerPose)
                                : new JObject()
                        });
                    }
                }
                characters.Add(characterJson);
            }

            WriteJsonAtomically(GetSessionJsonAbsolutePath(session), json);
        }

        internal static void WriteJsonAtomically(string path, JObject json)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A JSON output path is required.", nameof(path));
            string tempPath = path + ".tmp_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tempPath, (json ?? new JObject()).ToString(Formatting.Indented));
            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(tempPath, path, overwrite: true);
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        File.Copy(tempPath, path, overwrite: true);
                        File.Delete(tempPath);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static JArray GetPersistedAnalysisRecords(TimelineSessionRecord session)
        {
            string folder = Path.Combine(GetSessionGeneratedFolder(session), "Analyses");
            if (!Directory.Exists(folder)) return new JArray();
            var records = new JArray();
            foreach (string path in Directory.GetFiles(folder, "analysis_*.json").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    JObject record = JObject.Parse(File.ReadAllText(path));
                    // session.json is an AI navigation index, never a duplicate store for large analysis payloads.
                    // Read the returned analysis_path only when the selected result needs its sparse details.
                    records.Add(new JObject
                    {
                        ["analysis_id"] = record.Value<string>("analysis_id") ?? string.Empty,
                        ["analysis_path"] = ToProjectRelativePath(path),
                        ["motion_path"] = record.Value<string>("motion_path") ?? string.Empty,
                        ["character"] = record.Value<string>("character") ?? string.Empty,
                        ["character_ref"] = record.Value<string>("character_ref") ?? string.Empty,
                        ["animation_id"] = record.Value<string>("animation_id") ?? string.Empty,
                        ["animation_name"] = record.Value<string>("animation_name") ?? string.Empty,
                        ["input_signature"] = record.Value<string>("input_signature") ?? string.Empty,
                        ["created_at_utc"] = record.Value<string>("created_at_utc") ?? string.Empty,
                        ["keyframes"] = record["analysis"]?["keyframes"]?.DeepClone() ?? new JArray(),
                        ["foot_contacts"] = record["analysis"]?["foot_contacts"]?.DeepClone() ?? new JArray(),
                        // Keep the compact, self-describing visual index in session.json.  The PNG bytes and dense
                        // KMB motion remain on disk, so Session navigation stays cheap.
                        ["pictures"] = record["pictures"]?.DeepClone() ?? new JObject()
                    });
                }
                catch (Exception exception)
                {
                    records.Add(new JObject
                    {
                        ["analysis_path"] = ToProjectRelativePath(path),
                        ["error"] = exception.Message
                    });
                }
            }
            return records;
        }

        private static JObject DescribeGeneration(KimodoCommandGenerationMetadata value) => new JObject
        {
            ["request_id"] = value.requestId ?? string.Empty,
            ["character"] = value.character ?? string.Empty,
            ["animation"] = value.animation ?? string.Empty,
            ["status"] = value.status ?? string.Empty,
            ["stage"] = value.stage ?? string.Empty,
            ["message"] = value.message ?? string.Empty,
            ["error"] = value.error ?? string.Empty,
            ["started_at_utc"] = value.startedAtUtc ?? string.Empty,
            ["updated_at_utc"] = value.updatedAtUtc ?? string.Empty
        };

        private static void EnsureTimelineSessionsRestored()
        {
            if (timelineSessionsRestored)
            {
                return;
            }
            timelineSessionsRestored = true;

            string[] guids = AssetDatabase.FindAssets("t:TimelineAsset", new[] { GeneratedTimelineFolder });
            var restored = new List<TimelineSessionRecord>();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TimelineAsset timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
                KimodoCommandSessionMetadata metadata = AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<KimodoCommandSessionMetadata>().FirstOrDefault();
                if (timeline == null || metadata == null || !Guid.TryParse(metadata.sessionId, out Guid sessionId))
                {
                    continue;
                }
                if (!string.Equals(metadata.schemaVersion, SessionJsonSchemaVersion, StringComparison.Ordinal))
                {
                    Debug.LogWarning($"[Kimodo] Ignoring Session '{metadata.sessionName}' with unsupported schema '{metadata.schemaVersion}'. Expected '{SessionJsonSchemaVersion}'.");
                    continue;
                }
                PlayableDirector director = Resources.FindObjectsOfTypeAll<PlayableDirector>()
                    .FirstOrDefault(item => item != null && item.playableAsset == timeline && item.gameObject.scene.IsValid());
                if (director == null)
                {
                    var directorObject = new GameObject($"{TimelineDirectorNamePrefix}{KimodoRuntimeUtility.SanitizeName(metadata.sessionName, "Session")}");
                    directorObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
                    director = directorObject.AddComponent<PlayableDirector>();
                    director.playableAsset = timeline;
                }
                var session = new TimelineSessionRecord(sessionId, metadata.sessionName, director, timeline, path, metadata.isAutomatic, metadata);
                foreach (KimodoCommandCharacterMetadata savedCharacter in metadata.characters ?? new List<KimodoCommandCharacterMetadata>())
                {
                    GameObject root = ResolveObject(savedCharacter.characterRef) as GameObject;
                    Animator animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
                    AnimationTrack track = timeline.GetRootTracks().OfType<AnimationTrack>()
                        .FirstOrDefault(item => string.Equals(item.name, savedCharacter.trackName, StringComparison.Ordinal));
                    AnimationTrack poseTrack = track?.GetChildTracks().OfType<AnimationTrack>()
                        .FirstOrDefault(item => string.Equals(item.name, savedCharacter.poseCacheTrackName, StringComparison.Ordinal));
                    if (root == null || animator == null || track == null || poseTrack == null)
                    {
                        continue;
                    }
                    KimodoLocalAvatarUtility.AvatarResolveResult avatarResult = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                    if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatarResult.Avatar))
                    {
                        continue;
                    }
                    var character = new TimelineCharacterRecord(savedCharacter.characterRef, root, animator, avatarResult.Avatar, track, poseTrack, avatarResult.Error);
                    director.SetGenericBinding(track, animator);
                    session.Characters.Add(character);
                }
                foreach (KimodoCommandAnimatorImportMetadata imported in metadata.animatorImports ?? new List<KimodoCommandAnimatorImportMetadata>())
                {
                    TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                        string.Equals(item.CharacterRef, imported.characterRef, StringComparison.Ordinal));
                    if (character != null) character.AnimatorImports.Add(new AnimatorImportRecord(imported.sourceAnimatorRef, imported.name));
                }
                foreach (KimodoCommandAnimationMetadata saved in metadata.animations ?? new List<KimodoCommandAnimationMetadata>())
                {
                    TimelineCharacterRecord character = session.Characters.FirstOrDefault(item => string.Equals(item.CharacterRef, saved.characterRef, StringComparison.Ordinal));
                    if (character == null || !Guid.TryParse(saved.animationId, out Guid animationId))
                    {
                        continue;
                    }
                    List<KimodoCommandAnimationSegmentMetadata> savedSegments = saved.timelineSegments ?? new List<KimodoCommandAnimationSegmentMetadata>();
                    if (savedSegments.Count == 0)
                    {
                        continue;
                    }
                    var segments = new List<TimelineAnimationSegment>();
                    foreach (KimodoCommandAnimationSegmentMetadata savedSegment in savedSegments)
                    {
                        TimelineClip segmentClip = character.Track.GetClips().FirstOrDefault(item =>
                            string.Equals(GetObjectReference(item.asset), savedSegment.timelineClipAssetRef, StringComparison.Ordinal));
                        if (segmentClip == null)
                        {
                            segments.Clear();
                            break;
                        }
                        segments.Add(new TimelineAnimationSegment(
                            savedSegment.role,
                            (segmentClip.asset as AnimationPlayableAsset)?.clip,
                            segmentClip));
                    }
                    if (segments.Count == 0)
                    {
                        continue;
                    }
                    TimelineClip clip = segments[0].TimelineClip;
                    AnimationClip animationClip = segments[0].Clip;
                    JObject analysis = File.Exists(saved.analysisPath) ? JObject.Parse(File.ReadAllText(saved.analysisPath)) : null;
                    byte[] kmb = File.Exists(saved.kmbPath) ? File.ReadAllBytes(saved.kmbPath) : null;
                    JObject transition = string.IsNullOrWhiteSpace(saved.transitionJson) ? null : JObject.Parse(saved.transitionJson);
                    var restoredAnimation = new TimelineAnimationRecord(animationId, string.IsNullOrWhiteSpace(saved.name) ? clip.displayName : saved.name, saved.source, animationClip, clip, analysis, kmb, saved.startFrame, saved.endFrameExclusive)
                    {
                        AnimatorImportName = saved.animatorImportName ?? string.Empty,
                        ImportKey = saved.importKey ?? string.Empty
                    };
                    restoredAnimation.ConfigureComposite(saved.kind, segments, transition);
                    character.Animations.Add(restoredAnimation);
                    character.NextStartSeconds = Math.Max(
                        character.NextStartSeconds,
                        restoredAnimation.TimelineEndSeconds + command_context.ClipSafeZoneSeconds);
                }
                lock (TimelineSessionsLock)
                {
                    TimelineSessions[session.Name] = session;
                }
                restored.Add(session);
            }
            currentTimelineSession = restored.Where(item => item.Metadata.isCurrent)
                .OrderByDescending(item => item.Metadata.updatedAtUtc).FirstOrDefault();
            if (currentTimelineSession != null)
            {
                ActivateTimelineSession(currentTimelineSession);
            }
        }
    }
}
