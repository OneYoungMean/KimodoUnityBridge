using System;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorAvatarCacheUtility
    {
        private const string GeneratedAvatarFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Avatars";

        public static bool TryLoadGeneratedAvatarCache(GameObject avatarRoot, out Avatar avatar, out string cachePath)
        {
            avatar = null;
            cachePath = BuildAvatarCachePath(avatarRoot);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return false;
            }

            avatar = AssetDatabase.LoadAssetAtPath<Avatar>(cachePath);
            return avatar != null;
        }

        public static bool TrySaveGeneratedAvatarCache(GameObject avatarRoot, Avatar generatedAvatar, out Avatar savedAvatar, out string error)
        {
            savedAvatar = null;
            error = string.Empty;

            if (avatarRoot == null)
            {
                error = "Avatar root is null.";
                return false;
            }

            if (generatedAvatar == null)
            {
                error = "Generated avatar is null.";
                return false;
            }

            string cachePath = BuildAvatarCachePath(avatarRoot);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                error = "Avatar cache path is empty.";
                return false;
            }

            try
            {
                KimodoEditorClipWritebackService.EnsureFolderExists(GeneratedAvatarFolder);
                if (AssetDatabase.LoadAssetAtPath<Avatar>(cachePath) != null)
                {
                    AssetDatabase.DeleteAsset(cachePath);
                }

                AssetDatabase.CreateAsset(generatedAvatar, cachePath);
                KimodoEditorClipWritebackService.FlushWritebackAssets();
                savedAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(cachePath);
                if (savedAvatar == null)
                {
                    error = "Saved avatar cache could not be loaded.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Save generated avatar cache failed: {ex.Message}";
                return false;
            }
        }

        private static string BuildAvatarCachePath(GameObject avatarRoot)
        {
            string safeName = KimodoRuntimeUtility.SanitizeName(avatarRoot != null ? avatarRoot.name : "Avatar", "Avatar");
            int hash = ComputeHierarchyHash(avatarRoot != null ? avatarRoot.transform : null);
            return $"{GeneratedAvatarFolder}/{safeName}_{hash:X8}.asset";
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
    }
}
