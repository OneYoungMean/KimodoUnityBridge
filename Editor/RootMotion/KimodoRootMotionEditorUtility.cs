using System;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoRootMotionEditorUtility
    {
        private const string GeneratedFolder = "Assets/KimodoGeneratedClips";
        private const string Suffix = "_RootMotion";

        public static bool TryCreateRootMotionClipAsset(
            AnimationClip sourceClip,
            Avatar avatar,
            out AnimationClip outputClip,
            out string error)
        {
            outputClip = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!KimodoRetargetTools.IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            AnimationClip bakedClip = FootRootMotionClipBaker.AutoFixRootMotion(sourceClip, avatar, out error);
            if (bakedClip == null)
            {
                return false;
            }

            try
            {
                EnsureFolderExists(GeneratedFolder);

                string baseName = string.IsNullOrWhiteSpace(sourceClip.name) ? "Clip" : sourceClip.name.Trim();
                string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedFolder}/{baseName}{Suffix}.anim");
                AssetDatabase.CreateAsset(bakedClip, assetPath);
                EditorUtility.SetDirty(bakedClip);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                outputClip = bakedClip;
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Object.DestroyImmediate(bakedClip);
                outputClip = null;
                error = $"Create root motion clip asset failed: {ex.Message}";
                return false;
            }
        }

        public static bool TryResolveAvatarForPlayableClip(KimodoPlayableClip playableClip, out Avatar avatar, out string error)
        {
            avatar = null;
            error = string.Empty;

            if (playableClip == null)
            {
                error = "Playable clip is null.";
                return false;
            }

            var constraintProvider = new KimodoEditorConstraintProvider();
            GameObject bindingObject = constraintProvider.FindTimelineBindingObjectForAsset(playableClip);
            if (bindingObject != null)
            {
                KimodoLocalAvatarUtility.AvatarResolveResult result = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(bindingObject);
                if (result.IsHumanoid && result.Avatar != null)
                {
                    avatar = result.Avatar;
                    return true;
                }
            }

            if (playableClip.CustomRetargetAvatar != null &&
                playableClip.CustomRetargetAvatar.isValid &&
                playableClip.CustomRetargetAvatar.isHuman)
            {
                avatar = playableClip.CustomRetargetAvatar;
                return true;
            }

            error = "Cannot resolve humanoid avatar from timeline binding or custom avatar.";
            return false;
        }

        private static void EnsureFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string[] parts = folderPath.Split('/');
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
