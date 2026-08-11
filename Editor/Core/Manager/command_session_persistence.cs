using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal sealed class KimodoCommandSessionMetadata : ScriptableObject
    {
        public string sessionId;
        public string sessionName;
        public bool isAutomatic;
        public bool isCurrent;
        public string updatedAtUtc;
        public List<KimodoCommandCharacterMetadata> characters = new List<KimodoCommandCharacterMetadata>();
        public List<KimodoCommandAnimationMetadata> animations = new List<KimodoCommandAnimationMetadata>();
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
        public string characterRef;
        public string timelineClipAssetRef;
        public string source;
        public string analysisPath;
        public string kmbPath;
        public int startFrame;
        public int endFrameExclusive;
        public string animatorImportName;
        public string importKey;
        public string fromAnimation;
        public string toAnimation;
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
        // Rebuilt lazily after every editor domain reload.
        private static bool timelineSessionsRestored;

        private static void PersistTimelineSessionMetadata(TimelineSessionRecord session)
        {
            if (session?.Metadata == null || session.TimelineAsset == null)
            {
                return;
            }

            string cacheFolder = Path.Combine(Directory.GetCurrentDirectory(), "Library", "KimodoCache", "Commands");
            Directory.CreateDirectory(cacheFolder);
            KimodoCommandSessionMetadata metadata = session.Metadata;
            metadata.sessionId = session.Id.ToString("D");
            metadata.sessionName = session.Name;
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
                    analysisPath = Path.Combine(cacheFolder, $"animation_{animation.Id:D}_analysis.json");
                    File.WriteAllText(analysisPath, animation.Analysis.ToString());
                }
                if (animation.KmbBytes != null && animation.KmbBytes.Length > 0)
                {
                    kmbPath = Path.Combine(cacheFolder, $"motion_{animation.Id:D}.kmb");
                    File.WriteAllBytes(kmbPath, animation.KmbBytes);
                }
                metadata.animations.Add(new KimodoCommandAnimationMetadata
                {
                    animationId = animation.Id.ToString("D"),
                    characterRef = character.CharacterRef,
                    timelineClipAssetRef = animation.TimelineClip != null ? GetObjectReference(animation.TimelineClip.asset) : string.Empty,
                    source = animation.Source,
                    analysisPath = analysisPath,
                    kmbPath = kmbPath,
                    startFrame = animation.StartFrame,
                    endFrameExclusive = animation.EndFrameExclusive,
                    animatorImportName = animation.AnimatorImportName,
                    importKey = animation.ImportKey,
                    fromAnimation = animation.FromAnimation,
                    toAnimation = animation.ToAnimation
                });
            }
            EditorUtility.SetDirty(metadata);
            EditorUtility.SetDirty(session.TimelineAsset);
        }

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
                    TimelineClip clip = character.Track.GetClips().FirstOrDefault(item => string.Equals(GetObjectReference(item.asset), saved.timelineClipAssetRef, StringComparison.Ordinal));
                    if (clip == null)
                    {
                        continue;
                    }
                    AnimationClip animationClip = (clip.asset as AnimationPlayableAsset)?.clip;
                    JObject analysis = File.Exists(saved.analysisPath) ? JObject.Parse(File.ReadAllText(saved.analysisPath)) : null;
                    byte[] kmb = File.Exists(saved.kmbPath) ? File.ReadAllBytes(saved.kmbPath) : null;
                    var restoredAnimation = new TimelineAnimationRecord(animationId, clip.displayName, saved.source, animationClip, clip, analysis, kmb, saved.startFrame, saved.endFrameExclusive)
                    {
                        AnimatorImportName = saved.animatorImportName ?? string.Empty,
                        ImportKey = saved.importKey ?? string.Empty,
                        FromAnimation = saved.fromAnimation ?? string.Empty,
                        ToAnimation = saved.toAnimation ?? string.Empty
                    };
                    character.Animations.Add(restoredAnimation);
                    character.NextStartSeconds = Math.Max(
                        character.NextStartSeconds,
                        clip.end + command_context.ClipSafeZoneSeconds);
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
