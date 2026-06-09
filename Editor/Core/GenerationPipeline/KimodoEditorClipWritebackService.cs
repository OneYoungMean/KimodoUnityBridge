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
        internal const string InvalidCachePrefix = "invalid_";
        private const string MuscleCacheNameSuffix = "-muscle-cache";
        private const string BoneCacheNameMarker = "-bone-";
        private const string CacheNameSuffix = "-cache";
        private const string GeneratedAvatarFolder = GeneratedClipFolder + "/Avatars";
        private const string GeneratedPreviewControllerFolder = GeneratedClipFolder + "/PreviewControllers";

        private static readonly HashSet<string> PendingProtectedClipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool generatedClipTrimScheduled;

        public static AnimationClip CreateGeneratedAnimationClipAsset(string assetName)
        {
            var newAnimationClip = new AnimationClip
            {
                name = BuildGeneratedAnimationAssetName(assetName)
            };

            EnsureFolderExists(GeneratedClipFolder);

            string fileName = $"{newAnimationClip.name}.anim";
            string savePath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedClipFolder}/{fileName}");
            AssetDatabase.CreateAsset(newAnimationClip, savePath);
            EditorUtility.SetDirty(newAnimationClip);
            FlushWritebackAssets();
            ScheduleGeneratedClipTrim(newAnimationClip);
            return newAnimationClip;
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
                FlushWritebackAssets();
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
                FlushWritebackAssets();
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
                FlushWritebackAssets();
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

        public static bool TryLoadNamedClipCache(string cacheName, out AnimationClip cachedClip, out string error)
        {
            cachedClip = null;
            error = string.Empty;

            string safeCacheName = SanitizeAssetFileName(cacheName, "KimodoClip_cache");
            if (string.IsNullOrWhiteSpace(safeCacheName))
            {
                error = "Cache clip name is empty.";
                return false;
            }

            if (safeCacheName.StartsWith(InvalidCachePrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "Invalid cache names cannot be loaded.";
                return false;
            }

            string cachePath = $"{GeneratedClipFolder}/{safeCacheName}.anim";
            cachedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(cachePath);
            if (cachedClip == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(cachedClip.name) ||
                cachedClip.name.StartsWith(InvalidCachePrefix, StringComparison.OrdinalIgnoreCase))
            {
                cachedClip = null;
                return false;
            }

            EnsureClipNameMatchesFileName(cachedClip, safeCacheName);
            return true;
        }

        public static bool TryGetOrCreateNamedClipCache(
            string cacheName,
            float frameRate,
            out AnimationClip cachedClip,
            out string error)
        {
            cachedClip = null;
            error = string.Empty;

            string safeCacheName = SanitizeAssetFileName(cacheName, "KimodoClip_cache");
            if (string.IsNullOrWhiteSpace(safeCacheName))
            {
                error = "Cache clip name is empty.";
                return false;
            }

            string cachePath = $"{GeneratedClipFolder}/{safeCacheName}.anim";
            try
            {
                EnsureFolderExists(GeneratedClipFolder);
                cachedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(cachePath);
                if (cachedClip == null)
                {
                    cachedClip = new AnimationClip
                    {
                        name = safeCacheName,
                        legacy = false,
                        frameRate = frameRate > 0f ? frameRate : KimodoPlayableClip.FIXED_FRAME_RATE
                    };

                    AssetDatabase.CreateAsset(cachedClip, cachePath);
                    EditorUtility.SetDirty(cachedClip);
                    FlushWritebackAssets();
                    AssetDatabase.Refresh();
                }

                cachedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(cachePath) ?? cachedClip;
                if (cachedClip != null && frameRate > 0f && !Mathf.Approximately(cachedClip.frameRate, frameRate))
                {
                    cachedClip.frameRate = frameRate;
                    EditorUtility.SetDirty(cachedClip);
                }

                EnsureClipNameMatchesFileName(cachedClip, safeCacheName);
                ScheduleGeneratedClipTrim(cachedClip);
                return cachedClip != null;
            }
            catch (Exception ex)
            {
                if (cachedClip != null && string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(cachedClip)))
                {
                    UnityEngine.Object.DestroyImmediate(cachedClip);
                }

                cachedClip = null;
                error = $"Create named clip cache failed: {ex.Message}";
                return false;
            }
        }

        public static bool TryInvalidateNamedClipCache(string cacheName, out string error)
        {
            error = string.Empty;

            if (!TryLoadNamedClipCache(cacheName, out AnimationClip cachedClip, out error))
            {
                error = string.Empty;
                return true;
            }

            string assetPath = AssetDatabase.GetAssetPath(cachedClip);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return true;
            }

            string invalidName = SanitizeAssetFileName($"{InvalidCachePrefix}{cacheName}", "invalid_KimodoClip_cache");
            string invalidPath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedClipFolder}/{invalidName}.anim");
            string moveError = AssetDatabase.MoveAsset(assetPath, invalidPath);
            if (!string.IsNullOrWhiteSpace(moveError))
            {
                error = $"Invalidate named clip cache failed: {moveError}";
                return false;
            }

            AnimationClip movedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(invalidPath);
            if (movedClip != null)
            {
                movedClip.name = invalidName;
                EditorUtility.SetDirty(movedClip);
            }

            return true;
        }

        public static string BuildMuscleClipCacheName(AnimationClip sourceClip)
        {
            return BuildNamedClipCacheName(sourceClip, isMuscleClip: true, targetAvatar: null);
        }

        public static string BuildBoneClipCacheName(AnimationClip sourceClip, Avatar targetAvatar)
        {
            return BuildNamedClipCacheName(sourceClip, isMuscleClip: false, targetAvatar);
        }

        public static bool TryMaterializeGeneratedClipCache(
            AnimationClip sourceClip,
            bool exportMuscleClip,
            Avatar targetAvatar,
            bool forceRefresh,
            out AnimationClip cachedClip,
            out string error)
        {
            cachedClip = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!HasClipContent(sourceClip))
            {
                error = "Source clip has no curve content.";
                return false;
            }

            string cacheName = exportMuscleClip
                ? BuildMuscleClipCacheName(sourceClip)
                : BuildBoneClipCacheName(sourceClip, targetAvatar);
            if (string.IsNullOrWhiteSpace(cacheName))
            {
                error = "Cache clip name is empty.";
                return false;
            }

            if (forceRefresh && !TryInvalidateNamedClipCache(cacheName, out error))
            {
                return false;
            }

            float frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
            if (!TryGetOrCreateNamedClipCache(cacheName, frameRate, out cachedClip, out error))
            {
                return false;
            }

            try
            {
                KimodoEditorClipUtility.CopyClipData(sourceClip, cachedClip, forceNoLoopKeepY: false);
                cachedClip.legacy = sourceClip.legacy;
                EditorUtility.SetDirty(cachedClip);
                FlushWritebackAssets();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Materialize generated clip cache failed: {ex.Message}";
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
                if (!IsTrimmableNamedCacheClipAssetPath(path))
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

                if (AssetDatabase.DeleteAsset(candidatePath))
                {
                    deletedAny = true;
                    clipPaths.RemoveAt(i);
                    i--;
                }
            }

            if (deletedAny)
            {
                FlushWritebackAssets();
            }
        }

        private static bool HasClipContent(AnimationClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            return AnimationUtility.GetCurveBindings(clip).Length > 0 ||
                AnimationUtility.GetObjectReferenceCurveBindings(clip).Length > 0;
        }

        private static string BuildNamedClipCacheName(AnimationClip sourceClip, bool isMuscleClip, Avatar targetAvatar)
        {
            string sourceName = SanitizeAssetFileName(sourceClip != null ? sourceClip.name : "Clip", "Clip");
            if (isMuscleClip)
            {
                return $"{sourceName}{MuscleCacheNameSuffix}";
            }

            string avatarName = SanitizeAssetFileName(targetAvatar != null ? targetAvatar.name : "Avatar", "Avatar");
            return $"{sourceName}{BoneCacheNameMarker}{avatarName}{CacheNameSuffix}";
        }

        internal static void FlushWritebackAssets()
        {
            AssetDatabase.SaveAssets();
        }

        private static void EnsureClipNameMatchesFileName(AnimationClip clip, string expectedName)
        {
            if (clip == null || string.IsNullOrWhiteSpace(expectedName) || string.Equals(clip.name, expectedName, StringComparison.Ordinal))
            {
                return;
            }

            clip.name = expectedName;
            EditorUtility.SetDirty(clip);
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

        private static bool IsTrimmableNamedCacheClipAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) ||
                !assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                !assetPath.StartsWith(GeneratedClipFolder + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int lastSlashIndex = assetPath.LastIndexOf('/');
            if (lastSlashIndex <= 0)
            {
                return false;
            }

            string parentFolder = assetPath.Substring(0, lastSlashIndex);
            if (!string.Equals(parentFolder, GeneratedClipFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string clipName = Path.GetFileNameWithoutExtension(assetPath) ?? string.Empty;
            return IsValidNamedClipCacheName(clipName);
        }

        private static bool IsValidNamedClipCacheName(string clipName)
        {
            if (string.IsNullOrWhiteSpace(clipName) ||
                clipName.StartsWith(InvalidCachePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (clipName.EndsWith(MuscleCacheNameSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return clipName.Length > MuscleCacheNameSuffix.Length;
            }

            return clipName.EndsWith(CacheNameSuffix, StringComparison.OrdinalIgnoreCase) &&
                clipName.Contains(BoneCacheNameMarker);
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
