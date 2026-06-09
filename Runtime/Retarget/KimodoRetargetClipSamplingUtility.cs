using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoBridge
{
    internal static class KimodoRetargetClipSamplingUtility
    {
        internal enum ClipSamplingMode
        {
            Humanoid = 0,
            RawTransform = 1
        }

        internal sealed class ClipSamplingContext
        {
            public SkeletonCache cache;
            public PlayableGraph graph;
            public AnimationClipPlayable clipPlayable;
            public bool restoreAnimatorAvatar;
            public Avatar originalAnimatorAvatar;
            public float evaluatedTime;
            public bool hasEvaluatedTime;
            public float frameRate;

            public bool IsReady =>
                cache != null &&
                cache.IsReady &&
                graph.IsValid() &&
                clipPlayable.IsValid();
        }

        internal static void SetHierarchyHideFlags(Transform root, HideFlags hideFlags)
        {
            if (root == null)
            {
                return;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                all[i].gameObject.hideFlags = hideFlags;
            }
        }

        internal static void CaptureSkeletonBindPose(SkeletonCache cache)
        {
            if (cache == null || cache.root == null || cache.boneTransforms == null)
            {
                return;
            }

            Transform rootTransform = cache.root.transform;
            cache.rootLocalPosition = rootTransform.localPosition;
            cache.rootLocalRotation = rootTransform.localRotation;
            cache.rootLocalScale = rootTransform.localScale;

            int count = cache.boneTransforms.Length;
            cache.bindLocalPositions = new Vector3[count];
            cache.bindLocalRotations = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                Transform bone = cache.boneTransforms[i];
                if (bone == null)
                {
                    cache.bindLocalPositions[i] = Vector3.zero;
                    cache.bindLocalRotations[i] = Quaternion.identity;
                    continue;
                }

                cache.bindLocalPositions[i] = bone.localPosition;
                cache.bindLocalRotations[i] = bone.localRotation;
            }
        }

        internal static void ResetSkeletonCachePose(SkeletonCache cache)
        {
            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out _))
            {
                return;
            }

            Transform rootTransform = cache.root != null ? cache.root.transform : null;
            if (rootTransform != null)
            {
                rootTransform.localPosition = cache.rootLocalPosition;
                rootTransform.localRotation = cache.rootLocalRotation;
                rootTransform.localScale = cache.rootLocalScale;
            }

            Transform[] bones = cache.boneTransforms;
            Vector3[] bindPositions = cache.bindLocalPositions;
            Quaternion[] bindRotations = cache.bindLocalRotations;
            if (bones == null || bindPositions == null || bindRotations == null)
            {
                return;
            }

            int count = Mathf.Min(bones.Length, Mathf.Min(bindPositions.Length, bindRotations.Length));
            for (int i = 0; i < count; i++)
            {
                Transform bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                bone.localPosition = bindPositions[i];
                bone.localRotation = bindRotations[i];
            }
        }

        internal static ClipSamplingMode ResolveClipSamplingMode(AnimationClip clip)
        {
            return clip != null && clip.isHumanMotion
                ? ClipSamplingMode.Humanoid
                : ClipSamplingMode.RawTransform;
        }

        internal static float ResolveFrameRate(AnimationClip clip)
        {
            return clip != null && clip.frameRate > 0f
                ? clip.frameRate
                : KimodoPlayableClip.FIXED_FRAME_RATE;
        }

        internal static float ResolvePreRollSeconds(float frameRate)
        {
            return 1f / Mathf.Max(1f, frameRate);
        }

        internal static bool TryBuildClipSamplingContext(
            AnimationClip clip,
            SkeletonCache cache,
            string rootName,
            ClipSamplingMode samplingMode,
            out ClipSamplingContext context,
            out string error)
        {
            context = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out error))
            {
                return false;
            }

            PlayableGraph graph = default;
            Avatar originalAnimatorAvatar = null;
            bool restoreAnimatorAvatar = false;
            try
            {
                if (!TryConfigureAnimatorForClipSampling(cache, samplingMode, out originalAnimatorAvatar, out restoreAnimatorAvatar, out error))
                {
                    return false;
                }

                graph = PlayableGraph.Create(rootName + "Graph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationClipPlayable clipPlayable = AnimationClipPlayable.Create(graph, clip);
                clipPlayable.SetApplyFootIK(true);
                clipPlayable.SetApplyPlayableIK(true);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, rootName + "Output", cache.animator);
                output.SetSourcePlayable(clipPlayable);

                float seedTime = ResolveSeedTime(clip);
                clipPlayable.SetTime(seedTime);
                graph.Play();

                graph.Evaluate(0f);

                context = new ClipSamplingContext
                {
                    cache = cache,
                    graph = graph,
                    clipPlayable = clipPlayable,
                    restoreAnimatorAvatar = restoreAnimatorAvatar,
                    originalAnimatorAvatar = originalAnimatorAvatar,
                    evaluatedTime = seedTime,
                    hasEvaluatedTime = true,
                    frameRate = ResolveFrameRate(clip)
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (graph.IsValid())
                {
                    graph.Destroy();
                }

                if (restoreAnimatorAvatar)
                {
                    RestoreAnimatorAfterClipSampling(cache, originalAnimatorAvatar);
                }

                return false;
            }
        }

        internal static void DestroyClipSamplingContext(ClipSamplingContext context)
        {
            if (context == null)
            {
                return;
            }

            if (context.graph.IsValid())
            {
                context.graph.Destroy();
            }

            if (context.restoreAnimatorAvatar)
            {
                RestoreAnimatorAfterClipSampling(context.cache, context.originalAnimatorAvatar);
            }
        }

        internal static float ResolveSeedTime(AnimationClip clip)
        {
            return -ResolvePreRollSeconds(ResolveFrameRate(clip));
        }

        internal static bool TryEvaluateClipSamplingContext(ClipSamplingContext context, float sampleTime, out string error)
        {
            error = string.Empty;

            if (context == null || !context.IsReady)
            {
                error = "Clip sampling context is not initialized.";
                return false;
            }

            try
            {
                float targetTime = sampleTime;
                if (context.hasEvaluatedTime && targetTime < context.evaluatedTime)
                {
                    error = $"Clip sampling context does not support backward evaluation: previous={context.evaluatedTime:F6}, target={targetTime:F6}. Rebuild the context before sampling an earlier time.";
                    return false;
                }

                float deltaTime = context.hasEvaluatedTime
                    ? targetTime - context.evaluatedTime
                    : targetTime;

                context.graph.Evaluate(deltaTime);
                context.evaluatedTime = targetTime;
                context.hasEvaluatedTime = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryConfigureAnimatorForClipSampling(
            SkeletonCache cache,
            ClipSamplingMode samplingMode,
            out Avatar originalAnimatorAvatar,
            out bool restoreAnimatorAvatar,
            out string error)
        {
            originalAnimatorAvatar = null;
            restoreAnimatorAvatar = false;
            error = string.Empty;

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out error))
            {
                return false;
            }

            Animator animator = cache.animator;
            if (animator == null)
            {
                error = "Skeleton cache animator is null.";
                return false;
            }

            originalAnimatorAvatar = animator.avatar;
            Avatar desiredAvatar = samplingMode == ClipSamplingMode.Humanoid ? cache.avatar : null;
            restoreAnimatorAvatar = !ReferenceEquals(originalAnimatorAvatar, desiredAvatar);

            ResetSkeletonCachePose(cache);
            animator.avatar = desiredAvatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = true;
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();

            if (desiredAvatar != null)
            {
                cache.humanScale = Mathf.Max(1e-6f, animator.humanScale);
            }

            return true;
        }

        internal static void RestoreAnimatorAfterClipSampling(SkeletonCache cache, Avatar avatar)
        {
            if (cache?.animator == null)
            {
                return;
            }

            Animator animator = cache.animator;
            animator.avatar = avatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = true;
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();

            if (avatar != null)
            {
                cache.humanScale = Mathf.Max(1e-6f, animator.humanScale);
            }
        }
    }
    internal static class KimodoRetargetSamplingUtility
    {
        private delegate bool ClipSampleCallback<TSample>(
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
            float sampleTime,
            out TSample sample,
            out string error);

        internal static bool SampleBoneClipToBoneSample(
            AnimationClip clip,
            SkeletonCache cache,
            float sampleTime,
            out BoneSample sample,
            out string error)
        {
            return TrySampleFromClip(
                clip,
                cache,
                sampleTime,
                "KimodoRetargetTools_SourceBoneSampler",
                KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(clip),
                TrySampleBoneClipToBoneSampleInternal,
                out sample,
                out error);
        }

        internal static bool TryResolveSourceHumanoidClip(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            string rootName,
            AnimationClip providedSourceHumanoidClip,
            ref SkeletonCache sourceCache,
            out AnimationClip sourceHumanoidClip,
            out string error)
        {
            sourceHumanoidClip = providedSourceHumanoidClip ?? sourceClip;
            error = string.Empty;

            if (sourceHumanoidClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (providedSourceHumanoidClip != null || sourceClip.isHumanMotion)
            {
                return true;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(sourceCache, out _))
            {
                sourceCache = null;
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(sourceAvatar, rootName, out sourceCache, out error))
                {
                    return false;
                }
            }
            int frameRate = Mathf.RoundToInt(sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE);
            float duration = Mathf.Max(0f, sourceClip.length);
            int frameCount = ResolveFrameCount(duration, frameRate);
            if (!TryCollectMuscleSamplesFromClip(
                    sourceClip,
                    sourceCache,
                    frameCount,
                    duration,
                    KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(sourceClip),
                    out MuscleSample[] samples,
                    out error))
            {
                return false;
            }

            if (!TryCreateTransientMuscleClip(samples, frameRate, out sourceHumanoidClip, out error))
            {
                return false;
            }

            sourceHumanoidClip.name = BuildTransientHumanoidClipName(sourceClip);
            return true;
        }

        internal static bool TryCollectBoneSamplesFromClip(
            AnimationClip clip,
            SkeletonCache cache,
            int frameCount,
            float duration,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            out BoneSample[] samples,
            out string error)
        {
            return TryCollectSamplesFromClip(
                clip,
                cache,
                frameCount,
                duration,
                "KimodoRetargetTools_BatchBoneSampler",
                samplingMode,
                TrySampleBoneClipToBoneSampleInternal,
                CloneBoneSample,
                out samples,
                out error);
        }

        internal static bool TryCollectMuscleSamplesFromClip(
            AnimationClip clip,
            SkeletonCache cache,
            int frameCount,
            float duration,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            out MuscleSample[] samples,
            out string error)
        {
            return TryCollectSamplesFromClip(
                clip,
                cache,
                frameCount,
                duration,
                "KimodoRetargetTools_BatchMuscleSampler",
                samplingMode,
                TrySampleMuscleClipToMuscleSampleInternal,
                CloneMuscleSample,
                out samples,
                out error);
        }

        internal static bool TrySampleTargetFromHumanoidClip(
            AnimationClip sourceHumanoidClip,
            SkeletonCache targetCache,
            float sampleTime,
            out BoneSample targetSample,
            out MuscleSample targetMuscleSample,
            out string error)
        {
            targetSample = null;
            targetMuscleSample = null;
            error = string.Empty;

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(targetCache, out error))
            {
                return false;
            }

            if (sourceHumanoidClip == null)
            {
                error = "Source humanoid clip is null.";
                return false;
            }

            if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                    sourceHumanoidClip,
                    targetCache,
                    "KimodoRetargetTools_TargetHumanoidSample",
                    KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                    out KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
                    out error))
            {
                return false;
            }

            try
            {
                if (!TrySampleBoneClipToBoneSampleInternal(context, sampleTime, out targetSample, out error))
                {
                    return false;
                }

                if (!ValidateBoneSample(targetSample, out error))
                {
                    targetSample = null;
                    return false;
                }

                if (!TryCaptureMuscleSample(targetCache, out targetMuscleSample, out error))
                {
                    targetSample = null;
                    return false;
                }

                return true;
            }
            finally
            {
                KimodoRetargetClipSamplingUtility.DestroyClipSamplingContext(context);
            }
        }

        internal static int ResolveFrameCount(float duration, float frameRate)
        {
            return Mathf.Max(2, Mathf.RoundToInt(Mathf.Max(0f, duration) * Mathf.Max(1f, frameRate)) + 1);
        }

        internal static bool TryApplyBoneSampleToSkeletonCache(BoneSample sample, SkeletonCache cache, out string error)
        {
            error = string.Empty;

            if (!ValidateBoneSample(sample, out error))
            {
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out error))
            {
                return false;
            }

            if (sample.boneNames.Length != cache.boneTransforms.Length)
            {
                error = "Bone sample length does not match target cache.";
                return false;
            }

            for (int i = 0; i < cache.boneTransforms.Length; i++)
            {
                Transform transform = cache.boneTransforms[i];
                if (transform == null)
                {
                    continue;
                }

                transform.localPosition = sample.localPositions[i];
                transform.localRotation = sample.localRotations[i];
            }
            return true;
        }

        internal static bool ValidateBoneSample(BoneSample sample, out string error)
        {
            error = string.Empty;

            if (sample == null)
            {
                error = "Bone sample is null.";
                return false;
            }

            if (!sample.IsValid)
            {
                error = "Bone sample is invalid.";
                return false;
            }

            if (sample.boneNames.Length == 0)
            {
                error = "Bone sample is empty.";
                return false;
            }

            return true;
        }

        internal static bool TryCaptureMuscleSample(SkeletonCache cache, out MuscleSample sample, out string error)
        {
            sample = null;
            error = string.Empty;

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out error))
            {
                return false;
            }

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref pose);
                sample = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);
                return sample != null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryCreateTransientMuscleClip(
            IReadOnlyList<MuscleSample> samples,
            float frameRate,
            out AnimationClip clip,
            out string error)
        {
            clip = null;
            error = string.Empty;

            if (samples == null || samples.Count == 0)
            {
                error = "Muscle samples are empty.";
                return false;
            }

            clip = new AnimationClip
            {
                frameRate = frameRate > 0f ? frameRate : KimodoPlayableClip.FIXED_FRAME_RATE,
                hideFlags = HideFlags.HideAndDontSave,
                name = "KimodoTransientMuscleClip"
            };

            if (!KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(samples, clip, out error))
            {
                UnityEngine.Object.DestroyImmediate(clip);
                clip = null;
                return false;
            }

            return true;
        }

        internal static string BuildTransientHumanoidClipName(AnimationClip sourceClip)
        {
            string sourceName = sourceClip != null && !string.IsNullOrWhiteSpace(sourceClip.name)
                ? sourceClip.name
                : "Clip";
            return $"{sourceName}_TransientHumanoid";
        }

        private static bool TrySampleFromClip<TSample>(
            AnimationClip clip,
            SkeletonCache cache,
            float sampleTime,
            string rootName,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            ClipSampleCallback<TSample> sampleCallback,
            out TSample sample,
            out string error)
        {
            sample = default;
            error = string.Empty;

            if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                    clip,
                    cache,
                    rootName,
                    samplingMode,
                    out KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
                    out error))
            {
                return false;
            }

            try
            {
                return sampleCallback(context, sampleTime, out sample, out error);
            }
            finally
            {
                KimodoRetargetClipSamplingUtility.DestroyClipSamplingContext(context);
            }
        }

        private static bool TryCollectSamplesFromClip<TSample>(
            AnimationClip clip,
            SkeletonCache cache,
            int frameCount,
            float duration,
            string rootName,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            ClipSampleCallback<TSample> sampleCallback,
            Func<TSample, TSample> cloneSample,
            out TSample[] samples,
            out string error)
        {
            samples = null;
            error = string.Empty;

            if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                    clip,
                    cache,
                    rootName,
                    samplingMode,
                    out KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
                    out error))
            {
                return false;
            }

            try
            {
                samples = new TSample[frameCount + 1];
                if (!TryBuildSeedSample(context, out TSample seedSample, out error))
                {
                    return false;
                }

                samples[0] = cloneSample(seedSample);

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float time = FrameToTime(frame, frameCount, duration);
                    if (!sampleCallback(context, time, out TSample sample, out error))
                    {
                        return false;
                    }

                    samples[frame + 1] = cloneSample(sample);
                }

                return true;
            }
            finally
            {
                KimodoRetargetClipSamplingUtility.DestroyClipSamplingContext(context);
            }
        }

        private static bool TryBuildSeedSample<TSample>(
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
            out TSample seedSample,
            out string error)
        {
            seedSample = default;
            error = string.Empty;

            if (context == null || context.cache == null)
            {
                error = "Seed sample context is not initialized.";
                return false;
            }

            if (typeof(TSample) == typeof(BoneSample))
            {
                BoneSample boneSeed = CaptureBoneSample(context.cache);
                if (boneSeed == null)
                {
                    error = "Failed to capture seed bone sample.";
                    return false;
                }

                seedSample = (TSample)(object)boneSeed;
                return true;
            }

            if (typeof(TSample) != typeof(MuscleSample))
            {
                error = "Unsupported seed sample type.";
                return false;
            }

            if (TryCaptureMuscleSample(context.cache, out MuscleSample muscleSeed, out error))
            {
                seedSample = (TSample)(object)muscleSeed;
                return true;
            }

            string firstError = error;
            try
            {
                KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(context.cache);
                if (TryCaptureMuscleSample(context.cache, out muscleSeed, out error))
                {
                    seedSample = (TSample)(object)muscleSeed;
                    return true;
                }

                error = string.IsNullOrWhiteSpace(firstError)
                    ? error
                    : string.IsNullOrWhiteSpace(error)
                        ? firstError
                        : $"{firstError}; {error}";
                return false;
            }
            catch (Exception ex)
            {
                error = string.IsNullOrWhiteSpace(error)
                    ? $"Seed sample fallback failed: {ex.Message}"
                    : $"{error}; seed sample fallback failed: {ex.Message}";
                return false;
            }
        }

        private static bool TrySampleBoneClipToBoneSampleInternal(
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
            float sampleTime,
            out BoneSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(context, sampleTime, out error))
            {
                return false;
            }

            sample = CaptureBoneSample(context.cache);
            return true;
        }

        private static bool TrySampleMuscleClipToMuscleSampleInternal(
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
            float sampleTime,
            out MuscleSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(context, sampleTime, out error))
            {
                return false;
            }

            return TryCaptureMuscleSample(context.cache, out sample, out error);
        }

        internal static BoneSample CaptureBoneSample(SkeletonCache cache)
        {
            var sample = new BoneSample
            {
                boneNames = cache.bonePaths,
                localPositions = new Vector3[cache.bonePaths.Length],
                localRotations = new Quaternion[cache.bonePaths.Length]
            };

            for (int i = 0; i < cache.boneTransforms.Length; i++)
            {
                Transform transform = cache.boneTransforms[i];
                if (transform == null)
                {
                    sample.localPositions[i] = Vector3.zero;
                    sample.localRotations[i] = Quaternion.identity;
                    continue;
                }

                sample.localPositions[i] = transform.localPosition;
                sample.localRotations[i] = transform.localRotation;
            }

            return sample;
        }

        private static BoneSample CloneBoneSample(BoneSample source)
        {
            if (source == null || !source.IsValid)
            {
                return null;
            }

            int count = source.boneNames.Length;
            var clone = new BoneSample
            {
                boneNames = new string[count],
                localPositions = new Vector3[count],
                localRotations = new Quaternion[count]
            };

            Array.Copy(source.boneNames, clone.boneNames, count);
            Array.Copy(source.localPositions, clone.localPositions, count);
            Array.Copy(source.localRotations, clone.localRotations, count);
            return clone;
        }

        private static MuscleSample CloneMuscleSample(MuscleSample source)
        {
            if (source == null)
            {
                return null;
            }

            HumanPose pose = source.pose;
            if (pose.muscles != null)
            {
                float[] muscles = new float[pose.muscles.Length];
                Array.Copy(pose.muscles, muscles, pose.muscles.Length);
                pose.muscles = muscles;
            }

            return new MuscleSample
            {
                pose = pose,
                leftFootPosition = source.leftFootPosition,
                leftFootRotation = source.leftFootRotation,
                rightFootPosition = source.rightFootPosition,
                rightFootRotation = source.rightFootRotation,
                leftHandPosition = source.leftHandPosition,
                leftHandRotation = source.leftHandRotation,
                rightHandPosition = source.rightHandPosition,
                rightHandRotation = source.rightHandRotation
            };
        }


        private static float FrameToTime(int frame, int frameCount, float duration)
        {
            if (frameCount <= 1 || duration <= 0f)
            {
                return 0f;
            }

            float normalized = frame / Mathf.Max(1f, frameCount - 1f);
            return Mathf.Clamp01(normalized) * duration;
        }

    }
}
