using Newtonsoft.Json.Linq;
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.GenerationPipeline
{
    internal sealed class KimodoEditorClipWritebackService
    {
        private const string GeneratedClipFolder = "Assets/KimodoGeneratedClips";
        private const string GeneratedClipNamePrefix = "Kimodo_";

        public void CreateAndAssignNewAnimationClip(KimodoPlayableClip clip)
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

            clip.clip = newAnimationClip;
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(clip.clip);
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
            bool baked = KimodoAnimationBaker.BakeIntoClip(
                clip,
                motionJson,
                KimodoBakeSkeletonType.SOMA,
                modelName,
                null,
                out string bakeError);

            if (!baked)
            {
                AssetDatabase.DeleteAsset(clipPath);
                throw new InvalidOperationException($"Failed to bake generated clip: {bakeError}");
            }

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return clipPath;
        }

        public void ApplyMotionJsonToClip(KimodoPlayableClip clip, string prompt, string motionJson)
        {
            JObject obj = JObject.Parse(motionJson);

            clip.motionData = motionJson;
            clip.lastGeneratedPrompt = prompt ?? string.Empty;
            clip.isGenerated = true;

            clip.frameCount = obj.Value<int?>("num_frames") ?? 0;
            clip.jointCount = obj.Value<int?>("num_joints") ?? 0;
            clip.fps = obj.Value<int?>("fps") ?? 30;

            if (obj["joint_names"] is JArray names)
            {
                string[] arr = new string[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    arr[i] = names[i]?.ToString();
                }

                clip.jointNames = arr;
            }
            else
            {
                clip.jointNames = null;
            }

            if (obj["joints"] is JArray joints)
            {
                float[] arr = new float[joints.Count];
                for (int i = 0; i < joints.Count; i++)
                {
                    arr[i] = joints[i] != null ? joints[i].Value<float>() : 0f;
                }

                clip.motionPositions = arr;
            }
            else
            {
                clip.motionPositions = null;
            }

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
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

        public bool BakeCurrentMotionData(KimodoPlayableClip clip, bool hasValidRetargetAvatar, out string error)
        {
            error = string.Empty;
            if (clip == null || clip.clip == null || string.IsNullOrWhiteSpace(clip.motionData))
            {
                error = "Clip / motionData is missing.";
                return false;
            }

            bool willRetargetPipeline = hasValidRetargetAvatar;
            KimodoCurveFilterOptions bakeFilterOptions = clip.curveFilterOptions;
            if (willRetargetPipeline && clip.curveFilterOptions != null)
            {
                bakeFilterOptions = new KimodoCurveFilterOptions
                {
                    enabled = clip.curveFilterOptions.enabled,
                    positionError = 0f,
                    rotationError = 0f,
                    floatError = 0f,
                    ensureQuaternionContinuity = false
                };
            }

            bool ok = KimodoAnimationBaker.BakeIntoClip(
                targetClip: clip.clip,
                motionJson: clip.motionData,
                skeletonType: clip.InferredSkeletonType,
                modelName: clip.bridgeModelName,
                curveFilterOptions: bakeFilterOptions,
                out error);

            if (!ok)
            {
                Debug.LogWarning($"[Kimodo] Bake failed: {error}");
                return false;
            }

            clip.isGenerated = true;
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(clip.clip);
            AssetDatabase.SaveAssets();
            return true;
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
