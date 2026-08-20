using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoBridge
{
    internal static class KimodoRetargetClipSamplingUtility
    {
        // Serialized constraint payloads keep the enable bit separate from
        // the scene-space transform.  The animation job never receives these
        // values; they are materialized as temporary/preview Transforms and
        // converted to TransformSceneHandles before the playable is created.
        internal struct HumanoidWorldIkTargets
        {
            internal bool leftHand;
            internal bool rightHand;
            internal bool leftFoot;
            internal bool rightFoot;
            internal Vector3 leftHandPosition;
            internal Quaternion leftHandRotation;
            internal Vector3 rightHandPosition;
            internal Quaternion rightHandRotation;
            internal Vector3 leftFootPosition;
            internal Quaternion leftFootRotation;
            internal Vector3 rightFootPosition;
            internal Quaternion rightFootRotation;

            internal bool Any => leftHand || rightHand || leftFoot || rightFoot;

        }

        // This is the only object-level target payload accepted by the
        // sampling pipeline.  HumanoidIkSolveJob receives the handles created
        // from these Transforms, never the Vector3/Quaternion values above.
        internal struct HumanoidIkSceneTargets
        {
            internal bool leftHand;
            internal bool rightHand;
            internal bool leftFoot;
            internal bool rightFoot;
            internal Transform leftHandTransform;
            internal Transform rightHandTransform;
            internal Transform leftFootTransform;
            internal Transform rightFootTransform;

            internal bool Any => leftHand || rightHand || leftFoot || rightFoot;
        }

        private struct HumanoidIkSceneHandles
        {
            internal bool leftHand;
            internal bool rightHand;
            internal bool leftFoot;
            internal bool rightFoot;
            internal TransformSceneHandle leftHandHandle;
            internal TransformSceneHandle rightHandHandle;
            internal TransformSceneHandle leftFootHandle;
            internal TransformSceneHandle rightFootHandle;

            internal static HumanoidIkSceneHandles Bind(
                Animator animator,
                HumanoidIkSceneTargets targets)
            {
                return new HumanoidIkSceneHandles
                {
                    leftHand = targets.leftHand && targets.leftHandTransform != null,
                    rightHand = targets.rightHand && targets.rightHandTransform != null,
                    leftFoot = targets.leftFoot && targets.leftFootTransform != null,
                    rightFoot = targets.rightFoot && targets.rightFootTransform != null,
                    leftHandHandle = targets.leftHand && targets.leftHandTransform != null
                        ? animator.BindSceneTransform(targets.leftHandTransform)
                        : default,
                    rightHandHandle = targets.rightHand && targets.rightHandTransform != null
                        ? animator.BindSceneTransform(targets.rightHandTransform)
                        : default,
                    leftFootHandle = targets.leftFoot && targets.leftFootTransform != null
                        ? animator.BindSceneTransform(targets.leftFootTransform)
                        : default,
                    rightFootHandle = targets.rightFoot && targets.rightFootTransform != null
                        ? animator.BindSceneTransform(targets.rightFootTransform)
                        : default
                };
            }
        }

        internal sealed class HumanoidIkTargetScope : IDisposable
        {
            private readonly GameObject[] objects;

            internal HumanoidIkSceneTargets Targets;

            private HumanoidIkTargetScope(GameObject[] objects, HumanoidIkSceneTargets targets)
            {
                this.objects = objects;
                Targets = targets;
            }

            internal static HumanoidIkTargetScope Create(
                HumanoidWorldIkTargets goals,
                out string error)
            {
                error = string.Empty;
                var objects = new GameObject[4];
                try
                {
                    HumanoidIkSceneTargets targets = default;
                    CreateTarget(goals.leftHand, goals.leftHandPosition, goals.leftHandRotation,
                        "__KimodoIkLeftHand", ref objects[0], ref targets.leftHandTransform, ref targets.leftHand);
                    CreateTarget(goals.rightHand, goals.rightHandPosition, goals.rightHandRotation,
                        "__KimodoIkRightHand", ref objects[1], ref targets.rightHandTransform, ref targets.rightHand);
                    CreateTarget(goals.leftFoot, goals.leftFootPosition, goals.leftFootRotation,
                        "__KimodoIkLeftFoot", ref objects[2], ref targets.leftFootTransform, ref targets.leftFoot);
                    CreateTarget(goals.rightFoot, goals.rightFootPosition, goals.rightFootRotation,
                        "__KimodoIkRightFoot", ref objects[3], ref targets.rightFootTransform, ref targets.rightFoot);
                    return new HumanoidIkTargetScope(objects, targets);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    for (int i = 0; i < objects.Length; i++)
                    {
                        if (objects[i] != null) UnityEngine.Object.DestroyImmediate(objects[i]);
                    }
                    return null;
                }
            }

            private static void CreateTarget(
                bool enabled,
                Vector3 position,
                Quaternion rotation,
                string name,
                ref GameObject targetObject,
                ref Transform targetTransform,
                ref bool targetEnabled)
            {
                if (!enabled) return;
                targetObject = new GameObject(name)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                targetTransform = targetObject.transform;
                targetTransform.SetPositionAndRotation(position, rotation);
                targetEnabled = true;
            }

            public void Dispose()
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (objects[i] != null) UnityEngine.Object.DestroyImmediate(objects[i]);
                }
            }
        }

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

        private struct HumanoidIkSolveJob : IAnimationJob
        {
            public bool solveLeftHand;
            public bool solveRightHand;
            public bool solveLeftFoot;
            public bool solveRightFoot;
            public TransformSceneHandle leftHandTarget;
            public TransformSceneHandle rightHandTarget;
            public TransformSceneHandle leftFootTarget;
            public TransformSceneHandle rightFootTarget;

            public void ProcessRootMotion(AnimationStream stream)
            {
            }

            public void ProcessAnimation(AnimationStream stream)
            {
                //return;
                if (!stream.isHumanStream)
                {
                    return;
                }

                AnimationHumanStream human = stream.AsHuman();
                bool leftHand = TryReadTarget(stream, leftHandTarget, solveLeftHand, out Vector3 leftHandPosition, out Quaternion leftHandRotation);
                bool rightHand = TryReadTarget(stream, rightHandTarget, solveRightHand, out Vector3 rightHandPosition, out Quaternion rightHandRotation);
                bool leftFoot = TryReadTarget(stream, leftFootTarget, solveLeftFoot, out Vector3 leftFootPosition, out Quaternion leftFootRotation);
                bool rightFoot = TryReadTarget(stream, rightFootTarget, solveRightFoot, out Vector3 rightFootPosition, out Quaternion rightFootRotation);

                if (leftHand)
                {
                    human.SetGoalPosition(AvatarIKGoal.LeftHand, leftHandPosition);
                    human.SetGoalRotation(AvatarIKGoal.LeftHand, leftHandRotation);
                }
                if (rightHand)
                {
                    human.SetGoalPosition(AvatarIKGoal.RightHand, rightHandPosition);
                    human.SetGoalRotation(AvatarIKGoal.RightHand, rightHandRotation);
                }
                if (leftFoot)
                {
                    human.SetGoalPosition(AvatarIKGoal.LeftFoot, leftFootPosition);
                    human.SetGoalRotation(AvatarIKGoal.LeftFoot, leftFootRotation);
                }
                if (rightFoot)
                {
                    human.SetGoalPosition(AvatarIKGoal.RightFoot, rightFootPosition);
                    human.SetGoalRotation(AvatarIKGoal.RightFoot, rightFootRotation);
                }

                human.SetGoalWeightPosition(AvatarIKGoal.LeftHand, leftHand ? 1f : 0f);
                human.SetGoalWeightRotation(AvatarIKGoal.LeftHand, leftHand ? 1f : 0f);
                human.SetGoalWeightPosition(AvatarIKGoal.RightHand, rightHand ? 1f : 0f);
                human.SetGoalWeightRotation(AvatarIKGoal.RightHand, rightHand ? 1f : 0f);
                human.SetGoalWeightPosition(AvatarIKGoal.LeftFoot, leftFoot ? 1f : 0f);
                human.SetGoalWeightRotation(AvatarIKGoal.LeftFoot, leftFoot ? 1f : 0f);
                human.SetGoalWeightPosition(AvatarIKGoal.RightFoot, rightFoot ? 1f : 0f);
                human.SetGoalWeightRotation(AvatarIKGoal.RightFoot, rightFoot ? 1f : 0f);
                if (!leftHand && !rightHand && !leftFoot && !rightFoot)
                {
                    return;
                }
                human.SolveIK();
            }

            private static bool TryReadTarget(
                AnimationStream stream,
                TransformSceneHandle handle,
                bool enabled,
                out Vector3 position,
                out Quaternion rotation)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                if (!enabled || !handle.IsValid(stream)) return false;
                position = handle.GetPosition(stream);
                rotation = handle.GetRotation(stream);
                return true;
            }
        }

        internal sealed class ClipSamplingContext : IDisposable
        {
            private bool disposed;

            public SkeletonCache cache;
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
                SkeletonCache cache,
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
                : KimodoMotionModelProfiles.DefaultFrameRate;
        }

        internal static bool TryBuildIkClipSamplingContext(
            AnimationClip clip,
            SkeletonCache cache,
            string rootName,
            ClipSamplingMode samplingMode,
            out ClipSamplingContext context,
            out string error,
            bool applyMotionXToDelta = true,
            bool solveLeftHandIk = false,
            bool solveRightHandIk = false,
            bool solveLeftFootIk = false,
            bool solveRightFootIk = false,
            HumanoidIkSceneTargets? sceneTargets = null)
        {
            return TryBuildClipSamplingContext(
                clip,
                cache,
                rootName,
                samplingMode,
                out context,
                out error,
                applyMotionXToDelta,
                applyFootIk: solveLeftFootIk || solveRightFootIk,
                solveHandIk: solveLeftHandIk || solveRightHandIk,
                solveLeftHandIk: solveLeftHandIk,
                solveRightHandIk: solveRightHandIk,
                solveLeftFootIk: solveLeftFootIk,
                solveRightFootIk: solveRightFootIk,
                sceneTargets: sceneTargets);
        }

        internal static bool TryBuildClipSamplingContext(
            AnimationClip clip,
            SkeletonCache cache,
            string rootName,
            ClipSamplingMode samplingMode,
            out ClipSamplingContext context,
            out string error,
            bool applyMotionXToDelta = true,
            bool applyFootIk = false,
            bool solveHandIk = false,
            bool solveLeftHandIk = false,
            bool solveRightHandIk = false,
            bool solveLeftFootIk = false,
            bool solveRightFootIk = false,
            HumanoidIkSceneTargets? sceneTargets = null)
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
                HumanoidIkSceneTargets resolvedSceneTargets = sceneTargets ?? default;
                HumanoidIkSceneHandles sceneHandles = HumanoidIkSceneHandles.Bind(
                    cache.animator,
                    resolvedSceneTargets);
                bool useLeftHandIk = sceneHandles.leftHand;
                bool useRightHandIk = sceneHandles.rightHand;
                AnimationClipPlayable clipPlayable = AnimationClipPlayable.Create(graph, clip);
                bool solveLeftFoot = sceneHandles.leftFoot;
                bool solveRightFoot = sceneHandles.rightFoot;
                // AnimationClipPlayable foot IK is an all-or-nothing switch.
                // Solve goals in the job below so a disabled side stays untouched.
                clipPlayable.SetApplyFootIK(false);
                clipPlayable.SetApplyPlayableIK(false);
                Playable sourcePlayable = clipPlayable;
                if (applyMotionXToDelta)
                {
                    sourcePlayable = AnimationOffsetPlayableAccess.CreateMotionXToDeltaAndConnect(
                        graph,
                        sourcePlayable);
                }
                if (useLeftHandIk || useRightHandIk || solveLeftFoot || solveRightFoot ||
                    sceneHandles.leftHand || sceneHandles.rightHand ||
                    sceneHandles.leftFoot || sceneHandles.rightFoot)
                {
                    AnimationScriptPlayable ikPlayable = AnimationScriptPlayable.Create(
                        graph,
                        new HumanoidIkSolveJob
                        {
                            solveLeftHand = useLeftHandIk || sceneHandles.leftHand,
                            solveRightHand = useRightHandIk || sceneHandles.rightHand,
                            solveLeftFoot = solveLeftFoot || sceneHandles.leftFoot,
                            solveRightFoot = solveRightFoot || sceneHandles.rightFoot,
                            leftHandTarget = sceneHandles.leftHandHandle,
                            rightHandTarget = sceneHandles.rightHandHandle,
                            leftFootTarget = sceneHandles.leftFootHandle,
                            rightFootTarget = sceneHandles.rightFootHandle
                        },
                        1);
                    graph.Connect(sourcePlayable, 0, ikPlayable, 0);
                    ikPlayable.SetInputWeight(0, 1f);
                    sourcePlayable = ikPlayable;
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
        private delegate bool ClipWriteCallback<TSample>(
            IReadOnlyList<TSample> samples,
            AnimationClip clip,
            out string error);

        internal static bool SampleBoneClipToBoneSample(
            AnimationClip clip,
            SkeletonCache cache,
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
            SkeletonCache cache,
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
            SkeletonCache cache,
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
            SkeletonCache cache,
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
            SkeletonCache targetCache,
            out BoneSample targetSample,
            out MuscleSample targetMuscleSample,
            out string error,
            bool solveLeftHandIk = false,
            bool solveRightHandIk = false,
            bool applyFootIk = false,
            bool solveLeftFootIk = false,
            bool solveRightFootIk = false,
            bool ikGoalsAlreadyInTargetSpace = false,
            KimodoRetargetClipSamplingUtility.HumanoidWorldIkTargets? worldIkTargets = null,
            KimodoRetargetClipSamplingUtility.HumanoidIkSceneTargets? sceneTargets = null)
        {
            targetSample = null;
            targetMuscleSample = null;
            error = string.Empty;
            if (sourceSample == null)
            {
                error = "Source muscle sample is null.";
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(targetCache, out error))
            {
                return false;
            }

            if (sceneTargets.HasValue && sceneTargets.Value.Any ||
                (worldIkTargets.HasValue && worldIkTargets.Value.Any && ikGoalsAlreadyInTargetSpace))
            {
                // The sample and goals already belong to targetCache's Avatar;
                // solve once there. Cross-Avatar callers must solve on their
                // source cache before entering the retarget path.
                return TrySolveMuscleSampleOnAvatar(
                    sourceSample,
                    frameRate,
                    targetCache,
                    out targetSample,
                    out targetMuscleSample,
                    out error,
                    solveLeftHandIk,
                    solveRightHandIk,
                    applyFootIk,
                    solveLeftFootIk,
                    solveRightFootIk,
                    worldIkTargets,
                    sceneTargets);
            }
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
            if (!TryApplyBoneSampleToSkeletonCache(targetSample, targetCache, out error))
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
        /// Applies a CharacterPose's local IK goals on its own Avatar, then
        /// captures the solved pose as a transient muscle + FootT/Q snapshot.
        /// The returned sample is transport data only; it never mutates the
        /// authored CharacterPose or constraint payload.
        /// </summary>
        internal static bool TrySolveMuscleSampleOnAvatar(
            MuscleSample sourceSample,
            float frameRate,
            SkeletonCache sourceCache,
            out BoneSample solvedBoneSample,
            out MuscleSample solvedMuscleSample,
            out string error,
            bool solveLeftHandIk = false,
            bool solveRightHandIk = false,
            bool applyFootIk = false,
            bool solveLeftFootIk = false,
            bool solveRightFootIk = false,
            KimodoRetargetClipSamplingUtility.HumanoidWorldIkTargets? worldIkTargets = null,
            KimodoRetargetClipSamplingUtility.HumanoidIkSceneTargets? sceneTargets = null)
        {
            solvedBoneSample = null;
            solvedMuscleSample = null;
            error = string.Empty;
            if (sourceSample == null)
            {
                error = "Source muscle sample is null.";
                return false;
            }
            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(sourceCache, out error))
            {
                return false;
            }

            bool shouldSolveIk = (worldIkTargets.HasValue && worldIkTargets.Value.Any) ||
                (sceneTargets.HasValue && sceneTargets.Value.Any);
            var clipSamples = new[] { sourceSample, sourceSample };
            AnimationClip clip = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context = null;
            KimodoRetargetClipSamplingUtility.HumanoidIkTargetScope targetScope = null;
            try
            {
                if (!TryCreateTransientMuscleClip(
                        clipSamples,
                        frameRate,
                        out clip,
                        out error,
                        includeHandIkGoals: false))
                {
                    return false;
                }

                KimodoRetargetClipSamplingUtility.HumanoidIkSceneTargets resolvedSceneTargets = sceneTargets ?? default;
                if (shouldSolveIk && !sceneTargets.HasValue && worldIkTargets.HasValue)
                {
                    KimodoRetargetClipSamplingUtility.HumanoidWorldIkTargets goals = worldIkTargets.Value;
                    if (goals.Any)
                    {
                        targetScope = KimodoRetargetClipSamplingUtility.HumanoidIkTargetScope.Create(goals, out error);
                        if (targetScope == null)
                        {
                            return false;
                        }
                        resolvedSceneTargets = targetScope.Targets;
                    }
                }

                bool builtContext = shouldSolveIk
                    ? KimodoRetargetClipSamplingUtility.TryBuildIkClipSamplingContext(
                        clip,
                        sourceCache,
                        "KimodoRetarget_SourceIkPoseClip",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out context,
                        out error,
                        applyMotionXToDelta: true,
                        solveLeftHandIk: solveLeftHandIk,
                        solveRightHandIk: solveRightHandIk,
                        solveLeftFootIk: solveLeftFootIk,
                        solveRightFootIk: solveRightFootIk,
                        sceneTargets: resolvedSceneTargets)
                    : KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
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

                // The solved skeleton gives us the post-IK muscle pose, but
                // it cannot be used to regenerate FootT/Q or HandT/Q.  Those
                // curve channels and the runtime IK end-effector pose are in
                // different spaces.  Keeping the authored curve values makes
                // the transport clip a valid muscle + FootT/Q pair while the
                // solved muscles carry the constraint result across Avatar
                // retargeting.
                solvedMuscleSample = CopyIkCurveChannels(sourceSample, solvedPoseSample);
                return true;
            }
            finally
            {
                context?.Dispose();
                if (clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
                targetScope?.Dispose();
            }
        }


        internal static bool TryRetargetMuscleSamplesToBoneSamples(
            IReadOnlyList<MuscleSample> sourceSamples,
            float frameRate,
            SkeletonCache targetCache,
            out BoneSample[] targetSamples,
            out string error,
            Func<AnimationClip, string, string> writebackClip = null)
        {
            // This is the transport boundary: source samples must already be
            // solved in their own Avatar. Retargeting only consumes the
            // matching muscle + FootT/Q snapshot and never solves it again.
            targetSamples = null;
            error = string.Empty;
            if (sourceSamples == null || sourceSamples.Count == 0)
            {
                error = "Source muscle samples are empty.";
                return false;
            }
            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(targetCache, out error))
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
                // FootT/Q is an Avatar-local IK-goal channel.  It must not be
                // copied from the source Avatar into the target retarget clip;
                // the target pose is captured below in the target Avatar's
                // own space instead.
                if (!TryCreateTransientMuscleClip(
                        clipSamples,
                        frameRate,
                        out clip,
                        out error,
                        includeFootIkGoals: false))
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
                            out error,
                            includeFootIkGoals: true))
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
            out string error,
            bool includeHandIkGoals = false,
            bool includeFootIkGoals = true)
        {
            return TryCreateTransientClip(
                samples,
                frameRate,
                "Muscle samples are empty.",
                includeHandIkGoals ? "KimodoTransientMuscleIkClip" :
                    includeFootIkGoals ? "KimodoTransientMuscleClip" : "KimodoTransientMuscleOnlyClip",
                includeHandIkGoals && includeFootIkGoals
                    ? KimodoRetargetClipWriter.WriteMuscleCurvesWithIkGoals
                    : includeFootIkGoals
                        ? KimodoRetargetClipWriter.WriteMuscleCurves
                        : KimodoRetargetClipWriter.WriteMuscleCurvesWithoutIkGoals,
                out clip,
                out error);
        }

        private static MuscleSample CopyIkCurveChannels(
            MuscleSample sourceSample,
            MuscleSample solvedPoseSample)
        {
            solvedPoseSample.leftFootPosition = sourceSample.leftFootPosition;
            solvedPoseSample.leftFootRotation = sourceSample.leftFootRotation;
            solvedPoseSample.rightFootPosition = sourceSample.rightFootPosition;
            solvedPoseSample.rightFootRotation = sourceSample.rightFootRotation;
            solvedPoseSample.leftHandPosition = sourceSample.leftHandPosition;
            solvedPoseSample.leftHandRotation = sourceSample.leftHandRotation;
            solvedPoseSample.rightHandPosition = sourceSample.rightHandPosition;
            solvedPoseSample.rightHandRotation = sourceSample.rightHandRotation;
            return solvedPoseSample;
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
            SkeletonCache cache,
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
            SkeletonCache cache,
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

        internal static MuscleSample CloneMuscleSample(MuscleSample source)
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


    }
}
