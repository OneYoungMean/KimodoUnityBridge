using KimodoBridge;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorClipWritebackService
    {
        private const string GeneratedClipFolder = "Assets/KimodoGeneratedClips";
        private const string GeneratedClipNamePrefix = "Kimodo_";

        public AnimationClip CreateGeneratedAnimationClipAsset()
        {
            var newAnimationClip = new AnimationClip
            {
                name = $"{GeneratedClipNamePrefix}{DateTime.Now:yyyyMMdd_HHmmss_fff}"
            };

            if (!AssetDatabase.IsValidFolder(GeneratedClipFolder))
            {
                AssetDatabase.CreateFolder("Assets", "KimodoGeneratedClips");
            }

            string fileName = $"{newAnimationClip.name}.anim";
            string savePath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedClipFolder}/{fileName}");
            AssetDatabase.CreateAsset(newAnimationClip, savePath);
            EditorUtility.SetDirty(newAnimationClip);
            AssetDatabase.SaveAssets();
            return newAnimationClip;
        }

        public void CreateAndAssignNewAnimationClip(KimodoPlayableClip clip)
        {
            clip.clip = CreateGeneratedAnimationClipAsset();
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(clip.clip);
            AssetDatabase.SaveAssets();
        }

        public void BakeMotionJsonToClip(AnimationClip targetClip, string motionJson, string modelName, out string error)
        {
            error = string.Empty;
            if (targetClip == null || string.IsNullOrWhiteSpace(motionJson))
            {
                error = "Clip / motion json is missing.";
                return;
            }

            bool ok = KimodoRetargetToolsEditor.BakeIntoClip(
                targetClip: targetClip,
                motionJson: motionJson,
                skeletonType: KimodoPlayableClip.ResolveBakeSkeletonTypeFromModelName(modelName),
                modelName: modelName,
                curveFilterOptions: null,
                out error);

            if (!ok)
            {
                Debug.LogWarning($"[Kimodo] Bake failed: {error}");
                return;
            }

            EditorUtility.SetDirty(targetClip);
            AssetDatabase.SaveAssets();
        }

        public string CreateGeneratedClipFromMotionJson(string motionJson, string modelName, string outputFolderAssetPath, string clipNamePrefix)
        {
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new InvalidOperationException("motionJson is empty.");
            }

            string outputFolder = string.IsNullOrWhiteSpace(outputFolderAssetPath)
                ? GeneratedClipFolder
                : outputFolderAssetPath.Trim();
            if (!outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Output folder must be under Assets/.");
            }

            EnsureAssetFolder(outputFolder);

            string safePrefix = string.IsNullOrWhiteSpace(clipNamePrefix)
                ? GeneratedClipNamePrefix
                : clipNamePrefix.Trim();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string clipName = $"{safePrefix}{stamp}";
            var clip = new AnimationClip { name = clipName };
            string clipPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{clipName}.anim");

            AssetDatabase.CreateAsset(clip, clipPath);
            bool baked = KimodoRetargetToolsEditor.BakeIntoClip(
                clip,
                motionJson,
                KimodoPlayableClip.ResolveBakeSkeletonTypeFromModelName(modelName),
                modelName,
                null,
                out string bakeError);

            if (!baked)
            {
                AssetDatabase.DeleteAsset(clipPath);
                throw new InvalidOperationException($"Failed to bake generated clip: {bakeError}");
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar samplerAvatar, out string avatarError))
            {
                AssetDatabase.DeleteAsset(clipPath);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(avatarError) ? "Failed to resolve sampler avatar." : avatarError);
            }

            if (!KimodoBridge.Editor.KimodoRetargetToolsEditor.TryFilterClipInPlace(clip, samplerAvatar, null, out bakeError))
            {
                AssetDatabase.DeleteAsset(clipPath);
                throw new InvalidOperationException($"Failed to filter generated clip: {bakeError}");
            }

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return clipPath;
        }

        public bool BakeMotionJsonToPlayableClip(KimodoPlayableClip clip, string prompt, string motionJson, out string error)
        {
            error = string.Empty;
            if (clip == null || clip.clip == null || string.IsNullOrWhiteSpace(motionJson))
            {
                error = "Clip / motion json is missing.";
                return false;
            }

            ApplyGeneratedMetadata(clip, prompt, motionJson);
            bool ok = KimodoRetargetToolsEditor.BakeIntoClip(
                targetClip: clip.clip,
                motionJson: motionJson,
                skeletonType: clip.InferredSkeletonType,
                modelName: clip.bridgeModelName,
                curveFilterOptions: null,
                out error);

            if (!ok)
            {
                Debug.LogWarning($"[Kimodo] Bake failed: {error}");
                return false;
            }

            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(clip.clip);
            AssetDatabase.SaveAssets();
            return true;
        }

        public void TrimGeneratedClipsToLimit(KimodoPlayableClip clip)
        {
            int maxCount = Mathf.Clamp(
                KimodoPlayableClipGenerationSettings.instance.MaxGeneratedClips,
                KimodoPlayableClipGenerationSettings.MinGeneratedClipsLimit,
                KimodoPlayableClipGenerationSettings.MaxGeneratedClipsLimit);

            if (!AssetDatabase.IsValidFolder(GeneratedClipFolder))
            {
                return;
            }

            string[] clipGuids = AssetDatabase.FindAssets("t:AnimationClip", new[] { GeneratedClipFolder });
            if (clipGuids == null || clipGuids.Length <= maxCount)
            {
                return;
            }

            var clipPaths = new System.Collections.Generic.List<string>(clipGuids.Length);
            foreach (string guid in clipGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                if (!name.StartsWith(GeneratedClipNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                clipPaths.Add(path);
            }

            if (clipPaths.Count <= maxCount)
            {
                return;
            }

            clipPaths.Sort(CompareGeneratedClipPathsByAgeOldestFirst);
            string activeClipPath = clip.clip != null ? AssetDatabase.GetAssetPath(clip.clip) : string.Empty;

            foreach (string candidatePath in clipPaths)
            {
                if (!string.IsNullOrWhiteSpace(activeClipPath) &&
                    string.Equals(candidatePath, activeClipPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsAssetReferencedByOtherAssets(candidatePath))
                {
                    Debug.Log($"[Kimodo] Generated clip cleanup skipped referenced clip: {candidatePath}");
                    return;
                }

                if (AssetDatabase.DeleteAsset(candidatePath))
                {
                    AssetDatabase.SaveAssets();
                }

                return;
            }
        }

        private static void ApplyGeneratedMetadata(KimodoPlayableClip clip, string prompt, string motionJson)
        {
            JObject obj = JObject.Parse(motionJson);
            clip.lastGeneratedPrompt = prompt ?? string.Empty;
            clip.isGenerated = true;
            clip.frameCount = obj.Value<int?>("num_frames") ?? 0;
            clip.jointCount = obj.Value<int?>("num_joints") ?? 0;
            clip.fps = Mathf.RoundToInt(KimodoPlayableClip.FIXED_FRAME_RATE);
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

        private static void EnsureAssetFolder(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath))
            {
                return;
            }

            string[] parts = assetFolderPath.Split('/');
            if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid asset folder path.");
            }

            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
    }
}
