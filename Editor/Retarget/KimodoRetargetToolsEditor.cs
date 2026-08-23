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
            public List<float> foot_contacts;
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

            float fps = data.fps > 0 ? data.fps : KimodoMotionModelProfiles.DefaultFrameRate;
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

            BakeMotionCurvesDirect(rawClip, data, fps, frameCount);
            KimodoFootContactTrackUtility.Apply(rawClip, data.foot_contacts, frameCount, fps);
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

            AnimationClip muscleClip = null;
            try
            {
                if (!TryGetOrCreateEditorMuscleClipInternal(
                        sourceClip,
                        sourceAvatar,
                        forceRefresh: false,
                        out muscleClip,
                        out float muscleFrameRate,
                        out error))
                {
                    return false;
                }
                if (!ReferenceEquals(targetClip, muscleClip))
                {
                    KimodoEditorClipUtility.CopyClipData(muscleClip, targetClip, forceNoLoopKeepY: true);
                }

                KimodoEditorClipUtility.ApplyMuscleClipSettings(targetClip);
                targetClip.legacy = false;
                targetClip.frameRate = muscleFrameRate > 0f
                    ? muscleFrameRate
                    : (sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoMotionModelProfiles.DefaultFrameRate);

                EditorUtility.SetDirty(targetClip);
                return true;
            }
            finally
            {
                DestroyTransientClip(muscleClip, targetClip);
            }
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

            if (!TryPrepareEditorClipCache(
                    sourceClip,
                    KimodoRetargetEditorCacheUtility.BoneCacheType,
                    targetAvatar,
                    forceRefresh,
                    out string cacheName,
                    out boneCacheClip,
                    out frameRate,
                    out error))
            {
                return false;
            }
            if (boneCacheClip != null)
            {
                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetAvatar] bone cache hit: " +
                    $"cache='{cacheName}', targetAvatar={DescribeAvatarForDebug(targetAvatar)}, " +
                    $"clip='{boneCacheClip.name}', {DescribeClipBindingsForDebug(boneCacheClip)}.");
                return true;
            }

            bool persist = KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips;
            AnimationClip writableClip = null;
            if (persist &&
                !KimodoEditorClipWritebackService.TryGetOrCreateNamedClipCache(
                    cacheName,
                    frameRate,
                    out writableClip,
                    out error))
            {
                return false;
            }

            AnimationClip sourceHumanoidClip = null;
            if (!TryGetOrCreateEditorMuscleClipInternal(
                    sourceClip,
                    sourceAvatar,
                    forceRefresh,
                    out sourceHumanoidClip,
                    out float sourceFrameRate,
                    out error))
            {
                return false;
            }

            if (sourceFrameRate > 0f)
            {
                frameRate = sourceFrameRate;
            }

            RetargetSkeleton targetCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(targetAvatar, "KimodoRetargetToolsEditor_TargetBoneCache", out targetCache, out error))
                {
                    return false;
                }

                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetAvatar] target cache ready: " +
                    $"avatar={DescribeAvatarForDebug(targetCache.avatar)}, " +
                    $"animatorAvatar={DescribeAvatarForDebug(targetCache.animator != null ? targetCache.animator.avatar : null)}, " +
                    $"root='{targetCache.skeletonRoot?.name}', bones={targetCache.boneTransforms?.Length ?? 0}, " +
                    $"humanBones={targetCache.humanBoneTransforms?.Count ?? 0}, " +
                    $"paths={DescribeBonePathsForDebug(targetCache.bonePaths)}.");

                float duration = Mathf.Max(0f, sourceClip.length);
                int frameCount = KimodoRetargetSamplingUtility.ResolveInclusiveSampleCount(duration, frameRate);
                if (!KimodoRetargetSamplingUtility.TryCollectBoneSamplesFromClip(
                        sourceHumanoidClip,
                        targetCache,
                        frameCount,
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out BoneSample[] boneSamples,
                        out error,
                        applyMotionXToDelta: true))
                {
                    return false;
                }

                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetAvatar] Humanoid->Bone sample completed: " +
                    $"sourceHumanoidClip='{sourceHumanoidClip.name}', isHumanMotion={sourceHumanoidClip.isHumanMotion}, " +
                    $"samplingMode={KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid}, " +
                    $"applyMotionXToDelta=true, frames={boneSamples?.Length ?? 0}, " +
                    $"{DescribeBoneSampleMotionForDebug(boneSamples)}.");
                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetAvatar] animator avatar after Humanoid->Bone sample: " +
                    $"{DescribeAvatarForDebug(targetCache.animator != null ? targetCache.animator.avatar : null)}.");

                if (persist)
                {
                    if (!KimodoRetargetCoreUtility.WriteBoneSampleToBoneClip(boneSamples, writableClip, out error))
                    {
                        return false;
                    }
                }
                else if (!KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                        boneSamples,
                        frameRate,
                        out writableClip,
                        out error))
                {
                    return false;
                }
            }
            finally
            {
                targetCache?.Dispose();
                DestroyTransientClip(sourceHumanoidClip, sourceClip);
            }

            boneCacheClip = writableClip;
            boneCacheClip.name = cacheName;
            if (persist)
            {
                EditorUtility.SetDirty(boneCacheClip);
            }
            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][RetargetCache] Generated {(persist ? "persisted" : "transient")} bone animation: " +
                $"cache='{cacheName}', source='{sourceClip.name}', targetAvatar='{targetAvatar.name}'.");
            return true;
        }

        internal static bool TrySampleMarkerForClip(
            AnimationClip sourceClip,
            string markerType,
            double sampleTime,
            Avatar sourceAvatar,
            Avatar explicitTargetAvatar,
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

            Avatar requestedTargetAvatar = KimodoRetargetCoreUtility.IsValidHumanoid(explicitTargetAvatar)
                ? explicitTargetAvatar
                : sourceAvatar;
            if (!KimodoRetargetMarkerSamplingUtility.TryResolveTargetAvatar(
                    requestedTargetAvatar,
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

            RetargetSkeleton targetCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(targetAvatar, "KimodoMarkerEditorBoneCacheSample", out targetCache, out error))
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
                DestroyTransientClip(targetClip);
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

            if (!TryPrepareEditorClipCache(
                    sourceClip,
                    KimodoRetargetEditorCacheUtility.MuscleCacheType,
                    null,
                    forceRefresh,
                    out string cacheName,
                    out muscleClip,
                    out frameRate,
                    out error))
            {
                return false;
            }
            if (muscleClip != null)
            {
                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetAvatar] muscle cache hit: " +
                    $"cache='{cacheName}', sourceAvatar={DescribeAvatarForDebug(sourceAvatar)}, " +
                    $"clip='{muscleClip.name}', {DescribeClipBindingsForDebug(muscleClip)}.");
                return true;
            }

            RetargetSkeleton sourceCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(sourceAvatar, "KimodoRetargetToolsEditor_SourceMuscleCache", out sourceCache, out error))
                {
                    return false;
                }

                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetAvatar] source cache ready: " +
                    $"avatar={DescribeAvatarForDebug(sourceCache.avatar)}, " +
                    $"animatorAvatar={DescribeAvatarForDebug(sourceCache.animator != null ? sourceCache.animator.avatar : null)}, " +
                    $"root='{sourceCache.skeletonRoot?.name}', bones={sourceCache.boneTransforms?.Length ?? 0}, " +
                    $"humanBones={sourceCache.humanBoneTransforms?.Count ?? 0}, " +
                    $"paths={DescribeBonePathsForDebug(sourceCache.bonePaths)}.");

                float duration = Mathf.Max(0f, sourceClip.length);
                int frameCount = KimodoRetargetSamplingUtility.ResolveInclusiveSampleCount(duration, frameRate);
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
                bool persist = KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips;
                AnimationClip writableClip;
                if (persist)
                {
                    if (!KimodoEditorClipWritebackService.TryGetOrCreateNamedClipCache(
                            cacheName,
                            frameRate,
                            out writableClip,
                            out error) ||
                        !KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(samples, writableClip, out error))
                    {
                        return false;
                    }
                }
                else if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                        samples,
                        frameRate,
                        out writableClip,
                        out error))
                {
                    return false;
                }

                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetAvatar] source muscle sample completed: " +
                    $"sourceClip='{sourceClip.name}', isHumanMotion={sourceClip.isHumanMotion}, " +
                    $"frames={samples?.Length ?? 0}, {DescribeMuscleSampleMotionForDebug(samples)}.");

                KimodoEditorClipUtility.ApplyMuscleClipSettings(writableClip);
                writableClip.name = cacheName;
                if (persist)
                {
                    EditorUtility.SetDirty(writableClip);
                }
                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][RetargetCache] Generated {(persist ? "persisted" : "transient")} muscle animation: " +
                    $"cache='{cacheName}', source='{sourceClip.name}'.");

                muscleClip = writableClip;
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
                samplerRoot = new GameObject("__KimodoRecorderRoot")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(
                        samplerAvatar,
                        samplerRoot.transform,
                        out error))
                {
                    DestroySamplerHierarchyRoot(samplerRoot);
                    samplerRoot = null;
                    return false;
                }
                KimodoRetargetClipSamplingUtility.SetHierarchyHideFlags(
                    samplerRoot.transform,
                    HideFlags.HideAndDontSave);

                var recorder = new GameObjectRecorder(samplerRoot);
                recorder.BindComponentsOfType<Transform>(samplerRoot, true);

                float effectiveFps = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoMotionModelProfiles.DefaultFrameRate;
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

        private static bool TryPrepareEditorClipCache(
            AnimationClip sourceClip,
            string cacheType,
            Avatar targetAvatar,
            bool forceRefresh,
            out string cacheName,
            out AnimationClip cachedClip,
            out float frameRate,
            out string error)
        {
            cacheName = string.Empty;
            cachedClip = null;
            frameRate = 0f;
            error = string.Empty;
            cacheName = KimodoRetargetEditorCacheUtility.BuildNamedCacheName(sourceClip, cacheType, targetAvatar);
            frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoMotionModelProfiles.DefaultFrameRate;
            if (!KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips)
            {
                return true;
            }
            if (forceRefresh && !KimodoEditorClipWritebackService.TryInvalidateNamedClipCache(cacheName, out error))
            {
                return false;
            }

            if (KimodoRetargetEditorCacheUtility.TryLoadStrictNamedCache(
                    cacheName,
                    out cachedClip,
                    out float cachedFrameRate,
                    out error))
            {
                frameRate = cachedFrameRate;
            }

            return true;
        }

        private static void DestroyTransientClip(AnimationClip clip, AnimationClip keep = null)
        {
            if (clip == null ||
                ReferenceEquals(clip, keep) ||
                !string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(clip)))
            {
                return;
            }
            UnityEngine.Object.DestroyImmediate(clip);
        }

        internal static string DescribeAvatarForDebug(Avatar avatar)
        {
            if (avatar == null)
            {
                return "<null>";
            }

            int humanCount = 0;
            int skeletonCount = 0;
            try
            {
                HumanDescription description = avatar.humanDescription;
                humanCount = description.human != null ? description.human.Length : 0;
                skeletonCount = description.skeleton != null ? description.skeleton.Length : 0;
            }
            catch (Exception)
            {
                // Keep diagnostics best-effort; Avatar validity/name are still useful.
            }

            string assetPath = AssetDatabase.GetAssetPath(avatar);
            return $"name='{avatar.name}',id='{KimodoUnityObjectIdUtility.NameKey(avatar)}',asset='{assetPath}'," +
                $"isValid={avatar.isValid},isHuman={avatar.isHuman},human={humanCount},skeleton={skeletonCount}";
        }

        private static string DescribeBonePathsForDebug(string[] bonePaths)
        {
            if (bonePaths == null || bonePaths.Length == 0)
            {
                return "<none>";
            }

            int count = Mathf.Min(8, bonePaths.Length);
            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = string.IsNullOrWhiteSpace(bonePaths[i]) ? "<root>" : bonePaths[i];
            }

            string suffix = bonePaths.Length > count ? $",...(+{bonePaths.Length - count})" : string.Empty;
            return $"[{string.Join(",", names)}{suffix}]";
        }

        private static string DescribeClipBindingsForDebug(AnimationClip clip)
        {
            if (clip == null)
            {
                return "curveBindings=0";
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            int animatorBindings = 0;
            int transformBindings = 0;
            var names = new List<string>();
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (binding.type == typeof(Animator))
                {
                    animatorBindings++;
                }
                else if (binding.type == typeof(Transform))
                {
                    transformBindings++;
                }

                if (names.Count < 8 && !names.Contains(binding.path))
                {
                    names.Add(string.IsNullOrWhiteSpace(binding.path) ? "<root>" : binding.path);
                }
            }

            return $"curveBindings={bindings.Length},animatorBindings={animatorBindings}," +
                $"transformBindings={transformBindings},paths=[{string.Join(",", names)}]";
        }

        private static string DescribeBoneSampleMotionForDebug(IReadOnlyList<BoneSample> samples)
        {
            if (samples == null || samples.Count < 2 || samples[0] == null || samples[samples.Count - 1] == null)
            {
                return "dynamicBones=unknown";
            }

            BoneSample first = samples[0];
            BoneSample last = samples[samples.Count - 1];
            int count = Mathf.Min(first.localRotations?.Length ?? 0, last.localRotations?.Length ?? 0);
            int dynamic = 0;
            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                float rotationDelta = Quaternion.Angle(first.localRotations[i], last.localRotations[i]);
                Vector3 positionDelta = last.localPositions[i] - first.localPositions[i];
                if (rotationDelta > 0.01f || positionDelta.sqrMagnitude > 1e-8f)
                {
                    dynamic++;
                    if (names.Count < 8)
                    {
                        names.Add(first.boneNames != null && i < first.boneNames.Length
                            ? (string.IsNullOrWhiteSpace(first.boneNames[i]) ? "<root>" : first.boneNames[i])
                            : $"#{i}");
                    }
                }
            }

            return $"dynamicBones={dynamic}/{count},names=[{string.Join(",", names)}]";
        }

        private static string DescribeMuscleSampleMotionForDebug(IReadOnlyList<MuscleSample> samples)
        {
            if (samples == null || samples.Count < 2 || samples[0] == null || samples[samples.Count - 1] == null)
            {
                return "dynamicMuscles=unknown";
            }

            float[] first = samples[0].data;
            float[] last = samples[samples.Count - 1].data;
            int count = Mathf.Min(first?.Length ?? 0, last?.Length ?? 0);
            int dynamic = 0;
            for (int i = 0; i < count; i++)
            {
                if (Mathf.Abs(last[i] - first[i]) > 1e-4f)
                {
                    dynamic++;
                }
            }

            return $"dynamicMuscles={dynamic}/{count}";
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
                propertyName.StartsWith("MotionQ.", StringComparison.Ordinal);
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

            float effectiveFps = fps > 0f ? fps : KimodoMotionModelProfiles.DefaultFrameRate;
            float duration = Mathf.Max(clip.length, 1f / effectiveFps);
            return Mathf.Max(
                2,
                KimodoFrameTimeUtility.SecondsToFrameCount(duration, effectiveFps) + 1);
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

        private static void BakeMotionCurvesDirect(AnimationClip targetClip, MotionJsonData data, float fps, int frameCount)
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
                    Vector3 heldPosition = ReadPos(data, frameCount - 1, joint);
                    float coverageEnd = frameCount / fps;
                    px.AddKey(coverageEnd, heldPosition.x);
                    py.AddKey(coverageEnd, heldPosition.y);
                    pz.AddKey(coverageEnd, heldPosition.z);

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
                    Quaternion heldRotation = ReadLocalQuat(data, frameCount - 1, joint, rotJointCount);
                    float coverageEnd = frameCount / fps;
                    qx.AddKey(coverageEnd, heldRotation.x);
                    qy.AddKey(coverageEnd, heldRotation.y);
                    qz.AddKey(coverageEnd, heldRotation.z);
                    qw.AddKey(coverageEnd, heldRotation.w);

                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", qx);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", qy);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", qz);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", qw);
                }
            }
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
