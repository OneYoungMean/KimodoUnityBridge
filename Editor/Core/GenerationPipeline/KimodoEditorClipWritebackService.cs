using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorClipWritebackService
    {
        internal const string GeneratedClipFolder = "Assets/KimodoGeneratedClips";
        internal const string CacheClipFolder = GeneratedClipFolder + "/Cache";
        internal const string GeneratedClipNamePrefix = "Kimodo_";
        internal const string InvalidCachePrefix = "invalid_";
        private const string MuscleCacheNameSuffix = "-muscle-cache";
        private const string BoneCacheNameMarker = "-bone-";
        private const string CacheNameSuffix = "-cache";
        private const string GeneratedPreviewControllerFolder = GeneratedClipFolder + "/PreviewControllers";

        private static readonly HashSet<string> PendingProtectedClipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool generatedClipTrimScheduled;

        internal readonly struct NamedClipCacheCleanupSummary
        {
            internal readonly int CandidateCount;
            internal readonly int ReferencedCount;
            internal readonly int DeletedCount;
            internal readonly int FailedCount;

            internal NamedClipCacheCleanupSummary(int candidateCount, int referencedCount, int deletedCount, int failedCount)
            {
                CandidateCount = Mathf.Max(0, candidateCount);
                ReferencedCount = Mathf.Max(0, referencedCount);
                DeletedCount = Mathf.Max(0, deletedCount);
                FailedCount = Mathf.Max(0, failedCount);
            }
        }

        public static AnimationClip CreateGeneratedAnimationClipAsset(string assetName)
        {
            return CreateAnimationClipAsset(assetName, GeneratedClipFolder, trackForTrim: false);
        }

        public static AnimationClip CreateGeneratedCacheAnimationClipAsset(string assetName)
        {
            return CreateAnimationClipAsset(assetName, CacheClipFolder, trackForTrim: true);
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

            string cachePath = $"{CacheClipFolder}/{safeCacheName}.anim";
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

            string cachePath = $"{CacheClipFolder}/{safeCacheName}.anim";
            try
            {
                EnsureFolderExists(CacheClipFolder);
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
            string invalidPath = AssetDatabase.GenerateUniqueAssetPath($"{CacheClipFolder}/{invalidName}.anim");
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

            if (!KimodoRetargetEditorCacheUtility.ClipHasContent(sourceClip))
            {
                error = "Source clip has no curve content.";
                return false;
            }

            string cacheName = exportMuscleClip
                ? BuildNamedClipCacheName(sourceClip, isMuscleClip: true, targetAvatar: null)
                : BuildNamedClipCacheName(sourceClip, isMuscleClip: false, targetAvatar);
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

        internal static bool TryDeleteUnreferencedNamedClipCaches(out NamedClipCacheCleanupSummary summary, out string error)
        {
            summary = new NamedClipCacheCleanupSummary(0, 0, 0, 0);
            error = string.Empty;

            if (!AssetDatabase.IsValidFolder(CacheClipFolder))
            {
                return true;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Kimodo Clip Cache", "Collecting cache clip candidates...", 0.05f);
                List<string> candidatePaths = CollectClearableKimodoGeneratedClipAssetPaths();
                if (candidatePaths.Count == 0)
                {
                    return true;
                }

                var candidatePathSet = new HashSet<string>(candidatePaths, StringComparer.OrdinalIgnoreCase);

                EditorUtility.DisplayProgressBar("Kimodo Clip Cache", "Scanning project asset references...", 0.25f);
                HashSet<string> referencedPaths = CollectReferencedNamedClipCacheAssetPaths(candidatePathSet);

                int deletedCount = 0;
                int failedCount = 0;
                int totalToDelete = 0;
                for (int i = 0; i < candidatePaths.Count; i++)
                {
                    if (!referencedPaths.Contains(candidatePaths[i]))
                    {
                        totalToDelete++;
                    }
                }

                if (totalToDelete > 0)
                {
                    int processedDeleteCount = 0;
                    for (int i = 0; i < candidatePaths.Count; i++)
                    {
                        string candidatePath = candidatePaths[i];
                        if (referencedPaths.Contains(candidatePath))
                        {
                            continue;
                        }

                        float progress = 0.8f + (0.2f * processedDeleteCount / Mathf.Max(1, totalToDelete));
                        string clipName = Path.GetFileNameWithoutExtension(candidatePath) ?? candidatePath;
                        EditorUtility.DisplayProgressBar("Kimodo Clip Cache", $"Deleting unreferenced cache clip '{clipName}'...", progress);

                        if (AssetDatabase.DeleteAsset(candidatePath))
                        {
                            deletedCount++;
                            PendingProtectedClipPaths.Remove(candidatePath);
                        }
                        else
                        {
                            failedCount++;
                        }

                        processedDeleteCount++;
                    }
                }

                if (deletedCount > 0)
                {
                    FlushWritebackAssets();
                    AssetDatabase.Refresh();
                }

                summary = new NamedClipCacheCleanupSummary(
                    candidatePaths.Count,
                    referencedPaths.Count,
                    deletedCount,
                    failedCount);

                if (failedCount > 0)
                {
                    error = $"Delete unreferenced cache clips finished with {failedCount} failure(s).";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Clear unreferenced clip cache failed: {ex.Message}";
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void TrimGeneratedClipsToLimit(IReadOnlyCollection<string> protectedPaths, int maxCount)
        {
            maxCount = Mathf.Max(1, maxCount);
            if (!AssetDatabase.IsValidFolder(CacheClipFolder))
            {
                return;
            }

            string[] clipGuids = AssetDatabase.FindAssets("t:AnimationClip", new[] { CacheClipFolder });
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

        private static AnimationClip CreateAnimationClipAsset(string assetName, string folderPath, bool trackForTrim)
        {
            var newAnimationClip = new AnimationClip
            {
                name = BuildGeneratedAnimationAssetName(assetName)
            };

            EnsureFolderExists(folderPath);

            string fileName = $"{newAnimationClip.name}.anim";
            string savePath = AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/{fileName}");
            AssetDatabase.CreateAsset(newAnimationClip, savePath);
            EditorUtility.SetDirty(newAnimationClip);
            FlushWritebackAssets();
            if (trackForTrim)
            {
                ScheduleGeneratedClipTrim(newAnimationClip);
            }

            return newAnimationClip;
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
            if (!IsCacheClipAssetPath(assetPath))
            {
                return false;
            }

            int lastSlashIndex = assetPath.LastIndexOf('/');
            if (lastSlashIndex <= 0)
            {
                return false;
            }

            string parentFolder = assetPath.Substring(0, lastSlashIndex);
            if (!string.Equals(parentFolder, CacheClipFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string clipName = Path.GetFileNameWithoutExtension(assetPath) ?? string.Empty;
            return IsValidNamedClipCacheName(clipName);
        }

        private static List<string> CollectClearableKimodoGeneratedClipAssetPaths()
        {
            var clipPaths = new List<string>();
            string[] clipGuids = AssetDatabase.FindAssets("t:AnimationClip", new[] { CacheClipFolder });
            if (clipGuids == null || clipGuids.Length == 0)
            {
                return clipPaths;
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < clipGuids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(clipGuids[i]);
                if (!IsClearableKimodoGeneratedClipAssetPath(assetPath) || !seenPaths.Add(assetPath))
                {
                    continue;
                }

                clipPaths.Add(assetPath);
            }

            return clipPaths;
        }

        private static HashSet<string> CollectReferencedNamedClipCacheAssetPaths(HashSet<string> candidatePathSet)
        {
            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (candidatePathSet == null || candidatePathSet.Count == 0)
            {
                return referencedPaths;
            }

            string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
            var dependencyRoots = new List<string>(allAssetPaths.Length);
            for (int i = 0; i < allAssetPaths.Length; i++)
            {
                string assetPath = allAssetPaths[i];
                if (!IsDependencyScanRootAssetPath(assetPath) || candidatePathSet.Contains(assetPath))
                {
                    continue;
                }

                dependencyRoots.Add(assetPath);
            }

            if (dependencyRoots.Count > 0)
            {
                string[] dependencies = AssetDatabase.GetDependencies(dependencyRoots.ToArray(), true);
                for (int i = 0; i < dependencies.Length; i++)
                {
                    string dependencyPath = dependencies[i];
                    if (candidatePathSet.Contains(dependencyPath))
                    {
                        referencedPaths.Add(dependencyPath);
                    }
                }
            }

            EditorUtility.DisplayProgressBar("Kimodo Clip Cache", "Scanning loaded scene references...", 0.65f);
            CollectReferencedNamedClipCacheAssetPathsFromLoadedScenes(candidatePathSet, referencedPaths);
            return referencedPaths;
        }

        private static bool IsClearableKimodoGeneratedClipAssetPath(string assetPath)
        {
            if (!IsCacheClipAssetPath(assetPath))
            {
                return false;
            }

            int lastSlashIndex = assetPath.LastIndexOf('/');
            if (lastSlashIndex <= 0)
            {
                return false;
            }

            string parentFolder = assetPath.Substring(0, lastSlashIndex);
            if (!string.Equals(parentFolder, CacheClipFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string clipName = Path.GetFileNameWithoutExtension(assetPath) ?? string.Empty;
            return clipName.StartsWith(GeneratedClipNamePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCacheClipAssetPath(string assetPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath) &&
                assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                assetPath.StartsWith(CacheClipFolder + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectReferencedNamedClipCacheAssetPathsFromLoadedScenes(
            HashSet<string> candidatePathSet,
            HashSet<string> referencedPaths)
        {
            int sceneCount = EditorSceneManager.sceneCount;
            for (int sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                Scene scene = EditorSceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                GameObject[] rootObjects = scene.GetRootGameObjects();
                if (rootObjects == null || rootObjects.Length == 0)
                {
                    continue;
                }

                Object[] dependencies = EditorUtility.CollectDependencies(rootObjects);
                for (int i = 0; i < dependencies.Length; i++)
                {
                    string assetPath = AssetDatabase.GetAssetPath(dependencies[i]);
                    if (!string.IsNullOrWhiteSpace(assetPath) && candidatePathSet.Contains(assetPath))
                    {
                        referencedPaths.Add(assetPath);
                    }
                }
            }
        }

        private static bool IsDependencyScanRootAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) ||
                assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                AssetDatabase.IsValidFolder(assetPath))
            {
                return false;
            }

            return assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
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

        internal static void EnsureFolderExists(string folderPath)
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
