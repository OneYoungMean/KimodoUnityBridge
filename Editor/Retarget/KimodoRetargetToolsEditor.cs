#if UNITY_EDITOR
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    public static class KimodoRetargetToolsEditor
    {
        [Serializable]
        private sealed class MotionJsonData
        {
            public int num_frames;
            public int num_joints;
            public int fps;
            public string[] joint_names;
            public int[] joint_parents;
            public List<List<List<float>>> positions;
            public List<float> local_rot_quats;
        }

        public static bool BakeIntoClip(
            AnimationClip targetClip,
            string motionJson,
            KimodoBakeSkeletonType skeletonType,
            string modelName,
            KimodoCurveFilterOptions curveFilterOptions,
            out string error)
        {
            error = string.Empty;
            if (targetClip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            MotionJsonData data;
            try
            {
                data = ParseMotionJsonFlexible(motionJson);
            }
            catch (Exception e)
            {
                error = $"Failed to parse motionJson: {e.Message}";
                return false;
            }

            if (!ValidateData(data, out error))
            {
                return false;
            }

            if (skeletonType != KimodoBakeSkeletonType.SOMA &&
                skeletonType != KimodoBakeSkeletonType.G1 &&
                skeletonType != KimodoBakeSkeletonType.SMPLX)
            {
                error = "Unsupported bake skeleton type.";
                return false;
            }

            float fps = data.fps > 0 ? data.fps : KimodoPlayableClip.FIXED_FRAME_RATE;
            int positionFrames = data.positions != null ? data.positions.Count : 0;
            int frameHint = data.num_frames > 0 ? data.num_frames : positionFrames;
            int frameCount = positionFrames > 0
                ? Mathf.Min(frameHint, positionFrames)
                : Mathf.Max(2, frameHint);

            targetClip.ClearCurves();
            AnimationUtility.SetAnimationClipSettings(
                targetClip,
                new AnimationClipSettings
                {
                    loopTime = false,
                    keepOriginalPositionY = true
                });

            var rawClip = new AnimationClip
            {
                name = $"{targetClip.name}_Raw",
                legacy = false,
                frameRate = fps
            };

            BakeMotionCurvesDirect(rawClip, data, fps, frameCount, ResolveProfileAvatarForBake(modelName));
            KimodoEditorClipUtility.CopyClipData(rawClip, targetClip, forceNoLoopKeepY: true);
            UnityEngine.Object.DestroyImmediate(rawClip);

            _ = curveFilterOptions;
            _ = modelName;

            EditorUtility.SetDirty(targetClip);
            return true;
        }

        public static bool TryBakeMuscleClipToClip(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            AnimationClip targetClip,
            out string error)
        {
            error = string.Empty;
            if (sourceClip == null || targetClip == null)
            {
                error = "Source clip or target clip is null.";
                return false;
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (TryGetOrCreateEditorMuscleClipInternal(sourceClip, sourceAvatar, forceRefresh: false, out AnimationClip muscleClip, out float muscleFrameRate, out error))
            {
                if (!ReferenceEquals(targetClip, muscleClip))
                {
                    KimodoEditorClipUtility.CopyClipData(muscleClip, targetClip, forceNoLoopKeepY: true);
                }

                KimodoEditorClipUtility.ApplyMuscleClipSettings(targetClip);
                targetClip.legacy = false;
                targetClip.frameRate = muscleFrameRate > 0f
                    ? muscleFrameRate
                    : (sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE);

                EditorUtility.SetDirty(targetClip);
                return true;
            }

            return false;
        }

        internal static bool TryGetOrCreateEditorBoneClip(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            Avatar targetAvatar,
            bool forceRefresh,
            out AnimationClip boneCacheClip,
            out float frameRate,
            out string error)
        {
            boneCacheClip = null;
            frameRate = 0f;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar) || !KimodoRetargetCoreUtility.IsValidHumanoid(targetAvatar))
            {
                error = "Source or target avatar is null/invalid/non-humanoid.";
                return false;
            }

            string cacheName = KimodoRetargetEditorCacheUtility.BuildNamedCacheName(
                sourceClip,
                KimodoRetargetEditorCacheUtility.BoneCacheType,
                targetAvatar);
            frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;

            if (forceRefresh && !KimodoEditorClipWritebackService.TryInvalidateNamedClipCache(cacheName, out error))
            {
                return false;
            }

            if (KimodoRetargetEditorCacheUtility.TryLoadStrictNamedCache(cacheName, out boneCacheClip, out float cachedFrameRate, out error))
            {
                frameRate = cachedFrameRate;
                return true;
            }

            error = string.Empty;
            if (!KimodoEditorClipWritebackService.TryGetOrCreateNamedClipCache(cacheName, frameRate, out AnimationClip writableClip, out error))
            {
                return false;
            }

            if (!TryGetOrCreateEditorMuscleClipInternal(
                    sourceClip,
                    sourceAvatar,
                    forceRefresh,
                    out AnimationClip sourceHumanoidClip,
                    out float sourceFrameRate,
                    out error))
            {
                return false;
            }

            if (sourceFrameRate > 0f)
            {
                frameRate = sourceFrameRate;
            }

            SkeletonCache targetCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(targetAvatar, "KimodoRetargetToolsEditor_TargetBoneCache", out targetCache, out error))
                {
                    return false;
                }

                float duration = Mathf.Max(0f, sourceClip.length);
                int frameCount = KimodoRetargetSamplingUtility.ResolveFrameCount(duration, frameRate);
                if (!KimodoRetargetSamplingUtility.TryCollectBoneSamplesFromClip(
                        sourceHumanoidClip,
                        targetCache,
                        frameCount,
                        KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(sourceHumanoidClip),
                        out BoneSample[] boneSamples,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetCoreUtility.WriteBoneSampleToBoneClip(boneSamples, writableClip, out error))
                {
                    return false;
                }
            }
            finally
            {
                targetCache?.Dispose();
            }

            boneCacheClip = writableClip;
            boneCacheClip.name = cacheName;
            EditorUtility.SetDirty(boneCacheClip);
            Debug.Log($"[Kimodo][RetargetCache] Generated bone cache animation: cache='{cacheName}', source='{sourceClip.name}', targetAvatar='{targetAvatar.name}'.");
            return true;
        }

        internal static bool TrySampleMarkerForClip(
            AnimationClip sourceClip,
            string markerType,
            double sampleTime,
            Avatar sourceAvatar,
            Avatar explicitTargetAvatar,
            Animator fallbackAnimator,
            string modelName,
            bool forceRefresh,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!KimodoRetargetMarkerSamplingUtility.TryResolveTargetAvatar(
                    explicitTargetAvatar,
                    fallbackAnimator,
                    modelName,
                    out Avatar targetAvatar,
                    out error))
            {
                return false;
            }

            if (!TryGetOrCreateEditorBoneClip(
                    sourceClip,
                    sourceAvatar,
                    targetAvatar,
                    forceRefresh,
                    out AnimationClip targetClip,
                    out _,
                    out error))
            {
                return false;
            }

            SkeletonCache targetCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(targetAvatar, "KimodoMarkerEditorBoneCacheSample", out targetCache, out error))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.SampleBoneClipToBoneSample(
                        targetClip,
                        targetCache,
                        (float)sampleTime,
                        out BoneSample targetSample,
                        out error))
                {
                    return false;
                }

                return KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    targetSample,
                    targetCache,
                    modelName,
                    markerType,
                    sampleTime,
                    out sample,
                    out error);
            }
            finally
            {
                targetCache?.Dispose();
            }
        }

        private static bool TryGetOrCreateEditorMuscleClipInternal(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            bool forceRefresh,
            out AnimationClip muscleClip,
            out float frameRate,
            out string error)
        {
            muscleClip = null;
            frameRate = 0f;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (sourceClip.isHumanMotion)
            {
                muscleClip = sourceClip;
                frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
                return true;
            }

            string cacheName = KimodoRetargetEditorCacheUtility.BuildNamedCacheName(
                sourceClip,
                KimodoRetargetEditorCacheUtility.MuscleCacheType,
                null);
            if (forceRefresh && !KimodoEditorClipWritebackService.TryInvalidateNamedClipCache(cacheName, out error))
            {
                return false;
            }

            if (KimodoRetargetEditorCacheUtility.TryLoadStrictNamedCache(cacheName, out AnimationClip cachedClip, out float cachedFrameRate, out string cacheError))
            {
                if (cachedClip != null && KimodoRetargetEditorCacheUtility.ClipHasContent(cachedClip))
                {
                    muscleClip = cachedClip;
                    frameRate = cachedFrameRate;
                    return true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(cacheError))
            {
                error = cacheError;
                return false;
            }

            SkeletonCache sourceCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(sourceAvatar, "KimodoRetargetToolsEditor_SourceMuscleCache", out sourceCache, out error))
                {
                    return false;
                }

                float resolvedFrameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
                float duration = Mathf.Max(0f, sourceClip.length);
                int frameCount = KimodoRetargetSamplingUtility.ResolveFrameCount(duration, resolvedFrameRate);
                if (!KimodoRetargetSamplingUtility.TryCollectMuscleSamplesFromClip(
                        sourceClip,
                        sourceCache,
                        frameCount,
                        KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(sourceClip),
                        out MuscleSample[] samples,
                        out error))
                {
                    return false;
                }

                if (!KimodoEditorClipWritebackService.TryGetOrCreateNamedClipCache(cacheName, resolvedFrameRate, out AnimationClip writableClip, out error))
                {
                    return false;
                }

                KimodoEditorClipUtility.ApplyMuscleClipSettings(writableClip);
                if (!KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(samples, writableClip, out error))
                {
                    return false;
                }

                writableClip.name = cacheName;
                EditorUtility.SetDirty(writableClip);
                Debug.Log($"[Kimodo][RetargetCache] Generated muscle cache animation: cache='{cacheName}', source='{sourceClip.name}'.");

                muscleClip = writableClip;
                frameRate = resolvedFrameRate;
                return true;
            }
            finally
            {
                sourceCache?.Dispose();
            }
        }

        public static bool TryApplyCurveFilterToClip(
            AnimationClip sourceClip,
            AnimationClip targetClip,
            Avatar samplerAvatar,
            KimodoCurveFilterOptions options,
            out string error)
        {
            error = string.Empty;
            if (sourceClip == null || targetClip == null)
            {
                error = "Source clip or target clip is null.";
                return false;
            }

            KimodoCurveFilterOptions effectiveOptions = options ?? new KimodoCurveFilterOptions();
            if (!effectiveOptions.enabled)
            {
                if (!ReferenceEquals(sourceClip, targetClip))
                {
                    KimodoEditorClipUtility.CopyClipData(sourceClip, targetClip, forceNoLoopKeepY: true);
                }

                if (effectiveOptions.ensureQuaternionContinuity)
                {
                    targetClip.EnsureQuaternionContinuity();
                }

                return true;
            }

            if (samplerAvatar == null || !samplerAvatar.isValid || !samplerAvatar.isHuman)
            {
                error = "Sampler avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!TryApplyRecordedClipFilter(
                    sourceClip,
                    targetClip,
                    samplerAvatar,
                    effectiveOptions,
                    out error))
            {
                return false;
            }

            return true;
        }

        private static bool TryApplyRecordedClipFilter(
            AnimationClip sourceClip,
            AnimationClip targetClip,
            Avatar samplerAvatar,
            KimodoCurveFilterOptions options,
            out string error)
        {
            error = string.Empty;
            if (sourceClip == null || targetClip == null)
            {
                error = "Source clip or target clip is null.";
                return false;
            }

            GameObject samplerRoot = null;
            AnimationClip recordedClip = null;
            AnimationClip filteredClip = null;
            try
            {
                samplerRoot = CreateSamplerHierarchyForRecording(samplerAvatar, out error);
                if (samplerRoot == null)
                {
                    return false;
                }

                var recorder = new GameObjectRecorder(samplerRoot);
                recorder.BindComponentsOfType<Transform>(samplerRoot, true);

                float effectiveFps = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
                int frameCount = ComputeSampleFrameCount(sourceClip, effectiveFps);
                float dt = 1f / Mathf.Max(1f, effectiveFps);
                for (int f = 0; f < frameCount; f++)
                {
                    float t = f / effectiveFps;
                    sourceClip.SampleAnimation(samplerRoot, t);
                    recorder.TakeSnapshot(dt);
                }

                recordedClip = new AnimationClip
                {
                    name = $"{targetClip.name}_Recorded",
                    legacy = false,
                    frameRate = effectiveFps
                };

                CurveFilterOptions filter = BuildCurveFilterOptions(options);
                recorder.SaveToClip(recordedClip, effectiveFps, filter);

                HashSet<string> allowedPaths = BuildAllowedBindingPaths(sourceClip);
                filteredClip = BuildFilteredRecordedClip(recordedClip, allowedPaths, targetClip.name, effectiveFps);
                CopyFilteredClipUsingSourceBindings(sourceClip, filteredClip, targetClip, forceNoLoopKeepY: true);

                if ((options ?? new KimodoCurveFilterOptions()).ensureQuaternionContinuity)
                {
                    targetClip.EnsureQuaternionContinuity();
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Recorder SaveToClip failed: {ex.Message}";
                return false;
            }
            finally
            {
                if (filteredClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(filteredClip);
                }

                if (recordedClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(recordedClip);
                }

                if (samplerRoot != null)
                {
                    DestroySamplerHierarchyRoot(samplerRoot);
                }
            }
        }

        private static CurveFilterOptions BuildCurveFilterOptions(KimodoCurveFilterOptions options)
        {
            KimodoCurveFilterOptions effective = options ?? new KimodoCurveFilterOptions();
            float positionError = Mathf.Clamp01(effective.positionError);
            float rotationError = Mathf.Clamp01(effective.rotationError);
            float floatError = Mathf.Clamp01(effective.floatError);

            return new CurveFilterOptions
            {
                keyframeReduction = effective.enabled,
                positionError = positionError,
                scaleError = positionError,
                floatError = floatError,
                rotationError = rotationError,
                unrollRotation = true
            };
        }

        private static GameObject CreateSamplerHierarchyForRecording(Avatar avatar, out string error)
        {
            var root = new GameObject("__KimodoRecorderRoot")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                UnityEngine.Object.DestroyImmediate(root);
                error = "Sampler avatar is null/invalid/non-humanoid.";
                return null;
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, root.transform, out string buildError))
            {
                UnityEngine.Object.DestroyImmediate(root);
                error = buildError;
                return null;
            }

            error = string.Empty;
            return root;
        }

        private static AnimationClip BuildFilteredRecordedClip(
            AnimationClip sourceClip,
            HashSet<string> allowedPaths,
            string clipName,
            float fps)
        {
            if (sourceClip == null)
            {
                return null;
            }

            var output = new AnimationClip
            {
                name = $"{clipName}_Filtered",
                legacy = sourceClip.legacy,
                frameRate = fps > 0f ? fps : sourceClip.frameRate
            };
            AnimationUtility.SetAnimationClipSettings(output, AnimationUtility.GetAnimationClipSettings(sourceClip));

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(sourceClip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (!TryNormalizeRecordedBindingPath(binding.path, allowedPaths, out string normalizedPath))
                {
                    continue;
                }

                AnimationCurve curve = AnimationUtility.GetEditorCurve(sourceClip, binding);
                if (curve != null)
                {
                    output.SetCurve(normalizedPath, binding.type, binding.propertyName, curve);
                }
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(sourceClip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding binding = objectBindings[i];
                ObjectReferenceKeyframe[] curve = AnimationUtility.GetObjectReferenceCurve(sourceClip, binding);
                if (curve != null)
                {
                    AnimationUtility.SetObjectReferenceCurve(output, binding, curve);
                }
            }

            return output;
        }

        private static void CopyFilteredClipUsingSourceBindings(
            AnimationClip sourceClip,
            AnimationClip filteredClip,
            AnimationClip targetClip,
            bool forceNoLoopKeepY)
        {
            if (sourceClip == null || filteredClip == null || targetClip == null)
            {
                return;
            }

            targetClip.ClearCurves();
            targetClip.frameRate = filteredClip.frameRate > 0f
                ? filteredClip.frameRate
                : (sourceClip.frameRate > 0f ? sourceClip.frameRate : targetClip.frameRate);

            if (forceNoLoopKeepY)
            {
                AnimationUtility.SetAnimationClipSettings(
                    targetClip,
                    new AnimationClipSettings
                    {
                        loopTime = false,
                        keepOriginalPositionY = true
                    });
            }
            else
            {
                AnimationUtility.SetAnimationClipSettings(targetClip, AnimationUtility.GetAnimationClipSettings(filteredClip));
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(sourceClip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                AnimationCurve curve = AnimationUtility.GetEditorCurve(filteredClip, binding) ??
                    AnimationUtility.GetEditorCurve(sourceClip, binding);
                if (curve != null)
                {
                    targetClip.SetCurve(binding.path, binding.type, binding.propertyName, curve);
                }
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(sourceClip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding binding = objectBindings[i];
                ObjectReferenceKeyframe[] curve = AnimationUtility.GetObjectReferenceCurve(filteredClip, binding) ??
                    AnimationUtility.GetObjectReferenceCurve(sourceClip, binding);
                if (curve != null)
                {
                    AnimationUtility.SetObjectReferenceCurve(targetClip, binding, curve);
                }
            }

            AnimationEvent[] events = AnimationUtility.GetAnimationEvents(sourceClip);
            if (events != null)
            {
                AnimationUtility.SetAnimationEvents(targetClip, events);
            }
        }

        public static bool TryFilterClipInPlace(
            AnimationClip clip,
            Avatar samplerAvatar,
            KimodoCurveFilterOptions options,
            out string error)
        {
            error = string.Empty;
            if (clip == null)
            {
                error = "Clip is null.";
                return false;
            }

            List<PreservedAnimatorCurve> preservedRootMotionCurves = CapturePreservedRootMotionAnimatorCurves(clip);

            var temp = new AnimationClip
            {
                name = $"{clip.name}_FilterTemp",
                legacy = clip.legacy,
                frameRate = clip.frameRate
            };

            if (!TryApplyCurveFilterToClip(clip, temp, samplerAvatar, options, out error))
            {
                return false;
            }

            KimodoEditorClipUtility.CopyClipData(temp, clip, forceNoLoopKeepY: true);
            RestorePreservedAnimatorCurves(clip, preservedRootMotionCurves);
            UnityEngine.Object.DestroyImmediate(temp);
            return true;
        }

        private static List<PreservedAnimatorCurve> CapturePreservedRootMotionAnimatorCurves(AnimationClip clip)
        {
            var preserved = new List<PreservedAnimatorCurve>();
            if (clip == null)
            {
                return preserved;
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (binding.type != typeof(Animator) || !ShouldPreserveRootMotionAnimatorProperty(binding.propertyName))
                {
                    continue;
                }

                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null)
                {
                    continue;
                }

                preserved.Add(new PreservedAnimatorCurve
                {
                    path = binding.path,
                    propertyName = binding.propertyName,
                    curve = new AnimationCurve(curve.keys)
                });
            }

            return preserved;
        }

        private static void RestorePreservedAnimatorCurves(
            AnimationClip clip,
            List<PreservedAnimatorCurve> preservedCurves)
        {
            if (clip == null || preservedCurves == null || preservedCurves.Count == 0)
            {
                return;
            }

            for (int i = 0; i < preservedCurves.Count; i++)
            {
                PreservedAnimatorCurve preserved = preservedCurves[i];
                if (preserved == null || preserved.curve == null || string.IsNullOrWhiteSpace(preserved.propertyName))
                {
                    continue;
                }

                clip.SetCurve(
                    preserved.path ?? string.Empty,
                    typeof(Animator),
                    preserved.propertyName,
                    preserved.curve);
            }
        }

        private static bool ShouldPreserveRootMotionAnimatorProperty(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            return propertyName.StartsWith("MotionT.", StringComparison.Ordinal) ||
                propertyName.StartsWith("MotionQ.", StringComparison.Ordinal) ||
                propertyName.StartsWith("RootT.", StringComparison.Ordinal) ||
                propertyName.StartsWith("RootQ.", StringComparison.Ordinal);
        }

        [Serializable]
        private sealed class PreservedAnimatorCurve
        {
            public string path;
            public string propertyName;
            public AnimationCurve curve;
        }

        private static HashSet<string> BuildAllowedBindingPaths(AnimationClip sourceClip)
        {
            var allowedPaths = new HashSet<string>(StringComparer.Ordinal);
            if (sourceClip == null)
            {
                return allowedPaths;
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(sourceClip);
            for (int i = 0; i < bindings.Length; i++)
            {
                string path = bindings[i].path ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    allowedPaths.Add(path);
                }
            }

            return allowedPaths;
        }

        private static int ComputeSampleFrameCount(AnimationClip clip, float fps)
        {
            if (clip == null)
            {
                return 2;
            }

            float effectiveFps = fps > 0f ? fps : KimodoPlayableClip.FIXED_FRAME_RATE;
            float duration = Mathf.Max(clip.length, 1f / effectiveFps);
            return Mathf.Max(2, Mathf.RoundToInt(duration * effectiveFps) + 1);
        }

        private static bool TryNormalizeRecordedBindingPath(string bindingPath, HashSet<string> allowedPaths, out string normalizedPath)
        {
            normalizedPath = bindingPath ?? string.Empty;
            if (allowedPaths == null || allowedPaths.Count == 0)
            {
                return true;
            }

            if (allowedPaths.Contains(normalizedPath))
            {
                return true;
            }

            int firstSlash = normalizedPath.IndexOf('/');
            if (firstSlash >= 0 && firstSlash + 1 < normalizedPath.Length)
            {
                string stripped = normalizedPath.Substring(firstSlash + 1);
                if (allowedPaths.Contains(stripped))
                {
                    normalizedPath = stripped;
                    return true;
                }
            }

            return false;
        }

        private static void DestroySamplerHierarchyRoot(GameObject samplingObject)
        {
            if (samplingObject == null)
            {
                return;
            }

            Transform t = samplingObject.transform;
            while (t.parent != null)
            {
                t = t.parent;
            }

            UnityEngine.Object.DestroyImmediate(t.gameObject);
        }

        private static MotionJsonData ParseMotionJsonFlexible(string motionJson)
        {
            JToken token = JToken.Parse(motionJson);
            if (token.Type != JTokenType.Object)
            {
                throw new Exception("motionJson root is not an object.");
            }

            JObject obj = (JObject)token;
            MotionJsonData data = obj.ToObject<MotionJsonData>() ?? new MotionJsonData();

            if (data.positions != null && data.positions.Count > 0)
            {
                return data;
            }

            JToken posed = obj["posed_joints"];
            if (posed != null && posed.Type == JTokenType.Array)
            {
                data.positions = posed.ToObject<List<List<List<float>>>>();
                if (data.positions != null && data.positions.Count > 0)
                {
                    if (data.num_frames <= 0) data.num_frames = data.positions.Count;
                    if (data.num_joints <= 0 && data.positions[0] != null) data.num_joints = data.positions[0].Count;
                    return data;
                }
            }

            JToken flat = obj["joints"];
            if (flat != null && flat.Type == JTokenType.Array)
            {
                List<float> flatVals = flat.ToObject<List<float>>();
                int frames = data.num_frames;
                int joints = data.num_joints;
                if (frames > 0 && joints > 0 && flatVals != null && flatVals.Count >= frames * joints * 3)
                {
                    data.positions = new List<List<List<float>>>(frames);
                    int ptr = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        List<List<float>> frame = new List<List<float>>(joints);
                        for (int j = 0; j < joints; j++)
                        {
                            frame.Add(new List<float> { flatVals[ptr], flatVals[ptr + 1], flatVals[ptr + 2] });
                            ptr += 3;
                        }
                        data.positions.Add(frame);
                    }
                    return data;
                }
            }

            return data;
        }

        private static bool ValidateData(MotionJsonData data, out string error)
        {
            error = string.Empty;
            if (data == null)
            {
                error = "Parsed motion data is null.";
                return false;
            }

            if (data.positions == null || data.positions.Count == 0)
            {
                if (data.local_rot_quats == null || data.local_rot_quats.Count == 0)
                {
                    error = "No positions or local_rot_quats in motion data.";
                    return false;
                }
            }

            if (data.joint_names == null || data.joint_names.Length == 0)
            {
                error = "No joint_names in motion data.";
                return false;
            }

            int positionFrames = data.positions != null ? data.positions.Count : 0;
            int frameHint = data.num_frames > 0 ? data.num_frames : positionFrames;
            if (frameHint < 2)
            {
                error = "Need at least 2 frames for baking.";
                return false;
            }

            return true;
        }

        private static void BakeMotionCurvesDirect(AnimationClip targetClip, MotionJsonData data, float fps, int frameCount, Avatar profileAvatar)
        {
            int jointCount = Mathf.Min(data.joint_names.Length, data.num_joints > 0 ? data.num_joints : data.joint_names.Length);
            bool hasPositions = data.positions != null && data.positions.Count > 0;
            int rotJointCount = jointCount;
            bool hasRotations = false;
            if (data.local_rot_quats != null && data.local_rot_quats.Count > 0 && frameCount > 0)
            {
                int availableJointCount = data.local_rot_quats.Count / (frameCount * 4);
                rotJointCount = Mathf.Min(jointCount, availableJointCount);
                hasRotations = rotJointCount > 0;
            }

            int rootJoint = FindRootJointIndex(data, jointCount);
            string[] jointPaths = BuildJointPaths(data, jointCount);

            for (int joint = 0; joint < jointCount; joint++)
            {
                string path = jointPaths[joint];

                if (hasPositions && joint == rootJoint)
                {
                    AnimationCurve px = new AnimationCurve();
                    AnimationCurve py = new AnimationCurve();
                    AnimationCurve pz = new AnimationCurve();

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        Vector3 p = ReadPos(data, f, joint);
                        px.AddKey(t, p.x);
                        py.AddKey(t, p.y);
                        pz.AddKey(t, p.z);
                    }

                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", px);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", py);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", pz);
                }

                if (hasRotations && joint < rotJointCount)
                {
                    AnimationCurve qx = new AnimationCurve();
                    AnimationCurve qy = new AnimationCurve();
                    AnimationCurve qz = new AnimationCurve();
                    AnimationCurve qw = new AnimationCurve();

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        Quaternion q = ReadLocalQuat(data, f, joint, rotJointCount);
                        qx.AddKey(t, q.x);
                        qy.AddKey(t, q.y);
                        qz.AddKey(t, q.z);
                        qw.AddKey(t, q.w);
                    }

                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", qx);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", qy);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", qz);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", qw);
                }
            }
        }

        private static Avatar ResolveProfileAvatarForBake(string modelName)
        {
            if (KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar avatar, out _))
            {
                return avatar;
            }

            return null;
        }

        private static Vector3 ResolveProfilePelvisPosition(Avatar profileAvatar)
        {
            if (profileAvatar == null || !profileAvatar.isValid || !profileAvatar.isHuman)
            {
                return Vector3.zero;
            }

            SkeletonBone[] skeleton = profileAvatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0)
            {
                return Vector3.zero;
            }

            for (int i = 0; i < skeleton.Length; i++)
            {
                if (string.Equals(skeleton[i].name, "Hips", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(skeleton[i].name, "pelvis", StringComparison.OrdinalIgnoreCase))
                {
                    return skeleton[i].position;
                }
            }

            return skeleton[0].position;
        }

        private static Vector3 ReadPos(MotionJsonData data, int frame, int joint)
        {
            List<float> p = data.positions[frame][joint];
            Vector3 src = new Vector3(p[0], p[1], p[2]);
            return ConvertKimodoPosition(src);
        }

        private static Quaternion ReadLocalQuat(MotionJsonData data, int frame, int joint, int jointCount)
        {
            int baseIdx = (frame * jointCount + joint) * 4;
            float w = data.local_rot_quats[baseIdx + 0];
            float x = data.local_rot_quats[baseIdx + 1];
            float y = data.local_rot_quats[baseIdx + 2];
            float z = data.local_rot_quats[baseIdx + 3];
            Quaternion q = new Quaternion(x, y, z, w).normalized;
            return ConvertKimodoRotation(q);
        }

        private static Vector3 ConvertKimodoPosition(Vector3 src)
        {
            return new Vector3(-src.x, src.y, src.z);
        }

        private static Quaternion ConvertKimodoRotation(Quaternion src)
        {
            return new Quaternion(src.x, -src.y, -src.z, src.w);
        }

        private static int FindRootJointIndex(MotionJsonData data, int jointCount)
        {
            if (jointCount <= 0)
            {
                return 0;
            }

            if (data.joint_parents != null && data.joint_parents.Length >= jointCount)
            {
                for (int i = 0; i < jointCount; i++)
                {
                    if (data.joint_parents[i] < 0)
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        private static string[] BuildJointPaths(MotionJsonData data, int jointCount)
        {
            string[] paths = new string[jointCount];
            bool[] visiting = new bool[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                paths[i] = BuildJointPathRecursive(data, i, jointCount, paths, visiting);
            }

            return paths;
        }

        private static string BuildJointPathRecursive(MotionJsonData data, int joint, int jointCount, string[] cache, bool[] visiting)
        {
            if (joint < 0 || joint >= jointCount)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(cache[joint]))
            {
                return cache[joint];
            }

            if (visiting[joint])
            {
                cache[joint] = KimodoRuntimeUtility.SanitizeName(data.joint_names[joint]);
                return cache[joint];
            }

            visiting[joint] = true;
            string safeName = KimodoRuntimeUtility.SanitizeName(data.joint_names[joint]);
            int parent = (data.joint_parents != null && joint < data.joint_parents.Length) ? data.joint_parents[joint] : -1;
            if (parent >= 0 && parent < jointCount && parent != joint)
            {
                string parentPath = BuildJointPathRecursive(data, parent, jointCount, cache, visiting);
                cache[joint] = string.IsNullOrWhiteSpace(parentPath) ? safeName : $"{parentPath}/{safeName}";
            }
            else
            {
                cache[joint] = safeName;
            }

            visiting[joint] = false;
            return cache[joint];
        }
    }
}
#endif
