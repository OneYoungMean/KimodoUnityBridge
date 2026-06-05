using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

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
            if (TryEnsureHumanoidAvatar(avatarRoot, out Avatar avatar, out string source, out string error))
            {
                return new AvatarResolveResult(avatar, IsValidHumanoid(avatar), source, string.Empty);
            }

            return new AvatarResolveResult(null, false, string.Empty, error);
        }

        public static bool TryEnsureHumanoidAvatar(
            GameObject avatarRoot,
            out Avatar avatar,
            out string source,
            out string error)
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
            if (animator != null && IsValidHumanoid(animator.avatar) && CheckAvatarValid(animator.avatar, avatarRoot))
            {
                avatar = animator.avatar;
                source = "Animator";
                return true;
            }

            if (TryGetImporterAvatar(avatarRoot, out Avatar importerAvatar) &&
                IsValidHumanoid(importerAvatar) &&
                CheckAvatarValid(importerAvatar, avatarRoot))
            {
                avatar = importerAvatar;
                source = "Importer";
                return true;
            }

            if (KimodoEditorClipWritebackService.TryLoadGeneratedAvatarCache(avatarRoot, out Avatar cached, out _))
            {
                if (IsValidHumanoid(cached) && CheckAvatarValid(cached, avatarRoot))
                {
                    avatar = cached;
                    source = "Cache";
                    return true;
                }
            }

            Avatar generated = GenerateHumanoidAvatar(avatarRoot, out string generateError);
            if (!IsValidHumanoid(generated) || !CheckAvatarValid(generated, avatarRoot))
            {
                error = string.IsNullOrWhiteSpace(generateError)
                    ? "Generated avatar is invalid."
                    : generateError;
                return false;
            }

            string generatedAssetPath = AssetDatabase.GetAssetPath(generated);
            if (!string.IsNullOrEmpty(generatedAssetPath))
            {
                avatar = generated;
                source = "GeneratedImporter";
                return true;
            }

            if (KimodoEditorClipWritebackService.TrySaveGeneratedAvatarCache(avatarRoot, generated, out Avatar saved, out string saveError))
            {
                if (IsValidHumanoid(saved))
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

            avatar = generated;
            source = "GeneratedTemp";
            return true;
        }

        public static bool TryEnsureHumanoidAvatar(
            Animator animator,
            out Avatar avatar,
            out string source,
            out string error)
        {
            avatar = null;
            source = string.Empty;
            error = string.Empty;

            if (animator == null)
            {
                error = "Animator is null.";
                return false;
            }

            GameObject avatarRoot = animator.avatarRoot != null ? animator.avatarRoot.gameObject : animator.gameObject;
            return TryEnsureHumanoidAvatar(avatarRoot, out avatar, out source, out error);
        }

        public static bool CheckAvatarValid(Avatar avatar, GameObject gameObject)
        {
            if (!IsValidHumanoid(avatar) || gameObject == null)
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

        private static bool IsValidHumanoid(Avatar avatar)
        {
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        private static bool TryGetImporterAvatar(GameObject gameObject, out Avatar avatar)
        {
            avatar = null;
            if (gameObject == null)
            {
                return false;
            }

            if (!KimodoHumanoidAvatarBuilderUtility.TryLoadImporterAvatar(gameObject, out avatar, out _))
            {
                return false;
            }
            return true;
        }

        private static Avatar GenerateHumanoidAvatar(GameObject gameObject, out string error)
        {
            return KimodoHumanoidAvatarBuilderUtility.GenerateHumanoidAvatar(
                gameObject,
                includeExtendedNameAliases: true,
                normalizeSourceTransformBeforeClone: true,
                forceUnitScaleOnClone: false,
                avatarNameSuffix: "Humanoid",
                out error);
        }

    }
}

