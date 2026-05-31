using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using KimodoUnityMotionTools.Generation.Pipeline;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public enum KimodoRetargetResultMode
    {
        TargetBone = 1
    }

    public sealed class KimodoRetargetPreviewFrame
    {
        public HumanPose sourcePose;
        public HumanPose targetPose;
        public KimodoRetargetTools.BoneFrame targetBoneFrame;
        public string[] boneNames;
    }

    public sealed class KimodoRetargetDebugFrame
    {
        public HumanPose sourcePose;
        public HumanPose originBonePose;
        public HumanPose originMusclePose;
        public HumanPose targetMusclePose;
        public HumanPose targetBonePose;
        public KimodoRetargetTools.BoneFrame originBoneFrame;
        public KimodoRetargetTools.BoneFrame originMuscleFrame;
        public KimodoRetargetTools.BoneFrame targetMuscleFrame;
        public KimodoRetargetTools.BoneFrame targetBoneFrame;
    }

    public static partial class KimodoRetargetPipeline
    {
        private static bool IsMuscleAnimationClip(AnimationClip clip)
        {
            return clip != null && clip.isHumanMotion;
        }

        public static bool TryRetargetClip(
            AnimationClip sourceClip,
            Avatar originAvatar,
            Avatar targetAvatar,
            out AnimationClip outputClip,
            out KimodoRetargetResultMode mode,
            out string details)
        {
            outputClip = null;
            mode = KimodoRetargetResultMode.TargetBone;
            details = string.Empty;

            if (!TryBuildSourceMuscleData(sourceClip, originAvatar, out IGetMuscleData muscleData, out details))
            {
                return false;
            }

            try
            {
                if (!KimodoRetargetTools.TryRetarget(
                        muscleData,
                        targetAvatar,
                        sourceClip != null && sourceClip.frameRate > 0f ? sourceClip.frameRate : 30f,
                        sourceClip != null ? sourceClip.length : 0f,
                        out KimodoRetargetTools.RetargetResult result,
                        out details))
                {
                    return false;
                }

                outputClip = new AnimationClip
                {
                    frameRate = sourceClip != null && sourceClip.frameRate > 0f ? sourceClip.frameRate : 30f,
                    legacy = false
                };

                if (!TryWriteRetargetResultToClip(result, outputClip, out details))
                {
                    return false;
                }

                details = "Retarget ok (new core).";
                return true;
            }
            finally
            {
                if (muscleData is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        public static bool TryRetargetBakedClip(
            AnimationClip targetClip,
            Avatar originAvatar,
            Avatar targetAvatar,
            out AnimationClip outputClip,
            out KimodoRetargetResultMode mode,
            out string details)
        {
            return TryRetargetClip(targetClip, originAvatar, targetAvatar, out outputClip, out mode, out details);
        }

        public static bool TryBuildPreviewFrame(
            AnimationClip sourceClip,
            Avatar originAvatar,
            Avatar targetAvatar,
            float sampleTime,
            out KimodoRetargetPreviewFrame preview,
            out string error)
        {
            preview = null;
            error = string.Empty;

            if (!TryBuildSourceMuscleData(sourceClip, originAvatar, out IGetMuscleData muscleData, out error))
            {
                return false;
            }

            try
            {
                if (!TrySampleSourcePose(muscleData, sampleTime, out HumanPose sourcePose, out error))
                {
                    return false;
                }

                if (!KimodoRetargetTools.TryCopyHumanPose(sourcePose, out HumanPose copiedSourcePose))
                {
                    error = "Failed to copy source pose.";
                    return false;
                }

                var constantSource = new ConstantPoseMuscleData(copiedSourcePose);
                if (!KimodoRetargetTools.TryRetarget(
                        constantSource,
                        targetAvatar,
                        sourceClip != null && sourceClip.frameRate > 0f ? sourceClip.frameRate : 30f,
                        sourceClip != null ? sourceClip.length : 0f,
                        out KimodoRetargetTools.RetargetResult result,
                        out error))
                {
                    return false;
                }

                if (result == null || result.frames == null || result.frames.Length == 0 || result.poses == null || result.poses.Length == 0)
                {
                    error = "Preview retarget result is empty.";
                    return false;
                }

                preview = new KimodoRetargetPreviewFrame
                {
                    sourcePose = copiedSourcePose,
                    targetPose = result.poses[0],
                    targetBoneFrame = result.frames[0],
                    boneNames = result.boneNames
                };
                return true;
            }
            finally
            {
                if (muscleData is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        public static bool TryCreateRetargetDebugContext(
            AnimationClip sourceClip,
            Avatar originAvatar,
            Avatar targetAvatar,
            out KimodoRetargetDebugContext context,
            out string error)
        {
            context = null;
            error = string.Empty;

            if (!TryBuildSourceMuscleData(sourceClip, originAvatar, out IGetMuscleData muscleData, out error))
            {
                return false;
            }

            try
            {
                bool isMuscleClip = IsMuscleAnimationClip(sourceClip);
                KimodoRetargetStageCache originBone = null;
                KimodoRetargetStageCache originMuscle = null;

                if (!isMuscleClip && !TryBuildDebugStage(originAvatar, "OriginBone", out originBone, out error))
                {
                    return false;
                }

                if (!isMuscleClip && !TryBuildDebugStage(originAvatar, "OriginMuscle", out originMuscle, out error))
                {
                    return false;
                }

                if (!TryBuildDebugStage(targetAvatar, "TargetMuscle", out KimodoRetargetStageCache targetMuscle, out error))
                {
                    return false;
                }

                if (!TryBuildDebugStage(targetAvatar, "TargetBone", out KimodoRetargetStageCache targetBone, out error))
                {
                    return false;
                }

                context = new KimodoRetargetDebugContext
                {
                    sourceMuscleData = muscleData,
                    originBone = originBone,
                    originMuscle = originMuscle,
                    targetMuscle = targetMuscle,
                    targetBone = targetBone
                };

                muscleData = null;
                return true;
            }
            finally
            {
                if (muscleData is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        
        public static bool TryUpdateRetargetDebugFrame(
            KimodoRetargetDebugContext context,
            AnimationClip sourceClip,
            Avatar targetAvatar,
            float sampleTime,
            out KimodoRetargetDebugFrame frame,
            out string error)
        {
            frame = null;
            error = string.Empty;

            if (context == null || context.sourceMuscleData == null)
            {
                error = "Debug context is null.";
                return false;
            }

            if (!TrySampleSourcePose(context.sourceMuscleData, sampleTime, out HumanPose sourcePose, out error))
            {
                return false;
            }

            if (!KimodoRetargetTools.TryCopyHumanPose(sourcePose, out HumanPose copiedSourcePose))
            {
                error = "Failed to copy source pose.";
                return false;
            }

            bool isMuscleClip = IsMuscleAnimationClip(sourceClip);

            KimodoRetargetTools.BoneFrame originBoneFrame = null;
            KimodoRetargetTools.BoneFrame originMuscleFrame = null;
            if (!isMuscleClip)
            {
                if (context.originBone != null && !ApplyPoseToStage(context.originBone, copiedSourcePose, out originBoneFrame, out error))
                {
                    return false;
                }

                if (context.originMuscle != null && !ApplyPoseToStage(context.originMuscle, copiedSourcePose, out originMuscleFrame, out error))
                {
                    return false;
                }
            }

            HumanPose targetPose = copiedSourcePose;
            KimodoRetargetTools.BoneFrame targetMuscleFrame = null;
            KimodoRetargetTools.BoneFrame targetBoneFrame = null;
            
            if (isMuscleClip)
            {
                if (!ApplyPoseToStage(context.targetMuscle, targetPose, out targetMuscleFrame, out error))
                {
                    return false;
                }

                if (!TryReadPoseFromStage(context.targetMuscle, out targetPose, out error))
                {
                    return false;
                }

                if (!ApplyPoseToStage(context.targetBone, targetPose, out targetBoneFrame, out error))
                {
                    return false;
                }
            }
            else
            {
                if (!KimodoRetargetTools.TryRetargetPose(copiedSourcePose, targetAvatar, out KimodoRetargetTools.RetargetResult retargetResult, out error))
                {
                    return false;
                }

                if (retargetResult == null || retargetResult.poses == null || retargetResult.poses.Length == 0)
                {
                    error = "Retarget result is empty.";
                    return false;
                }

                targetPose = retargetResult.poses[0];
                if (!ApplyPoseToStage(context.targetMuscle, targetPose, out targetMuscleFrame, out error))
                {
                    return false;
                }

                if (!ApplyPoseToStage(context.targetBone, targetPose, out targetBoneFrame, out error))
                {
                    return false;
                }
            }

            frame = new KimodoRetargetDebugFrame
            {
                sourcePose = copiedSourcePose,
                originBonePose = context.originBone != null ? copiedSourcePose : default,
                originMusclePose = context.originMuscle != null ? copiedSourcePose : default,
                targetMusclePose = targetPose,
                targetBonePose = targetPose,
                originBoneFrame = originBoneFrame,
                originMuscleFrame = originMuscleFrame,
                targetMuscleFrame = targetMuscleFrame,
                targetBoneFrame = targetBoneFrame
            };
            return true;
        }

        public static void DestroyRetargetDebugContext(KimodoRetargetDebugContext context)
        {
            if (context == null)
            {
                return;
            }

            if (context.sourceMuscleData is IDisposable disposable)
            {
                disposable.Dispose();
            }

            context.sourceMuscleData = null;
            DestroyStage(context.originBone);
            DestroyStage(context.originMuscle);
            DestroyStage(context.targetMuscle);
            DestroyStage(context.targetBone);
        }

        private static bool TryBuildSourceMuscleData(
            AnimationClip sourceClip,
            Avatar originAvatar,
            out IGetMuscleData muscleData,
            out string error)
        {
            muscleData = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!IsValidHumanoidAvatar(originAvatar))
            {
                error = "Origin avatar is null/invalid/non-humanoid.";
                return false;
            }

            muscleData = new ClipMuscleData(sourceClip, originAvatar);
            return true;
        }

        private static bool TrySampleSourcePose(IGetMuscleData sourceMuscleData, float sampleTime, out HumanPose pose, out string error)
        {
            pose = new HumanPose();
            error = string.Empty;
            return sourceMuscleData != null && sourceMuscleData.TryGetPose(sampleTime, ref pose, out error);
        }

        private static bool TryBuildDebugStage(Avatar avatar, string stageName, out KimodoRetargetStageCache cache, out string error)
        {
            cache = new KimodoRetargetStageCache();
            error = string.Empty;

            if (!IsValidHumanoidAvatar(avatar))
            {
                error = $"Avatar is null/invalid/non-humanoid for stage '{stageName}'.";
                return false;
            }

            GameObject root = new GameObject(stageName);
            root.hideFlags = HideFlags.None;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, root.transform, out error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            string skeletonRootName = KimodoRuntimeAvatarSkeletonBuilder.ResolveSkeletonRootName(avatar);
            Transform skeletonRoot = FindTransformByName(root.transform, skeletonRootName);
            if (skeletonRoot == null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                error = $"Failed to resolve skeleton root for stage '{stageName}'.";
                return false;
            }

            SetHierarchyHideFlags(root.transform, HideFlags.None);

            Animator animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }

            animator.avatar = avatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.enabled = false;
            animator.Rebind();
            animator.Update(0f);

            root.transform.position = GetStageWorldOffset(stageName);

            cache.root = root;
            cache.avatar = avatar;
            cache.poseHandler = new HumanPoseHandler(avatar, skeletonRoot);
            var gizmo = root.AddComponent<KimodoTransformGizmoVisualizer>();
            gizmo.pointRadius = 0.015f;
            gizmo.drawNames = false;
            gizmo.drawOnlyWhenSelected = false;
            cache.bonePaths = BuildBonePaths(root.transform);
            cache.boneTransforms = BuildBoneTransforms(root.transform, cache.bonePaths);
            cache.boneFrame = new KimodoRetargetTools.BoneFrame
            {
                boneNames = cache.bonePaths,
                localPositions = new Vector3[cache.bonePaths.Length],
                localRotations = new Quaternion[cache.bonePaths.Length]
            };
            return true;
        }

        private static string[] BuildBonePaths(Transform root)
        {
            Transform[] all = root != null ? root.GetComponentsInChildren<Transform>(true) : Array.Empty<Transform>();
            var names = new System.Collections.Generic.List<string>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                string path = AnimationUtility.CalculateTransformPath(all[i], root);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                names.Add(path);
            }

            return names.ToArray();
        }

        private static Transform[] BuildBoneTransforms(Transform root, string[] bonePaths)
        {
            var transforms = new Transform[bonePaths.Length];
            for (int i = 0; i < bonePaths.Length; i++)
            {
                transforms[i] = FindByPath(root, bonePaths[i]);
            }

            return transforms;
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
                GameObject go = all[i].gameObject;
                if (go != null)
                {
                    go.hideFlags = hideFlags;
                }
            }
        }

        private static void DestroyStage(KimodoRetargetStageCache cache)
        {
            if (cache?.root != null)
            {
                UnityEngine.Object.DestroyImmediate(cache.root);
            }
        }

        private static bool ApplyPoseToStage(KimodoRetargetStageCache cache, HumanPose pose, out KimodoRetargetTools.BoneFrame frame, out string error)
        {
            frame = null;
            error = string.Empty;

            if (cache == null || cache.root == null || cache.poseHandler == null)
            {
                error = "Stage cache is not initialized.";
                return false;
            }

            try
            {
                cache.pose = pose;
                cache.poseHandler.SetHumanPose(ref cache.pose);
                frame = CaptureBoneFrame(cache);
                cache.boneFrame = frame;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadPoseFromStage(KimodoRetargetStageCache cache, out HumanPose pose, out string error)
        {
            pose = new HumanPose();
            error = string.Empty;

            if (cache == null || cache.poseHandler == null)
            {
                error = "Stage cache is not initialized.";
                return false;
            }

            try
            {
                cache.poseHandler.GetHumanPose(ref pose);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static KimodoRetargetTools.BoneFrame CaptureBoneFrame(KimodoRetargetStageCache cache)
        {
            var frame = new KimodoRetargetTools.BoneFrame
            {
                boneNames = cache.bonePaths,
                localPositions = new Vector3[cache.bonePaths.Length],
                localRotations = new Quaternion[cache.bonePaths.Length]
            };

            for (int i = 0; i < cache.boneTransforms.Length; i++)
            {
                Transform t = cache.boneTransforms[i];
                if (t == null)
                {
                    continue;
                }

                frame.localPositions[i] = t.localPosition;
                frame.localRotations[i] = t.localRotation;
            }

            return frame;
        }

        private static Transform FindByPath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (string.Equals(root.name, path, System.StringComparison.Ordinal))
            {
                return root;
            }

            string[] segments = path.Split('/');
            Transform current = root;
            for (int i = 0; i < segments.Length; i++)
            {
                if (current == null)
                {
                    return null;
                }

                if (i == 0 && string.Equals(current.name, segments[i], System.StringComparison.Ordinal))
                {
                    continue;
                }

                current = current.Find(segments[i]);
            }

            return current;
        }

        private static Transform FindTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform candidate = all[i];
                if (candidate != null && string.Equals(candidate.name, name, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static Vector3 GetStageWorldOffset(string stageName)
        {
            switch (stageName)
            {
                case "OriginBone":
                    return new Vector3(-3f, 0f, 0f);
                case "OriginMuscle":
                    return new Vector3(-1f, 0f, 0f);
                case "TargetMuscle":
                    return new Vector3(1f, 0f, 0f);
                case "TargetBone":
                    return new Vector3(3f, 0f, 0f);
                default:
                    return Vector3.zero;
            }
        }

        private static bool IsValidHumanoidAvatar(Avatar avatar)
        {
            return KimodoRetargetTools.IsValidHumanoid(avatar);
        }

        private static bool TryWriteRetargetResultToClip(KimodoRetargetTools.RetargetResult result, AnimationClip clip, out string error)
        {
            error = string.Empty;
            if (result == null || result.frames == null || result.frames.Length == 0)
            {
                error = "Retarget result is empty.";
                return false;
            }

            clip.ClearCurves();
            string[] boneNames = result.boneNames ?? Array.Empty<string>();
            for (int i = 0; i < boneNames.Length; i++)
            {
                string path = boneNames[i];
                var posX = new AnimationCurve();
                var posY = new AnimationCurve();
                var posZ = new AnimationCurve();
                var rotX = new AnimationCurve();
                var rotY = new AnimationCurve();
                var rotZ = new AnimationCurve();
                var rotW = new AnimationCurve();

                for (int frame = 0; frame < result.frames.Length; frame++)
                {
                    float t = clip.frameRate > 0f ? frame / clip.frameRate : frame / 30f;
                    KimodoRetargetTools.BoneFrame f = result.frames[frame];
                    if (f == null || f.localPositions == null || f.localRotations == null || i >= f.localPositions.Length || i >= f.localRotations.Length)
                    {
                        continue;
                    }

                    Vector3 lp = f.localPositions[i];
                    Quaternion lr = f.localRotations[i];
                    posX.AddKey(t, lp.x);
                    posY.AddKey(t, lp.y);
                    posZ.AddKey(t, lp.z);
                    rotX.AddKey(t, lr.x);
                    rotY.AddKey(t, lr.y);
                    rotZ.AddKey(t, lr.z);
                    rotW.AddKey(t, lr.w);
                }

                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", posX);
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", posY);
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", posZ);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rotX);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rotY);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rotZ);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rotW);
            }

            clip.EnsureQuaternionContinuity();
            return true;
        }

        private sealed class ClipMuscleData : IGetMuscleData
        {
            private readonly AnimationClip clip;
            private readonly Avatar avatar;
            private readonly GameObject tempRoot;
            private readonly Transform skeletonRoot;
            private readonly Animator animator;
            private readonly PlayableGraph graph;
            private readonly AnimationClipPlayable clipPlayable;
            private readonly AnimationPlayableOutput output;
            private HumanPoseHandler handler;
            private bool disposed;

            public ClipMuscleData(AnimationClip clip, Avatar avatar)
            {
                this.clip = clip;
                this.avatar = avatar;
                tempRoot = new GameObject("KimodoRetargetPipeline_ClipMuscleData");
                tempRoot.hideFlags = HideFlags.HideAndDontSave;
                tempRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                tempRoot.transform.localScale = Vector3.one;

                if (KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, tempRoot.transform, out _))
                {
                    string skeletonRootName = KimodoRuntimeAvatarSkeletonBuilder.ResolveSkeletonRootName(avatar);
                    skeletonRoot = FindTransformByName(tempRoot.transform, skeletonRootName);
                    if (skeletonRoot == null)
                    {
                        return;
                    }

                    animator = tempRoot.AddComponent<Animator>();
                    animator.avatar = avatar;
                    animator.applyRootMotion = false;
                    animator.enabled = true;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.Rebind();
                    animator.Update(0f);

                    graph = PlayableGraph.Create("KimodoRetargetPipeline_ClipMuscleData");
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    clipPlayable = AnimationClipPlayable.Create(graph, clip);
                    clipPlayable.SetApplyFootIK(false);
                    output = AnimationPlayableOutput.Create(graph, "KimodoRetargetPipeline_ClipMuscleDataOutput", animator);
                    output.SetSourcePlayable(clipPlayable);
                    graph.Play();
                }
            }

            public bool TryGetPoseHandle(out HumanPoseHandler poseHandle, out string error)
            {
                error = string.Empty;
                if (disposed)
                {
                    poseHandle = null;
                    error = "Pose data provider disposed.";
                    return false;
                }

                if (handler == null)
                {
                    if (animator == null || skeletonRoot == null)
                    {
                        poseHandle = null;
                        error = "Pose handle is not initialized.";
                        return false;
                    }

                    handler = new HumanPoseHandler(avatar, skeletonRoot);
                }

                poseHandle = handler;
                return true;
            }

            public bool TryGetPose(float time, ref HumanPose pose, out string error)
            {
                error = string.Empty;
                if (disposed)
                {
                    error = "Pose data provider disposed.";
                    return false;
                }

                if (clip == null || tempRoot == null || animator == null || skeletonRoot == null)
                {
                    error = "Source clip or sampler rig is null.";
                    return false;
                }

                try
                {
                    if (handler == null)
                    {
                        handler = new HumanPoseHandler(avatar, skeletonRoot);
                    }

                    if (!graph.IsValid())
                    {
                        error = "Playable graph is not initialized.";
                        return false;
                    }

                    clipPlayable.SetTime(Mathf.Max(0f, time));
                    graph.Evaluate(0);
                    handler.GetHumanPose(ref pose);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (graph.IsValid())
                {
                    graph.Destroy();
                }

                if (tempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempRoot);
                }
            }
        }

        private sealed class ConstantPoseMuscleData : IGetMuscleData
        {
            private readonly HumanPose pose;

            public ConstantPoseMuscleData(HumanPose pose)
            {
                this.pose = pose;
            }

            public bool TryGetPoseHandle(out HumanPoseHandler poseHandle, out string error)
            {
                poseHandle = null;
                error = "Constant pose provider does not expose a pose handle.";
                return false;
            }

            public bool TryGetPose(float time, ref HumanPose output, out string error)
            {
                error = string.Empty;
                output.bodyPosition = pose.bodyPosition;
                output.bodyRotation = pose.bodyRotation;
                output.muscles = pose.muscles != null ? (float[])pose.muscles.Clone() : Array.Empty<float>();
                return true;
            }
        }
    }
}
