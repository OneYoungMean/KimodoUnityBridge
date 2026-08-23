using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using TimelineInject;
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

        internal delegate bool ClipSampleCallback<TSample>(
            ClipSamplingContext context,
            float sampleTime,
            out TSample sample,
            out string error);

        internal sealed class ClipSamplingContext : IDisposable
        {
            private bool disposed;

            public RetargetSkeleton cache;
            public PlayableGraph graph;
            public AnimationClipPlayable clipPlayable;
            public bool restoreAnimatorAvatar;
            public Avatar originalAnimatorAvatar;
            public float evaluatedTime;
            public bool hasEvaluatedTime;
            public float frameRate;

            public bool IsReady =>
                !disposed &&
                cache != null &&
                cache.IsReady &&
                graph.IsValid() &&
                clipPlayable.IsValid();

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                try
                {
                    if (graph.IsValid())
                    {
                        graph.Destroy();
                    }
                }
                finally
                {
                    if (restoreAnimatorAvatar)
                    {
                        RestoreAnimatorAfterClipSampling(cache, originalAnimatorAvatar);
                        restoreAnimatorAvatar = false;
                    }
                }
            }
        }

        internal sealed class ClipSamplingSession : IDisposable
        {
            private readonly ClipSamplingContext context;
            private bool disposed;

            private ClipSamplingSession(ClipSamplingContext context)
            {
                this.context = context;
            }

            internal ClipSamplingContext Context => context;
            internal float FrameRate => context != null ? context.frameRate : KimodoMotionModelProfiles.DefaultFrameRate;

            internal static bool TryCreate(
                AnimationClip clip,
                RetargetSkeleton cache,
                string rootName,
                ClipSamplingMode samplingMode,
                out ClipSamplingSession session,
                out string error,
                bool applyMotionXToDelta = true)
            {
                session = null;
                if (!TryBuildClipSamplingContext(
                        clip,
                        cache,
                        rootName,
                        samplingMode,
                        out ClipSamplingContext context,
                        out error,
                        applyMotionXToDelta))
                {
                    return false;
                }

                session = new ClipSamplingSession(context);
                return true;
            }

            internal bool TrySample<TSample>(
                float sampleTime,
                ClipSampleCallback<TSample> sampleCallback,
                out TSample sample,
                out string error)
            {
                sample = default;
                if (disposed || context == null)
                {
                    error = "Clip sampling session is disposed.";
                    return false;
                }

                return sampleCallback(context, sampleTime, out sample, out error);
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                context?.Dispose();
            }
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

        internal static void CaptureSkeletonBindPose(RetargetSkeleton cache)
        {
            if (cache == null || cache.root == null || cache.boneTransforms == null)
            {
                return;
            }

            Transform rootTransform = cache.root.transform;
            cache.rootLocalPosition = rootTransform.localPosition;
            cache.rootLocalRotation = rootTransform.localRotation;
            cache.rootLocalScale = rootTransform.localScale;
            cache.bindSkeletonRootWorldRotation = cache.skeletonRoot != null
                ? cache.skeletonRoot.rotation
                : rootTransform.rotation;

            int count = cache.boneTransforms.Length;
            cache.bindLocalPositions = new Vector3[count];
            cache.bindLocalRotations = new Quaternion[count];
            cache.bindWorldRotations = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                Transform bone = cache.boneTransforms[i];
                if (bone == null)
                {
                    cache.bindLocalPositions[i] = Vector3.zero;
                    cache.bindLocalRotations[i] = Quaternion.identity;
                    cache.bindWorldRotations[i] = Quaternion.identity;
                    continue;
                }

                cache.bindLocalPositions[i] = bone.localPosition;
                cache.bindLocalRotations[i] = bone.localRotation;
                cache.bindWorldRotations[i] = bone.rotation;
            }

        }

        internal static void ResetRetargetSkeletonPose(RetargetSkeleton cache)
        {
            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(cache, out _))
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
                : KimodoMotionModelProfiles.DefaultFrameRate;
        }

        internal static bool TryBuildHumanoidClipSamplingContext(
            AnimationClip clip,
            RetargetSkeleton cache,
            string rootName,
            ClipSamplingMode samplingMode,
            out ClipSamplingContext context,
            out string error,
            bool applyMotionXToDelta = true,
            bool includeLeftHandEffector = false,
            bool includeRightHandEffector = false,
            bool includeLeftFootEffector = false,
            bool includeRightFootEffector = false)
        {
            // Compatibility flags remain accepted at this boundary; sampling is
            // intentionally FK-only and never consumes external IK targets.
            return TryBuildClipSamplingContext(
                clip,
                cache,
                rootName,
                samplingMode,
                out context,
                out error,
                applyMotionXToDelta,
                includeFootEffectors: false,
                includeLeftHandEffector: false,
                includeRightHandEffector: false,
                includeLeftFootEffector: false,
                includeRightFootEffector: false);
        }

        internal static bool TryBuildClipSamplingContext(
            AnimationClip clip,
            RetargetSkeleton cache,
            string rootName,
            ClipSamplingMode samplingMode,
            out ClipSamplingContext context,
            out string error,
            bool applyMotionXToDelta = true,
            bool includeFootEffectors = false,
            bool includeLeftHandEffector = false,
            bool includeRightHandEffector = false,
            bool includeLeftFootEffector = false,
            bool includeRightFootEffector = false)
        {
            context = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(cache, out error))
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
                clipPlayable.SetApplyFootIK(false);
                clipPlayable.SetApplyPlayableIK(false);
                Playable sourcePlayable = clipPlayable;
                if (applyMotionXToDelta)
                {
                    sourcePlayable = AnimationOffsetPlayableAccess.CreateMotionXToDeltaAndConnect(
                        graph,
                        sourcePlayable);
                }
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, rootName + "Output", cache.animator);
                output.SetSourcePlayable(sourcePlayable);

                clipPlayable.SetTime(0f);
                graph.Play();
                graph.Evaluate(0f);

                context = new ClipSamplingContext
                {
                    cache = cache,
                    graph = graph,
                    clipPlayable = clipPlayable,
                    restoreAnimatorAvatar = restoreAnimatorAvatar,
                    originalAnimatorAvatar = originalAnimatorAvatar,
                    evaluatedTime = 0f,
                    hasEvaluatedTime = false,
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
            RetargetSkeleton cache,
            ClipSamplingMode samplingMode,
            out Avatar originalAnimatorAvatar,
            out bool restoreAnimatorAvatar,
            out string error)
        {
            originalAnimatorAvatar = null;
            restoreAnimatorAvatar = false;
            error = string.Empty;

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(cache, out error))
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

            ResetRetargetSkeletonPose(cache);
            animator.avatar = desiredAvatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = true;
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();

            return true;
        }

        internal static void RestoreAnimatorAfterClipSampling(RetargetSkeleton cache, Avatar avatar)
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

        }

    }
    internal static class KimodoRetargetSamplingUtility
    {
        private delegate bool ClipWriteCallback<TSample>(
            IReadOnlyList<TSample> samples,
            AnimationClip clip,
            out string error);

        internal static bool SampleBoneClipToBoneSample(
            AnimationClip clip,
            RetargetSkeleton cache,
            float sampleTime,
            out BoneSample sample,
            out string error)
        {
            return SampleBoneClipToBoneSample(
                clip,
                cache,
                sampleTime,
                KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(clip),
                out sample,
                out error);
        }

        internal static bool SampleBoneClipToBoneSample(
            AnimationClip clip,
            RetargetSkeleton cache,
            float sampleTime,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            out BoneSample sample,
            out string error)
        {
            return TrySampleFromClip(
                clip,
                cache,
                sampleTime,
                "KimodoRetargetTools_SourceBoneSampler",
                samplingMode,
                TrySampleBoneClipToBoneSampleInternal,
                out sample,
                out error);
        }

        internal static bool TrySampleBoneClipSession(
            KimodoRetargetClipSamplingUtility.ClipSamplingSession session,
            float sampleTime,
            out BoneSample sample,
            out string error)
        {
            sample = null;
            if (session == null)
            {
                error = "Clip sampling session is null.";
                return false;
            }

            return session.TrySample(sampleTime, TrySampleBoneClipToBoneSampleInternal, out sample, out error);
        }

        internal static bool TryResolveSourceHumanoidClip(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            string rootName,
            AnimationClip providedSourceHumanoidClip,
            ref RetargetSkeleton sourceCache,
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

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(sourceCache, out _))
            {
                sourceCache = null;
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(sourceAvatar, rootName, out sourceCache, out error))
                {
                    return false;
                }
            }
            int frameRate = Mathf.RoundToInt(sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoMotionModelProfiles.DefaultFrameRate);
            float duration = Mathf.Max(0f, sourceClip.length);
            int frameCount = ResolveInclusiveSampleCount(duration, frameRate);
            if (!TryCollectMuscleSamplesFromClip(
                    sourceClip,
                    sourceCache,
                    frameCount,
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
            RetargetSkeleton cache,
            int frameCount,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            out BoneSample[] samples,
            out string error,
            bool applyMotionXToDelta = true)
        {
            return TryCollectSamplesFromClip(
                clip,
                cache,
                frameCount,
                "KimodoRetargetTools_BatchBoneSampler",
                samplingMode,
                TrySampleBoneClipToBoneSampleInternal,
                CloneBoneSample,
                out samples,
                out error,
                applyMotionXToDelta);
        }

        internal static bool TryCollectMuscleSamplesFromClip(
            AnimationClip clip,
            RetargetSkeleton cache,
            int frameCount,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            out MuscleSample[] samples,
            out string error)
        {
            return TryCollectSamplesFromClip(
                clip,
                cache,
                frameCount,
                "KimodoRetargetTools_BatchMuscleSampler",
                samplingMode,
                TrySampleMuscleClipToMuscleSampleInternal,
                CloneMuscleSample,
                out samples,
                out error,
                applyMotionXToDelta: true);
        }

        internal static bool TrySampleTargetFromSingleMuscleSample(
            MuscleSample sourceSample,
            float frameRate,
            RetargetSkeleton targetCache,
            out BoneSample targetSample,
            out MuscleSample targetMuscleSample,
            out string error,
            bool includeLeftHandEffector = false,
            bool includeRightHandEffector = false,
            bool includeFootEffectors = false,
            bool includeLeftFootEffector = false,
            bool includeRightFootEffector = false)
        {
            targetSample = null;
            targetMuscleSample = null;
            error = string.Empty;
            if (sourceSample == null)
            {
                error = "Source muscle sample is null.";
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            // IK targets remain protocol data only; sampling is always FK.
            if (!TryRetargetMuscleSamplesToBoneSamples(
                    new[] { sourceSample },
                    frameRate,
                    targetCache,
                    out BoneSample[] targetSamples,
                    out error) ||
                targetSamples == null ||
                targetSamples.Length == 0)
            {
                return false;
            }

            targetSample = targetSamples[0];
            if (!TryApplyBoneSampleToRetargetSkeleton(targetSample, targetCache, out error))
            {
                targetSample = null;
                return false;
            }

            if (!TryCaptureMuscleSample(targetCache, out targetMuscleSample, out error))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Rebuilds a transient FK pose from the current muscle snapshot.
        /// Effector channels are transport data and are not interpreted here.
        /// </summary>
        internal static bool TryRebuildPoseFromMuscles(
            MuscleSample sourceSample,
            float frameRate,
            RetargetSkeleton sourceCache,
            out BoneSample solvedBoneSample,
            out MuscleSample solvedMuscleSample,
            out string error,
            bool includeLeftHandEffector = false,
            bool includeRightHandEffector = false,
            bool includeFootEffectors = false,
            bool includeLeftFootEffector = false,
            bool includeRightFootEffector = false)
        {
            solvedBoneSample = null;
            solvedMuscleSample = null;
            error = string.Empty;
            if (sourceSample == null)
            {
                error = "Source muscle sample is null.";
                return false;
            }
            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(sourceCache, out error))
            {
                return false;
            }

            var clipSamples = new[] { sourceSample, sourceSample };
            AnimationClip clip = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context = null;
            try
            {
                if (!TryCreateTransientMuscleClip(
                        clipSamples,
                        frameRate,
                        out clip,
                        out error))
                {
                    return false;
                }

                bool builtContext = KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                    clip,
                    sourceCache,
                    "KimodoRetarget_SourcePoseClip",
                    KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                    out context,
                    out error,
                    applyMotionXToDelta: true);
                if (!builtContext ||
                    !KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(context, 0f, out error))
                {
                    return false;
                }

                solvedBoneSample = CaptureBoneSample(sourceCache);
                if (!TryCaptureMuscleSample(sourceCache, out MuscleSample solvedPoseSample, out error))
                {
                    return false;
                }

                // The rebuilt skeleton provides the canonical FK muscle pose.
                solvedMuscleSample = solvedPoseSample;
                return true;
            }
            finally
            {
                context?.Dispose();
                if (clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }


        internal static bool TryRetargetMuscleSamplesToBoneSamples(
            IReadOnlyList<MuscleSample> sourceSamples,
            float frameRate,
            RetargetSkeleton targetCache,
            out BoneSample[] targetSamples,
            out string error,
            Func<AnimationClip, string, string> writebackClip = null)
        {
            // This is the transport boundary: source samples must already be
            // solved in their own Avatar. Retargeting only consumes the
            // muscle snapshot and never solves it again.
            targetSamples = null;
            error = string.Empty;
            if (sourceSamples == null || sourceSamples.Count == 0)
            {
                error = "Source muscle samples are empty.";
                return false;
            }
            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            int sampleCount = sourceSamples.Count;
            int clipSampleCount = Mathf.Max(2, sampleCount);
            var clipSamples = new MuscleSample[clipSampleCount];
            for (int i = 0; i < clipSampleCount; i++)
            {
                MuscleSample source = sourceSamples[Mathf.Min(i, sampleCount - 1)];
                if (source == null)
                {
                    error = $"Source muscle sample {i} is null.";
                    return false;
                }
                clipSamples[i] = source;
            }

            AnimationClip clip = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context = null;
            try
            {
                // IK targets are scene data and are not copied across Avatar
                // retargeting. The target pose is captured below in the
                // target Avatar's own space instead.
                if (!TryCreateTransientMuscleClip(
                        clipSamples,
                        frameRate,
                        out clip,
                        out error))
                {
                    return false;
                }
                if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                        clip,
                        targetCache,
                        "KimodoRetarget_TargetPoseClip",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out context,
                        out error,
                        applyMotionXToDelta: true))
                {
                    return false;
                }

                targetSamples = new BoneSample[sampleCount];
                MuscleSample[] targetMuscleSamples = writebackClip != null
                    ? new MuscleSample[sampleCount]
                    : null;
                float fps = Mathf.Max(1f, frameRate);
                for (int i = 0; i < sampleCount; i++)
                {
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                            context,
                            i / fps,
                            out error))
                    {
                        targetSamples = null;
                        return false;
                    }

                    targetSamples[i] = CaptureBoneSample(targetCache);
                    if (targetMuscleSamples != null &&
                        !TryCaptureMuscleSample(targetCache, out targetMuscleSamples[i], out error))
                    {
                        targetSamples = null;
                        return false;
                    }
                }

                if (writebackClip != null)
                {
                    if (!TryCreateTransientMuscleClip(
                            targetMuscleSamples,
                            frameRate,
                            out AnimationClip targetClip,
                            out error))
                    {
                        targetSamples = null;
                        return false;
                    }

                    try
                    {
                        string writebackError = writebackClip(targetClip, "MuscleClip");
                        if (!string.IsNullOrWhiteSpace(writebackError))
                        {
                            error = writebackError;
                            targetSamples = null;
                            return false;
                        }
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(targetClip);
                    }
                }
                return true;
            }
            finally
            {
                context?.Dispose();
                if (clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        internal static int ResolveInclusiveSampleCount(float duration, float frameRate)
        {
            return Mathf.Max(
                2,
                KimodoFrameTimeUtility.SecondsToFrameCount(
                    Mathf.Max(0f, duration),
                    Mathf.Max(1f, frameRate)) + 1);
        }

        internal static bool TryApplyBoneSampleToRetargetSkeleton(BoneSample sample, RetargetSkeleton cache, out string error)
        {
            error = string.Empty;

            if (!ValidateBoneSample(sample, out error))
            {
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(cache, out error))
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

        internal static bool TryCaptureMuscleSample(RetargetSkeleton cache, out MuscleSample sample, out string error)
        {
            return KimodoRetargetHumanoidPoseUtility.TryCaptureEvaluatedMuscleSample(
                cache,
                out sample,
                out error);
        }

        internal static bool TryCaptureSampleData(
            RetargetSkeleton cache,
            out MuscleSample sampleData,
            out KimodoSampleChannelMask enableMask,
            out string error)
        {
            sampleData = new MuscleSample();
            enableMask = new KimodoSampleChannelMask();
            error = string.Empty;

            if (!TryCaptureMuscleSample(cache, out MuscleSample sample, out error))
            {
                return false;
            }

            if (!sample.IsValid)
            {
                // Sampling completed, but this pose must not be advertised as
                // valid. Keep the fixed-size payload and clear all validity bits.
                error = string.Empty;
                return true;
            }

            // The retarget sampler already produced the canonical 70D payload,
            // including the legacy body-relative footTQ transport. Do not pass
            // it through CharacterPose, whose scene-facing foot fields are
            // world-space display/effector values and would overwrite footTQ.
            sampleData.data = (float[])sample.data.Clone();

            enableMask.muscle49 = true;
            enableMask.rootTQ = true;
            enableMask.leftFootTQ = true;
            enableMask.rightFootTQ = true;
            return true;
        }

        internal static bool TryCreateTransientMuscleClip(
            IReadOnlyList<MuscleSample> samples,
            float frameRate,
            out AnimationClip clip,
            out string error)
        {
            return TryCreateTransientClip(
                samples,
                frameRate,
                "Muscle samples are empty.",
                "KimodoTransientBodyMuscleClip",
                KimodoRetargetClipWriter.WriteRetargetMuscleCurves,
                out clip,
                out error);
        }

        internal static bool TryCreateTransientBoneClip(
            IReadOnlyList<BoneSample> samples,
            float frameRate,
            out AnimationClip clip,
            out string error)
        {
            return TryCreateTransientClip(
                samples,
                frameRate,
                "Bone samples are empty.",
                "KimodoTransientPoseClip",
                KimodoRetargetCoreUtility.WriteBoneSampleToBoneClip,
                out clip,
                out error);
        }

        private static bool TryCreateTransientClip<TSample>(
            IReadOnlyList<TSample> samples,
            float frameRate,
            string emptyError,
            string clipName,
            ClipWriteCallback<TSample> writeSamples,
            out AnimationClip clip,
            out string error)
        {
            clip = null;
            error = string.Empty;
            if (samples == null || samples.Count == 0)
            {
                error = emptyError;
                return false;
            }

            clip = new AnimationClip
            {
                frameRate = frameRate > 0f ? frameRate : KimodoMotionModelProfiles.DefaultFrameRate,
                hideFlags = HideFlags.HideAndDontSave,
                name = clipName
            };
            if (!writeSamples(samples, clip, out error))
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
            RetargetSkeleton cache,
            float sampleTime,
            string rootName,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            KimodoRetargetClipSamplingUtility.ClipSampleCallback<TSample> sampleCallback,
            out TSample sample,
            out string error)
        {
            sample = default;
            error = string.Empty;

            if (!KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                    clip,
                    cache,
                    rootName,
                    samplingMode,
                    out KimodoRetargetClipSamplingUtility.ClipSamplingSession session,
                    out error))
            {
                return false;
            }

            try
            {
                return session.TrySample(sampleTime, sampleCallback, out sample, out error);
            }
            finally
            {
                session?.Dispose();
            }
        }

        private static bool TryCollectSamplesFromClip<TSample>(
            AnimationClip clip,
            RetargetSkeleton cache,
            int frameCount,
            string rootName,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            KimodoRetargetClipSamplingUtility.ClipSampleCallback<TSample> sampleCallback,
            Func<TSample, TSample> cloneSample,
            out TSample[] samples,
            out string error,
            bool applyMotionXToDelta)
        {
            samples = null;
            error = string.Empty;

            if (!KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                    clip,
                    cache,
                    rootName,
                    samplingMode,
                    out KimodoRetargetClipSamplingUtility.ClipSamplingSession session,
                    out error,
                    applyMotionXToDelta))
            {
                return false;
            }

            try
            {
                samples = new TSample[frameCount];
                float frameRate = Mathf.Max(1f, session.FrameRate);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float time = frame / frameRate;
                    if (!session.TrySample(time, sampleCallback, out TSample sample, out error))
                    {
                        return false;
                    }

                    samples[frame] = cloneSample(sample);
                }

                return true;
            }
            finally
            {
                session?.Dispose();
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

        internal static BoneSample CaptureBoneSample(RetargetSkeleton cache)
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

        internal static MuscleSample CloneMuscleSample(MuscleSample source)
        {
            if (source == null)
            {
                return null;
            }

            return source.Clone();
        }


    }
}
