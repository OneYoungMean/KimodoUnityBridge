using KimodoBridge;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorClipWritebackService
    {
        internal const string GeneratedClipFolder = "Assets/KimodoGeneratedClips";
        internal const string GeneratedClipNamePrefix = "Kimodo_";
        private const string MuscleCacheClipSuffix = "_musclecache";
        private const string LegacyMuscleCacheClipSuffix = "_MuscleCacheClip";
        private const string LegacyMuscleCacheSuffix = "_MuscleCache";
        private const string GeneratedAvatarFolder = GeneratedClipFolder + "/Avatars";
        private const string GeneratedPreviewControllerFolder = GeneratedClipFolder + "/PreviewControllers";

        private static readonly HashSet<string> PendingProtectedClipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool generatedClipTrimScheduled;

        public static AnimationClip CreateGeneratedAnimationClipAsset()
        {
            var newAnimationClip = new AnimationClip
            {
                name = BuildGeneratedAnimationAssetName($"{DateTime.Now:yyyyMMdd_HHmmss_fff}")
            };

            EnsureFolderExists(GeneratedClipFolder);

            string fileName = $"{newAnimationClip.name}.anim";
            string savePath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedClipFolder}/{fileName}");
            AssetDatabase.CreateAsset(newAnimationClip, savePath);
            EditorUtility.SetDirty(newAnimationClip);
            AssetDatabase.SaveAssets();
            ScheduleGeneratedClipTrim(newAnimationClip);
            return newAnimationClip;
        }

        public static bool CreateGeneratedAnimationClipAsset(
            AnimationClip clip,
            string assetName,
            out AnimationClip savedClip,
            out string error)
        {
            savedClip = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "Animation clip is null.";
                return false;
            }

            try
            {
                EnsureFolderExists(GeneratedClipFolder);
                string safeName = BuildGeneratedAnimationAssetName(assetName);
                clip.name = safeName;
                string savePath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedClipFolder}/{safeName}.anim");
                AssetDatabase.CreateAsset(clip, savePath);
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                savedClip = clip;
                ScheduleGeneratedClipTrim(savedClip);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Create generated animation clip asset failed: {ex.Message}";
                return false;
            }
        }

        public static void SaveDirtyAssets(params UnityEngine.Object[] assets)
        {
            if (assets != null)
            {
                for (int i = 0; i < assets.Length; i++)
                {
                    if (assets[i] != null)
                    {
                        EditorUtility.SetDirty(assets[i]);
                    }
                }
            }

            AssetDatabase.SaveAssets();
        }

        public static bool TryDeleteGeneratedAnimationClipAsset(AnimationClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(clip);
            if (!IsGeneratedAnimationClipAssetPath(assetPath))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(assetPath) && AssetDatabase.DeleteAsset(assetPath))
            {
                AssetDatabase.SaveAssets();
                return true;
            }

            return false;
        }

        public static bool TryCreateGeneratedPreviewAnimatorControllerAsset(
            out AnimatorController controller,
            out string assetPath,
            out string error)
        {
            controller = null;
            assetPath = string.Empty;
            error = string.Empty;

            try
            {
                EnsureFolderExists(GeneratedPreviewControllerFolder);
                string controllerName = BuildGeneratedPreviewControllerName();
                assetPath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedPreviewControllerFolder}/{controllerName}.controller");
                controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
                if (controller == null)
                {
                    error = "Animator controller asset creation returned null.";
                    return false;
                }

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (Exception ex)
            {
                controller = null;
                assetPath = string.Empty;
                error = $"Create generated preview animator controller failed: {ex.Message}";
                return false;
            }
        }

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
                EnsureFolderExists(GeneratedAvatarFolder);
                if (AssetDatabase.LoadAssetAtPath<Avatar>(cachePath) != null)
                {
                    AssetDatabase.DeleteAsset(cachePath);
                }

                AssetDatabase.CreateAsset(generatedAvatar, cachePath);
                AssetDatabase.SaveAssets();
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

        public static bool TryGetOrCreateClipCache(AnimationClip targetClip, out AnimationClip cachedClip, out string error)
        {
            cachedClip = null;
            error = string.Empty;

            if (targetClip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            if (TryFindClipCache(targetClip, out cachedClip))
            {
                return true;
            }

            string cachePath = BuildMuscleCacheClipPath(targetClip);
            string cacheName = BuildMuscleCacheClipName(targetClip);
            if (string.IsNullOrWhiteSpace(cachePath) || string.IsNullOrWhiteSpace(cacheName))
            {
                error = "Cache clip path is empty.";
                return false;
            }

            try
            {
                EnsureFolderExists(GeneratedClipFolder);
                RemoveLegacyEmbeddedMuscleCache(targetClip);

                cachedClip = new AnimationClip
                {
                    name = cacheName,
                    legacy = false,
                    frameRate = targetClip.frameRate > 0f ? targetClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE
                };

                AssetDatabase.CreateAsset(cachedClip, cachePath);
                EditorUtility.SetDirty(cachedClip);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                cachedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(cachePath) ?? cachedClip;
                EnsureClipNameMatchesFileName(cachedClip, cacheName);
                ScheduleGeneratedClipTrim(cachedClip);
                return true;
            }
            catch (Exception ex)
            {
                if (cachedClip != null && string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(cachedClip)))
                {
                    UnityEngine.Object.DestroyImmediate(cachedClip);
                }

                cachedClip = null;
                error = $"Create clip cache failed: {ex.Message}";
                return false;
            }
        }

        private static void TrimGeneratedClipsToLimit(IReadOnlyCollection<string> protectedPaths, int maxCount)
        {
            maxCount = Mathf.Max(1, maxCount);
            if (!AssetDatabase.IsValidFolder(GeneratedClipFolder))
            {
                return;
            }

            string[] clipGuids = AssetDatabase.FindAssets("t:AnimationClip", new[] { GeneratedClipFolder });
            if (clipGuids == null || clipGuids.Length == 0)
            {
                return;
            }

            var clipPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in clipGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                clipPathSet.Add(path);
            }

            var clipPaths = new List<string>(clipPathSet);
            if (clipPaths.Count <= maxCount)
            {
                return;
            }

            clipPaths.Sort(CompareGeneratedClipPathsByAgeOldestFirst);
            bool deletedAny = false;
            for (int i = 0; i < clipPaths.Count && clipPaths.Count > maxCount; i++)
            {
                string candidatePath = clipPaths[i];
                if (protectedPaths != null && protectedPaths.Contains(candidatePath))
                {
                    continue;
                }

                if (IsAssetReferencedByOtherAssets(candidatePath))
                {
                    Debug.Log($"[Kimodo] Generated clip cleanup skipped referenced clip: {candidatePath}");
                    continue;
                }

                if (AssetDatabase.DeleteAsset(candidatePath))
                {
                    deletedAny = true;
                    clipPaths.RemoveAt(i);
                    i--;
                }
            }

            if (deletedAny)
            {
                AssetDatabase.SaveAssets();
            }
        }

        private static bool TryFindClipCache(AnimationClip targetClip, out AnimationClip cachedClip)
        {
            cachedClip = null;
            string cachePath = BuildMuscleCacheClipPath(targetClip);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return false;
            }

            cachedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(cachePath);
            if (cachedClip == null)
            {
                return false;
            }

            EnsureClipNameMatchesFileName(cachedClip, BuildMuscleCacheClipName(targetClip));
            return true;
        }

        private static void RemoveLegacyEmbeddedMuscleCache(AnimationClip targetClip)
        {
            string clipPath = AssetDatabase.GetAssetPath(targetClip);
            if (!IsGeneratedAnimationClipAssetPath(clipPath))
            {
                return;
            }

            string expectedCacheName = BuildMuscleCacheClipName(targetClip);
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(clipPath);
            var objectsToRemove = new List<UnityEngine.Object>();
            for (int i = 0; i < assets.Length; i++)
            {
                AnimationClip cacheClip = assets[i] as AnimationClip;
                if (cacheClip == null ||
                    cacheClip == targetClip ||
                    (!string.Equals(cacheClip.name, expectedCacheName, StringComparison.Ordinal) &&
                     !IsMuscleCacheClipName(cacheClip.name)))
                {
                    continue;
                }

                AddDestroyObject(objectsToRemove, cacheClip, targetClip);
            }

            DestroyCacheObjects(clipPath, objectsToRemove);
        }

        private static void EnsureClipNameMatchesFileName(AnimationClip clip, string expectedName)
        {
            if (clip == null || string.IsNullOrWhiteSpace(expectedName) || string.Equals(clip.name, expectedName, StringComparison.Ordinal))
            {
                return;
            }

            clip.name = expectedName;
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
        }

        private static string BuildGeneratedAnimationAssetName(string assetName)
        {
            string safeName = KimodoRuntimeUtility.SanitizeName(assetName, "KimodoClip");
            if (safeName.StartsWith(GeneratedClipNamePrefix, StringComparison.Ordinal))
            {
                return safeName;
            }

            return $"{GeneratedClipNamePrefix}{safeName}";
        }

        private static bool IsGeneratedAnimationClipAssetPath(string assetPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath) &&
                assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                assetPath.StartsWith(GeneratedClipFolder + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMuscleCacheClipPath(AnimationClip targetClip)
        {
            string cacheName = BuildMuscleCacheClipName(targetClip);
            if (string.IsNullOrWhiteSpace(cacheName))
            {
                return string.Empty;
            }

            return $"{GeneratedClipFolder}/{cacheName}.anim";
        }

        private static bool IsMuscleCacheClipName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                (name.EndsWith(MuscleCacheClipSuffix, StringComparison.Ordinal) ||
                 name.EndsWith(LegacyMuscleCacheClipSuffix, StringComparison.Ordinal) ||
                 name.EndsWith(LegacyMuscleCacheSuffix, StringComparison.Ordinal));
        }

        private static string BuildMuscleCacheClipName(AnimationClip targetClip)
        {
            string clipName = targetClip != null ? targetClip.name : "KimodoClip";
            return SanitizeAssetFileName($"{clipName}{MuscleCacheClipSuffix}", "KimodoClip_musclecache");
        }

        private static string SanitizeAssetFileName(string value, string defaultName)
        {
            string safeName = KimodoRuntimeUtility.SanitizeName(value, defaultName);
            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalidChars.Length; i++)
            {
                safeName = safeName.Replace(invalidChars[i], '_');
            }

            return string.IsNullOrWhiteSpace(safeName) ? defaultName : safeName;
        }

        private static string BuildGeneratedPreviewControllerName()
        {
            return $"{GeneratedClipNamePrefix}Preview_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        }

        private static string BuildAvatarCachePath(GameObject avatarRoot)
        {
            string safeName = KimodoRuntimeUtility.SanitizeName(avatarRoot != null ? avatarRoot.name : "Avatar", "Avatar");
            int hash = ComputeHierarchyHash(avatarRoot != null ? avatarRoot.transform : null);
            return $"{GeneratedAvatarFolder}/{safeName}_{hash:X8}.asset";
        }

        private static int CompareGeneratedClipPathsByAgeOldestFirst(string leftPath, string rightPath)
        {
            string leftName = Path.GetFileNameWithoutExtension(leftPath) ?? string.Empty;
            string rightName = Path.GetFileNameWithoutExtension(rightPath) ?? string.Empty;
            string leftStamp = leftName.StartsWith(GeneratedClipNamePrefix, StringComparison.Ordinal)
                ? leftName.Substring(GeneratedClipNamePrefix.Length)
                : leftName;
            string rightStamp = rightName.StartsWith(GeneratedClipNamePrefix, StringComparison.Ordinal)
                ? rightName.Substring(GeneratedClipNamePrefix.Length)
                : rightName;
            return string.Compare(leftStamp, rightStamp, StringComparison.Ordinal);
        }

        private static bool IsAssetReferencedByOtherAssets(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            string[] allAssets = AssetDatabase.GetAllAssetPaths();
            foreach (string path in allAssets)
            {
                if (string.IsNullOrWhiteSpace(path) ||
                    string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase) ||
                    !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] dependencies;
                try
                {
                    dependencies = AssetDatabase.GetDependencies(path, false);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < dependencies.Length; i++)
                {
                    if (string.Equals(dependencies[i], assetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
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

        private static void ScheduleGeneratedClipTrim(AnimationClip protectedClip)
        {
            string protectedPath = protectedClip != null ? AssetDatabase.GetAssetPath(protectedClip) : string.Empty;
            if (!string.IsNullOrWhiteSpace(protectedPath))
            {
                PendingProtectedClipPaths.Add(protectedPath);
            }

            if (generatedClipTrimScheduled)
            {
                return;
            }

            generatedClipTrimScheduled = true;
            EditorApplication.update += OnGeneratedClipTrimEditorUpdate;
        }

        private static void OnGeneratedClipTrimEditorUpdate()
        {
            EditorApplication.update -= OnGeneratedClipTrimEditorUpdate;
            generatedClipTrimScheduled = false;

            try
            {
                int maxCount = Mathf.Clamp(
                    KimodoPlayableClipGenerationSettings.instance.MaxGeneratedClips,
                    KimodoPlayableClipGenerationSettings.MinGeneratedClipsLimit,
                    KimodoPlayableClipGenerationSettings.MaxGeneratedClipsLimit);
                TrimGeneratedClipsToLimit(PendingProtectedClipPaths, maxCount);
            }
            finally
            {
                PendingProtectedClipPaths.Clear();
            }
        }

        private static void AddDestroyObject(List<UnityEngine.Object> objects, UnityEngine.Object value, AnimationClip targetClip)
        {
            if (value == null || value == targetClip || objects.Contains(value))
            {
                return;
            }

            objects.Add(value);
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

        private static void DestroyCacheObjects(string clipPath, List<UnityEngine.Object> objects)
        {
            if (objects == null || objects.Count == 0)
            {
                return;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(objects[i], true);
                }
            }

            AssetDatabase.ImportAsset(clipPath);
            AssetDatabase.SaveAssets();
        }
    }
}
