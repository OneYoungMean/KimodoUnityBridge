using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoLocalAvatarUtility
    {
        private const string AvatarCacheFolder = "Assets/KimodoGenerated/Avatars";

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
            if (avatarRoot == null)
            {
                error = "Avatar root is null.";
                return false;
            }

            if (IsValidHumanoid(animator.avatar) && CheckAvatarValid(animator.avatar, avatarRoot))
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

            EnsureFolderExists(AvatarCacheFolder);
            string cachePath = BuildAvatarCachePath(avatarRoot);
            if (File.Exists(cachePath))
            {
                Avatar cached = AssetDatabase.LoadAssetAtPath<Avatar>(cachePath);
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

            try
            {
                if (File.Exists(cachePath))
                {
                    AssetDatabase.DeleteAsset(cachePath);
                }

                AssetDatabase.CreateAsset(generated, cachePath);
                AssetDatabase.SaveAssets();
                Avatar saved = AssetDatabase.LoadAssetAtPath<Avatar>(cachePath);
                if (IsValidHumanoid(saved))
                {
                    avatar = saved;
                    source = "GeneratedCache";
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Kimodo][Avatar] Save generated avatar failed: {e.Message}");
            }

            avatar = generated;
            source = "GeneratedTemp";
            return true;
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

        private static string BuildAvatarCachePath(GameObject avatarRoot)
        {
            string safeName = SanitizeName(avatarRoot != null ? avatarRoot.name : "Avatar");
            int hash = ComputeHierarchyHash(avatarRoot != null ? avatarRoot.transform : null);
            return $"{AvatarCacheFolder}/{safeName}_{hash:X8}.asset";
        }

        private static int ComputeHierarchyHash(Transform root)
        {
            unchecked
            {
                int hash = 5381;
                if (root == null)
                {
                    return hash;
                }

                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    string path = AnimationUtility.CalculateTransformPath(all[i], root);
                    string name = $"{all[i].name}|{path}";
                    for (int j = 0; j < name.Length; j++)
                    {
                        hash = ((hash << 5) + hash) ^ name[j];
                    }
                }

                return hash;
            }
        }

        private static string SanitizeName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "Avatar";
            }

            char[] chars = input.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        private static void EnsureFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string[] parts = folderPath.Split('/');
            if (parts.Length == 0)
            {
                return;
            }

            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }
    }
}
