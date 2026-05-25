using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

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
            RefreshTimelinePreviewGraph(clip);
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

        public bool BakeCurrentMotionData(KimodoPlayableClip clip, out string error)
        {
            error = string.Empty;
            if (clip == null || clip.clip == null || string.IsNullOrWhiteSpace(clip.motionData))
            {
                error = "Clip / motionData is missing.";
                return false;
            }

            bool willRetargetPipeline = clip.autoRetargetOnBinding || (!clip.autoRetargetOnBinding && clip.CustomRetargetAvatar != null);
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

        public void RefreshTimelinePreviewGraph(KimodoPlayableClip clip)
        {
            if (clip == null || TimelineEditor.inspectedAsset == null)
            {
                return;
            }

            if (!TryGetTimelinePreviewMode(out bool isPreviewMode) || !isPreviewMode)
            {
                return;
            }

            if (KimodoTimelineClipResolver.FindTimelineClipForAsset(clip) == null)
            {
                return;
            }

            if (!TrySetTimelinePreviewMode(false))
            {
                return;
            }

            TrySetTimelinePreviewMode(true);
            TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static bool TryGetTimelinePreviewMode(out bool previewMode)
        {
            previewMode = false;
            object timelineState = GetTimelineEditorState();
            if (timelineState == null)
            {
                return false;
            }

            Type stateType = timelineState.GetType();
            var previewModeProperty = stateType.GetProperty("previewMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (previewModeProperty != null && previewModeProperty.PropertyType == typeof(bool))
            {
                previewMode = (bool)previewModeProperty.GetValue(timelineState, null);
                return true;
            }

            var previewModeField = stateType.GetField("previewMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (previewModeField != null && previewModeField.FieldType == typeof(bool))
            {
                previewMode = (bool)previewModeField.GetValue(timelineState);
                return true;
            }

            return false;
        }

        private static bool TrySetTimelinePreviewMode(bool value)
        {
            object timelineState = GetTimelineEditorState();
            if (timelineState == null)
            {
                return false;
            }

            Type stateType = timelineState.GetType();
            var previewModeProperty = stateType.GetProperty("previewMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (previewModeProperty != null && previewModeProperty.PropertyType == typeof(bool) && previewModeProperty.CanWrite)
            {
                previewModeProperty.SetValue(timelineState, value, null);
                return true;
            }

            var previewModeField = stateType.GetField("previewMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (previewModeField != null && previewModeField.FieldType == typeof(bool))
            {
                previewModeField.SetValue(timelineState, value);
                return true;
            }

            return false;
        }

        private static object GetTimelineEditorState()
        {
            Type timelineEditorType = typeof(TimelineEditor);
            const System.Reflection.BindingFlags StaticMemberFlags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            var stateProperty = timelineEditorType.GetProperty("state", StaticMemberFlags);
            if (stateProperty != null)
            {
                return stateProperty.GetValue(null, null);
            }

            var stateField = timelineEditorType.GetField("state", StaticMemberFlags);
            if (stateField != null)
            {
                return stateField.GetValue(null);
            }

            return null;
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
    }
}
