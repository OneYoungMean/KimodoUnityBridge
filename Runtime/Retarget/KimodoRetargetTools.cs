using KimodoUnityMotionTools.Generation.Pipeline;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoUnityMotionTools
{
    public static class KimodoRetargetTools
    {
        private enum ClipSamplingMode
        {
            Humanoid = 0,
            RawTransform = 1
        }

        private sealed class ClipSamplingContext
        {
            public SkeletonCache cache;
            public PlayableGraph graph;
            public AnimationClipPlayable clipPlayable;
            public bool restoreAnimatorAvatar;
            public Avatar originalAnimatorAvatar;

            public bool IsReady =>
                cache != null &&
                cache.IsReady &&
                graph.IsValid() &&
                clipPlayable.IsValid();
        }

        public static bool IsValidHumanoid(Avatar avatar)
        {
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        public static bool TryBuildSkeletonCache(Avatar avatar, string rootName, out SkeletonCache cache, out string error)
        {
            cache = null;
            error = string.Empty;

            if (!IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            GameObject root = new GameObject(string.IsNullOrWhiteSpace(rootName) ? "KimodoSkeletonCache" : rootName);
            root.hideFlags = HideFlags.HideAndDontSave;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, root.transform, out error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            SetHierarchyHideFlags(root.transform, HideFlags.HideAndDontSave);

            Animator animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }

            animator.avatar = avatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = true;
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.Update(0f);

            string canonicalRootBoneName = KimodoRetargetAvatarUtility.ResolveSkeletonRootBoneName(avatar);
            if (!KimodoRetargetAvatarUtility.TryBuildBoneNameTable(root.transform, canonicalRootBoneName, out string[] bonePaths, out error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            if (bonePaths == null || bonePaths.Length == 0)
            {
                error = "Skeleton cache bone table is empty.";
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            cache = new SkeletonCache
            {
                avatar = avatar,
                root = root,
                skeletonRoot = root.transform,
                canonicalRootBoneName = canonicalRootBoneName,
                animator = animator,
                poseHandler = new HumanPoseHandler(avatar, root.transform),
                humanScale = Mathf.Max(1e-6f, animator.humanScale),
                bonePaths = bonePaths,
                boneTransforms = KimodoRetargetAvatarUtility.BuildBoneTransforms(root.transform, bonePaths, canonicalRootBoneName),
                boneCount = bonePaths.Length
            };

            return true;
        }

        public static void DestroySkeletonCache(SkeletonCache cache)
        {
            if (cache?.root != null)
            {
                UnityEngine.Object.DestroyImmediate(cache.root);
            }
        }

        public static bool SampleBoneClipToBoneSample(
            AnimationClip clip,
            SkeletonCache cache,
            float sampleTime,
            out BoneSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (!TryBuildClipSamplingContext(clip, cache, "KimodoRetargetTools_SourceBoneSampler", ResolveClipSamplingMode(clip), out ClipSamplingContext context, out error))
            {
                return false;
            }

            try
            {
                return TrySampleBoneClipToBoneSample(context, sampleTime, out sample, out error);
            }
            finally
            {
                DestroyClipSamplingContext(context);
            }
        }

        public static bool RetargetBoneSampleToMuscleSample(
            BoneSample sourceSample,
            Avatar sourceAvatar,
            out MuscleSample targetSample,
            out string error)
        {
            targetSample = null;
            error = string.Empty;

            if (!ValidateBoneSample(sourceSample, out error))
            {
                return false;
            }

            if (!IsValidHumanoid(sourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!TryBuildSkeletonCache(sourceAvatar, "KimodoRetargetTools_SourceBoneToMuscle", out SkeletonCache sourceCache, out error))
            {
                return false;
            }

            try
            {
                return RetargetBoneSampleToMuscleSample(sourceSample, sourceCache, out targetSample, out error);
            }
            finally
            {
                DestroySkeletonCache(sourceCache);
            }
        }

        public static bool RetargetBoneSampleToMuscleSample(
            BoneSample sourceSample,
            SkeletonCache sourceCache,
            out MuscleSample targetSample,
            out string error)
        {
            targetSample = null;
            error = string.Empty;

            if (!ValidateBoneSample(sourceSample, out error))
            {
                return false;
            }

            if (!ValidateRetargetCache(sourceCache, out error))
            {
                return false;
            }

            if (!TryApplyBoneSampleToCache(sourceSample, sourceCache, out HumanPose sourcePose, out error))
            {
                return false;
            }

            targetSample = BuildMuscleSampleFromPose(sourceCache, sourcePose);
            return true;
        }


        public static bool RetargetMuscleSampleToBoneSample(
            MuscleSample sourceSample,
            SkeletonCache targetCache,
            out BoneSample targetSample,
            out string error)
        {
            targetSample = null;
            error = string.Empty;

            if (sourceSample == null)
            {
                error = "Source muscle sample is null.";
                return false;
            }

            if (!ValidateRetargetCache(targetCache, out error))
            {
                return false;
            }

            if (!TryCreateTransientMuscleClip(new[] { sourceSample }, 30f, out AnimationClip tempClip, out error))
            {
                return false;
            }

            try
            {
                return TrySampleBoneClipToBoneSample(tempClip, targetCache, 0f, ClipSamplingMode.Humanoid, out targetSample, out error);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempClip);
            }
        }

        public static bool WriteMuscleSampleToMuscleClip(IReadOnlyList<MuscleSample> samples, AnimationClip clip, out string error)
        {
            error = string.Empty;
            if (clip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            if (samples == null || samples.Count == 0)
            {
                error = "Muscle samples are empty.";
                return false;
            }

            clip.ClearCurves();
            if (!WriteMuscleCurves(samples, clip, out error))
            {
                return false;
            }

            clip.EnsureQuaternionContinuity();
            return true;
        }

        public static bool WriteBoneSampleToBoneClip(IReadOnlyList<BoneSample> samples, AnimationClip clip, out string error)
        {
            error = string.Empty;
            if (clip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            if (samples == null || samples.Count == 0)
            {
                error = "Bone samples are empty.";
                return false;
            }

            clip.ClearCurves();
            if (!WriteBoneCurves(samples, clip, out error))
            {
                return false;
            }

            clip.EnsureQuaternionContinuity();
            return true;
        }

        public static bool TryRetargetNew(
            BoneSample sourceSample,
            Avatar sourceAvatar,
            out MuscleSample targetSample,
            out string error)
        {
            return RetargetBoneSampleToMuscleSample(sourceSample, sourceAvatar, out targetSample, out error);
        }

        public static bool TryRetargetNew(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            Avatar targetAvatar,
            float sampleTime,
            out BoneSample targetSample,
            out string error)
        {
            targetSample = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!IsValidHumanoid(sourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!IsValidHumanoid(targetAvatar))
            {
                error = "Target avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!TryBuildSkeletonCache(sourceAvatar, "KimodoRetargetTools_SourceClipSample", out SkeletonCache sourceCache, out error))
            {
                return false;
            }

            if (!TryBuildSkeletonCache(targetAvatar, "KimodoRetargetTools_TargetClipSample", out SkeletonCache targetCache, out error))
            {
                DestroySkeletonCache(sourceCache);
                return false;
            }

            if (sourceClip.isHumanMotion)
            {
                try
                {
                    return SampleBoneClipToBoneSample(sourceClip, targetCache, sampleTime, out targetSample, out error);
                }
                finally
                {
                    DestroySkeletonCache(targetCache);
                    DestroySkeletonCache(sourceCache);
                }
            }

            try
            {
                if (!SampleBoneClipToBoneSample(sourceClip, sourceCache, sampleTime, out BoneSample sourceSample, out error))
                {
                    return false;
                }

                if (!RetargetBoneSampleToMuscleSample(sourceSample, sourceCache, out MuscleSample sourceMuscleSample, out error))
                {
                    return false;
                }

                return RetargetMuscleSampleToBoneSample(sourceMuscleSample, targetCache, out targetSample, out error);
            }
            finally
            {
                DestroySkeletonCache(targetCache);
                DestroySkeletonCache(sourceCache);
            }
        }

        public static bool TryRetargetNew(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            Avatar targetAvatar,
            bool exportMuscleClip,
            out AnimationClip targetClip,
            out string error)
        {
            targetClip = sourceClip;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (exportMuscleClip && sourceClip.isHumanMotion)
            {
                return true;
            }

            if (!IsValidHumanoid(sourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!IsValidHumanoid(targetAvatar))
            {
                error = "Target avatar is null/invalid/non-humanoid.";
                return false;
            }

            float frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : 30f;
            float duration = Mathf.Max(0f, sourceClip.length);
            int frameCount = ResolveFrameCount(duration, frameRate);

            if (!TryBuildSkeletonCache(sourceAvatar, "KimodoRetargetTools_SourceClipBatch", out SkeletonCache sourceCache, out error))
            {
                return false;
            }

            if (!TryBuildSkeletonCache(targetAvatar, "KimodoRetargetTools_TargetClipBatch", out SkeletonCache targetCache, out error))
            {
                DestroySkeletonCache(sourceCache);
                return false;
            }

            try
            {
                if (targetClip != null)
                {
                    targetClip.frameRate = frameRate;
                }

                if (exportMuscleClip)
                {
                    if (sourceClip.isHumanMotion)
                    {
                        return true;
                    }

                    if (!TryCollectMuscleSamplesFromBoneClip(sourceClip, sourceCache, frameCount, duration, out MuscleSample[] targetMuscleSamples, out error))
                    {
                        return false;
                    }

                    return WriteMuscleSampleToMuscleClip(targetMuscleSamples, targetClip, out error);
                }

                if (!TryCollectTargetBoneSamplesFromClip(sourceClip, sourceCache, targetCache, frameCount, duration, out BoneSample[] targetBoneSamples, out error))
                {
                    return false;
                }

                return WriteBoneSampleToBoneClip(targetBoneSamples, targetClip, out error);
            }
            finally
            {
                DestroySkeletonCache(targetCache);
                DestroySkeletonCache(sourceCache);
            }
        }

        private static bool TryBuildClipSamplingContext(
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

            if (!ValidateRetargetCache(cache, out error))
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
                //clipPlayable.SetApplyFootIK(false);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, rootName + "Output", cache.animator);
                output.SetSourcePlayable(clipPlayable);
                graph.Play();

                context = new ClipSamplingContext
                {
                    cache = cache,
                    graph = graph,
                    clipPlayable = clipPlayable,
                    restoreAnimatorAvatar = restoreAnimatorAvatar,
                    originalAnimatorAvatar = originalAnimatorAvatar
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

        private static void DestroyClipSamplingContext(ClipSamplingContext context)
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

        private static bool TryEvaluateClipSamplingContext(ClipSamplingContext context, float sampleTime, out string error)
        {
            error = string.Empty;

            if (context == null || !context.IsReady)
            {
                error = "Clip sampling context is not initialized.";
                return false;
            }

            try
            {
                context.clipPlayable.SetTime(Mathf.Max(0f, sampleTime));
                context.graph.Evaluate(0f);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TrySampleBoneClipToBoneSample(
            ClipSamplingContext context,
            float sampleTime,
            out BoneSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (!TryEvaluateClipSamplingContext(context, sampleTime, out error))
            {
                return false;
            }

            sample = CaptureBoneSample(context.cache);
            return true;
        }

        private static bool TrySampleBoneClipToBoneSample(
            AnimationClip clip,
            SkeletonCache cache,
            float sampleTime,
            ClipSamplingMode samplingMode,
            out BoneSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (!TryBuildClipSamplingContext(clip, cache, "KimodoRetargetTools_SourceBoneSampler", samplingMode, out ClipSamplingContext context, out error))
            {
                return false;
            }

            try
            {
                return TrySampleBoneClipToBoneSample(context, sampleTime, out sample, out error);
            }
            finally
            {
                DestroyClipSamplingContext(context);
            }
        }

        private static bool TryCollectTargetBoneSamplesFromClip(
            AnimationClip sourceClip,
            SkeletonCache sourceCache,
            SkeletonCache targetCache,
            int frameCount,
            float duration,
            out BoneSample[] targetBoneSamples,
            out string error)
        {
            targetBoneSamples = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (sourceClip.isHumanMotion)
            {
                return TryCollectBoneSamplesFromClip(sourceClip, targetCache, frameCount, duration, out targetBoneSamples, out error);
            }

            if (!TryCollectMuscleSamplesFromBoneClip(sourceClip, sourceCache, frameCount, duration, out MuscleSample[] sourceMuscleSamples, out error))
            {
                return false;
            }

            return TryCollectBoneSamplesFromMuscleSamples(sourceMuscleSamples, targetCache, sourceClip.frameRate > 0f ? sourceClip.frameRate : 30f, out targetBoneSamples, out error);
        }


        private static bool TryCollectBoneSamplesFromMuscleSamples(
            IReadOnlyList<MuscleSample> sourceSamples,
            SkeletonCache targetCache,
            float frameRate,
            out BoneSample[] targetSamples,
            out string error)
        {
            targetSamples = null;
            error = string.Empty;

            if (sourceSamples == null || sourceSamples.Count == 0)
            {
                error = "Muscle samples are empty.";
                return false;
            }

            if (!TryCreateTransientMuscleClip(sourceSamples, frameRate, out AnimationClip tempClip, out error))
            {
                return false;
            }
            //AssetDatabase.CreateAsset(tempClip, "Assets/tet.anim");
            //AssetDatabase.SaveAssets();
            try
            {
                float duration = sourceSamples.Count <= 1
                    ? 0f
                    : (sourceSamples.Count - 1f) / Mathf.Max(1f, tempClip.frameRate);
                return TryCollectBoneSamplesFromClip(tempClip, targetCache, sourceSamples.Count, duration, ClipSamplingMode.Humanoid, out targetSamples, out error);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempClip);
            }
        }

        private static bool TryCollectBoneSamplesFromClip(
            AnimationClip clip,
            SkeletonCache cache,
            int frameCount,
            float duration,
            out BoneSample[] samples,
            out string error)
        {
            return TryCollectBoneSamplesFromClip(clip, cache, frameCount, duration, ResolveClipSamplingMode(clip), out samples, out error);
        }

        private static bool TryCollectBoneSamplesFromClip(
            AnimationClip clip,
            SkeletonCache cache,
            int frameCount,
            float duration,
            ClipSamplingMode samplingMode,
            out BoneSample[] samples,
            out string error)
        {
            samples = null;
            error = string.Empty;

            if (!TryBuildClipSamplingContext(clip, cache, "KimodoRetargetTools_BatchBoneSampler", samplingMode, out ClipSamplingContext context, out error))
            {
                return false;
            }

            try
            {
                samples = new BoneSample[frameCount];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float time = FrameToTime(frame, frameCount, duration);
                    if (!TrySampleBoneClipToBoneSample(context, time, out BoneSample sample, out error))
                    {
                        return false;
                    }

                    samples[frame] = CloneBoneSample(sample);
                }

                return true;
            }
            finally
            {
                DestroyClipSamplingContext(context);
            }
        }

        private static bool TryCollectMuscleSamplesFromBoneClip(
            AnimationClip clip,
            SkeletonCache cache,
            int frameCount,
            float duration,
            out MuscleSample[] samples,
            out string error)
        {
            return TryCollectMuscleSamplesFromBoneClip(clip, cache, frameCount, duration, ResolveClipSamplingMode(clip), out samples, out error);
        }

        private static bool TryCollectMuscleSamplesFromBoneClip(
            AnimationClip clip,
            SkeletonCache cache,
            int frameCount,
            float duration,
            ClipSamplingMode samplingMode,
            out MuscleSample[] samples,
            out string error)
        {
            samples = null;
            error = string.Empty;

            if (!TryBuildClipSamplingContext(clip, cache, "KimodoRetargetTools_BatchMuscleSampler", samplingMode, out ClipSamplingContext context, out error))
            {
                return false;
            }

            try
            {
                samples = new MuscleSample[frameCount];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float time = FrameToTime(frame, frameCount, duration);
                    if (!TryEvaluateClipSamplingContext(context, time, out error))
                    {
                        return false;
                    }

                    HumanPose pose = new HumanPose
                    {
                        muscles = new float[HumanTrait.MuscleCount]
                    };
                    context.cache.poseHandler.GetHumanPose(ref pose);
                    EnsureHumanPoseMuscles(ref pose);
                    samples[frame] = BuildMuscleSampleFromPose(context.cache, pose);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                DestroyClipSamplingContext(context);
            }
        }

        private static bool TryCreateTransientMuscleClip(
            IReadOnlyList<MuscleSample> samples,
            float frameRate,
            out AnimationClip clip,
            out string error)
        {
            error = string.Empty;
            clip = new AnimationClip
            {
                name = "KimodoRetargetTools_TempMuscleClip",
                legacy = false,
                frameRate = frameRate > 0f ? frameRate : 30f
            };

            if (WriteMuscleSampleToMuscleClip(samples, clip, out error))
            {
                return true;
            }

            UnityEngine.Object.DestroyImmediate(clip);
            clip = null;
            return false;
        }

        private static int ResolveFrameCount(float duration, float frameRate)
        {
            return Mathf.Max(2, Mathf.RoundToInt(Mathf.Max(0f, duration) * Mathf.Max(1f, frameRate)) + 1);
        }

        private static bool TryApplyBoneSampleToCache(BoneSample sample, SkeletonCache cache, out HumanPose pose, out string error)
        {
            pose = new HumanPose();
            error = string.Empty;

            if (!ValidateBoneSample(sample, out error))
            {
                return false;
            }

            if (!ValidateRetargetCache(cache, out error))
            {
                return false;
            }

            if (sample.boneNames.Length != cache.boneTransforms.Length)
            {
                error = "Bone sample count does not match skeleton cache.";
                return false;
            }

            for (int i = 0; i < cache.boneTransforms.Length; i++)
            {
                Transform bone = cache.boneTransforms[i];
                if (bone == null)
                {
                    continue;
                }

                bone.localPosition = sample.localPositions[i];
                bone.localRotation = sample.localRotations[i];
            }

            try
            {
                cache.poseHandler.GetHumanPose(ref pose);
                EnsureHumanPoseMuscles(ref pose);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static BoneSample CaptureBoneSample(SkeletonCache cache)
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

        private static MuscleSample BuildMuscleSampleFromPose(SkeletonCache cache, HumanPose pose)
        {
            var sample = new MuscleSample
            {
                pose = pose,
                leftFootPosition = Vector3.zero,
                leftFootRotation = Quaternion.identity,
                rightFootPosition = Vector3.zero,
                rightFootRotation = Quaternion.identity,
                leftHandPosition = Vector3.zero,
                leftHandRotation = Quaternion.identity,
                rightHandPosition = Vector3.zero,
                rightHandRotation = Quaternion.identity
            };

            if (cache == null || cache.animator == null || cache.avatar == null)
            {
                return sample;
            }

            float humanScale = Mathf.Max(1e-6f, cache.humanScale);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.LeftFoot, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.leftFootPosition, out sample.leftFootRotation);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.RightFoot, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.rightFootPosition, out sample.rightFootRotation);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.LeftHand, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.leftHandPosition, out sample.leftHandRotation);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.RightHand, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.rightHandPosition, out sample.rightHandRotation);
            return sample;
        }

        private static bool WriteMuscleCurves(IReadOnlyList<MuscleSample> samples, AnimationClip clip, out string error)
        {
            error = string.Empty;
            if (samples == null || samples.Count == 0)
            {
                error = "Muscle samples are empty.";
                return false;
            }

            string[] muscleNames = HumanTrait.MuscleName;
            int muscleCount = Mathf.Min(HumanTrait.MuscleCount, muscleNames != null ? muscleNames.Length : 0);
            if (muscleCount <= 0)
            {
                error = "HumanTrait muscle list is empty.";
                return false;
            }

            AnimationCurve rootTx = new AnimationCurve();
            AnimationCurve rootTy = new AnimationCurve();
            AnimationCurve rootTz = new AnimationCurve();
            AnimationCurve rootQx = new AnimationCurve();
            AnimationCurve rootQy = new AnimationCurve();
            AnimationCurve rootQz = new AnimationCurve();
            AnimationCurve rootQw = new AnimationCurve();
            AnimationCurve leftFootTx = new AnimationCurve();
            AnimationCurve leftFootTy = new AnimationCurve();
            AnimationCurve leftFootTz = new AnimationCurve();
            AnimationCurve leftFootQx = new AnimationCurve();
            AnimationCurve leftFootQy = new AnimationCurve();
            AnimationCurve leftFootQz = new AnimationCurve();
            AnimationCurve leftFootQw = new AnimationCurve();
            AnimationCurve rightFootTx = new AnimationCurve();
            AnimationCurve rightFootTy = new AnimationCurve();
            AnimationCurve rightFootTz = new AnimationCurve();
            AnimationCurve rightFootQx = new AnimationCurve();
            AnimationCurve rightFootQy = new AnimationCurve();
            AnimationCurve rightFootQz = new AnimationCurve();
            AnimationCurve rightFootQw = new AnimationCurve();

            var muscleCurves = new AnimationCurve[muscleCount];
            for (int i = 0; i < muscleCount; i++)
            {
                muscleCurves[i] = new AnimationCurve();
            }

            float frameRate = clip.frameRate > 0f ? clip.frameRate : 30f;
            for (int frame = 0; frame < samples.Count; frame++)
            {
                MuscleSample sample = samples[frame];
                if (sample == null)
                {
                    continue;
                }

                float time = frame / frameRate;
                HumanPose pose = sample.pose;
                EnsureHumanPoseMuscles(ref pose);

                rootTx.AddKey(time, pose.bodyPosition.x);
                rootTy.AddKey(time, pose.bodyPosition.y);
                rootTz.AddKey(time, pose.bodyPosition.z);
                rootQx.AddKey(time, pose.bodyRotation.x);
                rootQy.AddKey(time, pose.bodyRotation.y);
                rootQz.AddKey(time, pose.bodyRotation.z);
                rootQw.AddKey(time, pose.bodyRotation.w);
                leftFootTx.AddKey(time, sample.leftFootPosition.x);
                leftFootTy.AddKey(time, sample.leftFootPosition.y);
                leftFootTz.AddKey(time, sample.leftFootPosition.z);
                leftFootQx.AddKey(time, sample.leftFootRotation.x);
                leftFootQy.AddKey(time, sample.leftFootRotation.y);
                leftFootQz.AddKey(time, sample.leftFootRotation.z);
                leftFootQw.AddKey(time, sample.leftFootRotation.w);
                rightFootTx.AddKey(time, sample.rightFootPosition.x);
                rightFootTy.AddKey(time, sample.rightFootPosition.y);
                rightFootTz.AddKey(time, sample.rightFootPosition.z);
                rightFootQx.AddKey(time, sample.rightFootRotation.x);
                rightFootQy.AddKey(time, sample.rightFootRotation.y);
                rightFootQz.AddKey(time, sample.rightFootRotation.z);
                rightFootQw.AddKey(time, sample.rightFootRotation.w);

                for (int muscle = 0; muscle < muscleCount; muscle++)
                {
                    float value = muscle < pose.muscles.Length ? pose.muscles[muscle] : 0f;
                    muscleCurves[muscle].AddKey(time, value);
                }
            }

            SetFloatCurve(clip, "RootT.x", rootTx);
            SetFloatCurve(clip, "RootT.y", rootTy);
            SetFloatCurve(clip, "RootT.z", rootTz);
            SetFloatCurve(clip, "RootQ.x", rootQx);
            SetFloatCurve(clip, "RootQ.y", rootQy);
            SetFloatCurve(clip, "RootQ.z", rootQz);
            SetFloatCurve(clip, "RootQ.w", rootQw);
            SetFloatCurve(clip, "LeftFootT.x", leftFootTx);
            SetFloatCurve(clip, "LeftFootT.y", leftFootTy);
            SetFloatCurve(clip, "LeftFootT.z", leftFootTz);
            SetFloatCurve(clip, "LeftFootQ.x", leftFootQx);
            SetFloatCurve(clip, "LeftFootQ.y", leftFootQy);
            SetFloatCurve(clip, "LeftFootQ.z", leftFootQz);
            SetFloatCurve(clip, "LeftFootQ.w", leftFootQw);
            SetFloatCurve(clip, "RightFootT.x", rightFootTx);
            SetFloatCurve(clip, "RightFootT.y", rightFootTy);
            SetFloatCurve(clip, "RightFootT.z", rightFootTz);
            SetFloatCurve(clip, "RightFootQ.x", rightFootQx);
            SetFloatCurve(clip, "RightFootQ.y", rightFootQy);
            SetFloatCurve(clip, "RightFootQ.z", rightFootQz);
            SetFloatCurve(clip, "RightFootQ.w", rightFootQw);

            for (int muscle = 0; muscle < muscleCount; muscle++)
            {
                string muscleName = muscleNames[muscle];
                if (!string.IsNullOrWhiteSpace(muscleName))
                {
                    SetFloatCurve(clip, muscleName, muscleCurves[muscle]);
                }
            }

            return true;
        }

        private static bool WriteBoneCurves(IReadOnlyList<BoneSample> samples, AnimationClip clip, out string error)
        {
            error = string.Empty;
            if (samples == null || samples.Count == 0)
            {
                error = "Bone samples are empty.";
                return false;
            }

            BoneSample first = samples[0];
            if (!ValidateBoneSample(first, out error))
            {
                return false;
            }

            float frameRate = clip.frameRate > 0f ? clip.frameRate : 30f;
            string[] boneNames = first.boneNames;
            AnimationCurve rootTx = new AnimationCurve();
            AnimationCurve rootTy = new AnimationCurve();
            AnimationCurve rootTz = new AnimationCurve();
            AnimationCurve rootQx = new AnimationCurve();
            AnimationCurve rootQy = new AnimationCurve();
            AnimationCurve rootQz = new AnimationCurve();
            AnimationCurve rootQw = new AnimationCurve();

            for (int i = 0; i < boneNames.Length; i++)
            {
                if (i == 0)
                {
                    // Reserve the first bone sample as humanoid root motion data.
                    for (int frame = 0; frame < samples.Count; frame++)
                    {
                        BoneSample sample = samples[frame];
                        if (sample == null || !sample.IsValid || sample.localPositions.Length == 0 || sample.localRotations.Length == 0)
                        {
                            continue;
                        }

                        float time = frame / frameRate;
                        Vector3 rootPosition = sample.localPositions[0];
                        Quaternion rootRotation = sample.localRotations[0];
                        rootTx.AddKey(time, rootPosition.x);
                        rootTy.AddKey(time, rootPosition.y);
                        rootTz.AddKey(time, rootPosition.z);
                        rootQx.AddKey(time, rootRotation.x);
                        rootQy.AddKey(time, rootRotation.y);
                        rootQz.AddKey(time, rootRotation.z);
                        rootQw.AddKey(time, rootRotation.w);
                    }

                    continue;
                }

                AnimationCurve posX = new AnimationCurve();
                AnimationCurve posY = new AnimationCurve();
                AnimationCurve posZ = new AnimationCurve();
                AnimationCurve rotX = new AnimationCurve();
                AnimationCurve rotY = new AnimationCurve();
                AnimationCurve rotZ = new AnimationCurve();
                AnimationCurve rotW = new AnimationCurve();

                for (int frame = 0; frame < samples.Count; frame++)
                {
                    BoneSample sample = samples[frame];
                    if (sample == null || !sample.IsValid || i >= sample.localPositions.Length || i >= sample.localRotations.Length)
                    {
                        continue;
                    }

                    float time = frame / frameRate;
                    Vector3 localPosition = sample.localPositions[i];
                    Quaternion localRotation = sample.localRotations[i];
                    posX.AddKey(time, localPosition.x);
                    posY.AddKey(time, localPosition.y);
                    posZ.AddKey(time, localPosition.z);
                    rotX.AddKey(time, localRotation.x);
                    rotY.AddKey(time, localRotation.y);
                    rotZ.AddKey(time, localRotation.z);
                    rotW.AddKey(time, localRotation.w);
                }

                string path = boneNames[i];
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", posX);
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", posY);
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", posZ);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rotX);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rotY);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rotZ);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rotW);
            }

            SetFloatCurve(clip, "MotionT.x", rootTx);
            SetFloatCurve(clip, "MotionT.y", rootTy);
            SetFloatCurve(clip, "MotionT.z", rootTz);
            SetFloatCurve(clip, "MotionQ.x", rootQx);
            SetFloatCurve(clip, "MotionQ.y", rootQy);
            SetFloatCurve(clip, "MotionQ.z", rootQz);
            SetFloatCurve(clip, "MotionQ.w", rootQw);

            return true;
        }

        private static bool ValidateBoneSample(BoneSample sample, out string error)
        {
            error = string.Empty;
            if (sample == null || !sample.IsValid)
            {
                error = "Bone sample is invalid.";
                return false;
            }

            return true;
        }

        private static bool ValidateRetargetCache(SkeletonCache cache, out string error)
        {
            error = string.Empty;
            if (cache == null || !cache.IsReady)
            {
                error = "Skeleton cache is not initialized.";
                return false;
            }

            return true;
        }

        private static void SetHierarchyHideFlags(Transform root, HideFlags hideFlags)
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

        private static ClipSamplingMode ResolveClipSamplingMode(AnimationClip clip)
        {
            return clip != null && clip.isHumanMotion
                ? ClipSamplingMode.Humanoid
                : ClipSamplingMode.RawTransform;
        }

        private static bool TryConfigureAnimatorForClipSampling(
            SkeletonCache cache,
            ClipSamplingMode samplingMode,
            out Avatar originalAnimatorAvatar,
            out bool restoreAnimatorAvatar,
            out string error)
        {
            originalAnimatorAvatar = null;
            restoreAnimatorAvatar = false;
            error = string.Empty;

            if (!ValidateRetargetCache(cache, out error))
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

            animator.avatar = desiredAvatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = true;
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            //animator.Rebind();
            //animator.Update(0f);

            //if (desiredAvatar != null)
            //{
            //    cache.humanScale = Mathf.Max(1e-6f, animator.humanScale);
            //}

            return true;
        }

        private static void RestoreAnimatorAfterClipSampling(SkeletonCache cache, Avatar avatar)
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
            animator.Update(0f);

            if (avatar != null)
            {
                cache.humanScale = Mathf.Max(1e-6f, animator.humanScale);
            }
        }

        private static void SetFloatCurve(AnimationClip clip, string propertyName, AnimationCurve curve)
        {
            clip.SetCurve(string.Empty, typeof(Animator), propertyName, curve);
        }

        private static float FrameToTime(int frame, int frameCount, float duration)
        {
            if (frameCount <= 1)
            {
                return 0f;
            }

            float normalized = frame / (frameCount - 1f);
            return Mathf.Clamp01(normalized) * Mathf.Max(0f, duration);
        }

        private static void EnsureHumanPoseMuscles(ref HumanPose pose)
        {
            if (pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount)
            {
                pose.muscles = new float[HumanTrait.MuscleCount];
            }
        }

        private static BoneSample CloneBoneSample(BoneSample source)
        {
            if (source == null)
            {
                return null;
            }

            return new BoneSample
            {
                boneNames = source.boneNames != null ? (string[])source.boneNames.Clone() : null,
                localPositions = source.localPositions != null ? (Vector3[])source.localPositions.Clone() : null,
                localRotations = source.localRotations != null ? (Quaternion[])source.localRotations.Clone() : null
            };
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
                pose.muscles = (float[])pose.muscles.Clone();
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

        private static bool TryGetHumanoidIkGoalPose(
            SkeletonCache cache,
            AvatarIKGoal avatarIKGoal,
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            float humanScale,
            out Vector3 goalPosition,
            out Quaternion goalRotation)
        {
            goalPosition = Vector3.zero;
            goalRotation = Quaternion.identity;

            if (!ValidateRetargetCache(cache, out _))
            {
                return false;
            }

            HumanBodyBones bone = HumanBodyBoneFromAvatarIKGoal(avatarIKGoal);
            if (bone == HumanBodyBones.LastBone)
            {
                return false;
            }

            Transform transform = ResolveHumanBoneTransform(cache, bone);
            if (transform == null)
            {
                return false;
            }

            int humanId = (int)bone;
            Quaternion postRotation = AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(cache.avatar, humanId);
            Quaternion worldGoalRotation = transform.rotation * postRotation;
            Vector3 worldGoalPosition = transform.position;

            if (avatarIKGoal == AvatarIKGoal.LeftFoot || avatarIKGoal == AvatarIKGoal.RightFoot)
            {
                float axisLength = AvatarRuntimeAccess.GetAvatarAxisLengthOrZero(cache.avatar, humanId);
                worldGoalPosition += worldGoalRotation * new Vector3(axisLength, 0f, 0f);
            }

            Quaternion inverseBodyRotation = Quaternion.Inverse(bodyRotation);
            goalPosition = inverseBodyRotation * (worldGoalPosition - bodyPosition);
            goalRotation = inverseBodyRotation * worldGoalRotation;
            goalPosition /= Mathf.Max(1e-6f, humanScale);
            return true;
        }

        private static Transform ResolveHumanBoneTransform(SkeletonCache cache, HumanBodyBones bone)
        {
            if (cache?.animator == null)
            {
                return null;
            }

            if (cache.animator.avatar != null)
            {
                Transform resolved = cache.animator.GetBoneTransform(bone);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            if (!IsValidHumanoid(cache.avatar))
            {
                return null;
            }

            HumanBone[] humanBones = cache.avatar.humanDescription.human;
            string humanName = bone.ToString();
            for (int i = 0; i < humanBones.Length; i++)
            {
                HumanBone humanBone = humanBones[i];
                if (!string.Equals(humanBone.humanName, humanName, StringComparison.Ordinal))
                {
                    continue;
                }

                return KimodoRetargetAvatarUtility.FindTransformByName(cache.skeletonRoot, humanBone.boneName);
            }

            return null;
        }

        private static HumanBodyBones HumanBodyBoneFromAvatarIKGoal(AvatarIKGoal avatarIKGoal)
        {
            switch (avatarIKGoal)
            {
                case AvatarIKGoal.LeftFoot:
                    return HumanBodyBones.LeftFoot;
                case AvatarIKGoal.RightFoot:
                    return HumanBodyBones.RightFoot;
                case AvatarIKGoal.LeftHand:
                    return HumanBodyBones.LeftHand;
                case AvatarIKGoal.RightHand:
                    return HumanBodyBones.RightHand;
                default:
                    return HumanBodyBones.LastBone;
            }
        }
    }
}
