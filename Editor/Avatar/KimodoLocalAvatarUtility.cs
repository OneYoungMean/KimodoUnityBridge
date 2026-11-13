using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoLocalAvatarUtility
    {
        public readonly struct AvatarResolveResult
        {
            public AvatarResolveResult(Avatar avatar, bool isHumanoid, string source, string error)
            {
                Avatar = avatar;
                IsHumanoid = isHumanoid;
                Source = source ?? string.Empty;
                Error = error ?? string.Empty;
            }

            public Avatar Avatar { get; }
            public bool IsHumanoid { get; }
            public string Source { get; }
            public string Error { get; }
        }

        public static AvatarResolveResult ResolveAvatarFromGameObject(GameObject avatarRoot)
        {
            return ResolveAvatarFromGameObject(avatarRoot, allowGeneration: true);
        }

        public static AvatarResolveResult ResolveAvatarFromGameObject(GameObject avatarRoot, bool allowGeneration)
        {
            if (TryEnsureHumanoidAvatar(avatarRoot, out Avatar avatar, out string source, out string error,
                    allowGeneration))
            {
                return new AvatarResolveResult(avatar, KimodoRetargetCoreUtility.IsValidHumanoid(avatar), source, string.Empty);
            }

            return new AvatarResolveResult(null, false, string.Empty, error);
        }

        public static AvatarResolveResult ResolveTimelineSourceAvatar(TrackAsset track, Animator animator, bool allowGeneration = false)
        {
            if (animator == null || animator.gameObject == null)
            {
                return new AvatarResolveResult(null, false, string.Empty, "Timeline binding Animator is missing.");
            }

            Avatar customAvatar = ResolveTrackCustomAvatar(track);
            if (customAvatar != null)
            {
                bool valid = KimodoRetargetCoreUtility.IsValidHumanoid(customAvatar) &&
                    CheckAvatarValid(customAvatar, animator.gameObject);
                return new AvatarResolveResult(
                    valid ? customAvatar : null,
                    valid,
                    "TrackFirstClip",
                    valid ? string.Empty : "The first Track clip Custom Avatar is invalid or does not match the Timeline binding skeleton.");
            }

            return ResolveAvatarFromGameObject(animator.gameObject, allowGeneration);
        }

        public static Avatar ResolveTrackCustomAvatar(TrackAsset track)
        {
            if (track == null)
            {
                return null;
            }

            TimelineClip firstClip = track.GetClips()
                .Where(clip => clip != null)
                .OrderBy(clip => clip.start)
                .FirstOrDefault();
            return (firstClip?.asset as KimodoPlayableClip)?.CustomRetargetAvatar;
        }

        public static bool TryEnsureHumanoidAvatar(
            GameObject avatarRoot,
            out Avatar avatar,
            out string source,
            out string error,
            bool allowGeneration = true)
        {
            avatar = null;
            source = string.Empty;
            error = string.Empty;

            if (avatarRoot == null)
            {
                error = "Avatar root is null.";
                return false;
            }

            Animator animator = avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator != null && KimodoRetargetCoreUtility.IsValidHumanoid(animator.avatar) && CheckAvatarValid(animator.avatar, avatarRoot))
            {
                avatar = animator.avatar;
                source = "Animator";
                return true;
            }

            if (KimodoHumanoidAvatarBuilderUtility.TryLoadImporterAvatar(avatarRoot, out Avatar importerAvatar, out _) &&
                KimodoRetargetCoreUtility.IsValidHumanoid(importerAvatar) &&
                CheckAvatarValid(importerAvatar, avatarRoot))
            {
                avatar = importerAvatar;
                source = "Importer";
                return true;
            }

            if (KimodoEditorAvatarCacheUtility.TryLoadGeneratedAvatarCache(avatarRoot, out Avatar cached, out _))
            {
                if (KimodoRetargetCoreUtility.IsValidHumanoid(cached) && CheckAvatarValid(cached, avatarRoot))
                {
                    avatar = cached;
                    source = "Cache";
                    return true;
                }
            }

            if (KimodoEditorAvatarCacheUtility.HasGeneratedAvatarCache(avatarRoot))
            {
                error = "A generated Avatar cache exists but is invalid for this character; automatic regeneration was skipped.";
                return false;
            }

            if (!allowGeneration)
            {
                error = "No valid Humanoid Avatar is assigned, imported, or cached; automatic generation was not requested.";
                return false;
            }

            Avatar generated = KimodoHumanoidAvatarBuilderUtility.GenerateHumanoidAvatar(
                avatarRoot,
                out string generateError);
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(generated) || !CheckAvatarValid(generated, avatarRoot))
            {
                error = string.IsNullOrWhiteSpace(generateError)
                    ? "Generated avatar is invalid."
                    : generateError;
                return false;
            }

            if (KimodoEditorAvatarCacheUtility.TrySaveGeneratedAvatarCache(avatarRoot, generated, out Avatar saved, out string saveError))
            {
                if (KimodoRetargetCoreUtility.IsValidHumanoid(saved) && CheckAvatarValid(saved, avatarRoot))
                {
                    avatar = saved;
                    source = "GeneratedCache";
                    return true;
                }
            }
            else
            {
                Debug.LogWarning($"[Kimodo][Avatar] Save generated avatar failed: {saveError}");
            }

            error = string.IsNullOrWhiteSpace(saveError)
                ? "Generated Avatar could not be cached; original model import settings have been restored."
                : $"Generated Avatar could not be cached: {saveError}";
            return false;
        }

        public static bool CheckAvatarValid(Avatar avatar, GameObject gameObject)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar) || gameObject == null)
            {
                return false;
            }

            var allBones = gameObject.GetComponentsInChildren<Transform>(true).ToArray();
            HumanBone[] humanBones = avatar.humanDescription.human;
            for (int i = 0; i < humanBones.Length; i++)
            {
                string boneName = humanBones[i].boneName;
                bool found = false;
                for (int j = 0; j < allBones.Length; j++)
                {
                    if (string.Equals(allBones[j].name, boneName, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

