using KimodoUnityMotionTools.Generation.Pipeline;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public enum KimodoRetargetResultMode
    {
        SomaFallback = 0,
        HumanoidMuscle = 1,
        TargetBone = 2
    }

    public static partial class KimodoRetargetPipeline
    {
        private readonly struct RetargetContext
        {
            public readonly AnimationClip SourceSomaBoneClip;
            public readonly Animator TargetAnimator;
            public readonly Avatar EnsuredAvatar;
            public readonly string AvatarSource;
            public readonly bool HadHumanoidAvatar;

            public RetargetContext(
                AnimationClip sourceSomaBoneClip,
                Animator targetAnimator,
                Avatar ensuredAvatar,
                string avatarSource,
                bool hadHumanoidAvatar)
            {
                SourceSomaBoneClip = sourceSomaBoneClip;
                TargetAnimator = targetAnimator;
                EnsuredAvatar = ensuredAvatar;
                AvatarSource = avatarSource;
                HadHumanoidAvatar = hadHumanoidAvatar;
            }
        }

        internal static bool TryRetargetClipToAvatar(
            AnimationClip sourceSomaClip,
            Avatar targetAvatar,
            out AnimationClip outputClip,
            out string details)
        {
            outputClip = null;
            details = string.Empty;

            if (sourceSomaClip == null)
            {
                details = "Source clip is null.";
                return false;
            }

            if (targetAvatar == null || !targetAvatar.isValid || !targetAvatar.isHuman)
            {
                details = "Custom avatar is null or invalid humanoid avatar.";
                return false;
            }

            if (!TryCreateSomaSamplingAnimator(null, out Animator somaAnimator, out GameObject somaTempRoot, out string somaError))
            {
                details = $"Ensure SOMA avatar failed: {somaError}";
                return false;
            }

            try
            {
                if (!TryBuildSomaMuscleClip(sourceSomaClip, somaAnimator, out AnimationClip muscleClip, out string muscleError))
                {
                    details = $"SOMA->Muscle failed: {muscleError}";
                    return false;
                }

                outputClip = new AnimationClip
                {
                    name = $"{sourceSomaClip.name}_RetargetCustomAvatar",
                    legacy = false,
                    frameRate = sourceSomaClip.frameRate > 0f ? sourceSomaClip.frameRate : 30f
                };

                Animator targetSamplingAnimator = CreateTempAnimatorForAvatarObject(targetAvatar, out GameObject targetTempRoot);
                if (targetSamplingAnimator == null)
                {
                    details = "Failed to create sampling animator from custom avatar.";
                    return false;
                }

                try
                {
                    if (!TryConvertMuscleToTargetBoneClip(
                            muscleClip,
                            targetSamplingAnimator,
                            outputClip,
                            out string toBoneError,
                            sourceSomaClip.frameRate))
                    {
                        details = $"Muscle->TargetBone failed: {toBoneError}";
                        return false;
                    }
                }
                finally
                {
                    if (targetTempRoot != null)
                    {
                        UnityEngine.Object.DestroyImmediate(targetTempRoot);
                    }
                }

                details = "Retarget ok (Mode=CustomAvatar).";
                return true;
            }
            finally
            {
                if (somaTempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(somaTempRoot);
                }
            }
        }

        public static bool TryRetargetBakedClip(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip,
            Avatar explicitAvatar,
            out KimodoRetargetResultMode mode,
            out string details)
        {
            mode = KimodoRetargetResultMode.SomaFallback;
            details = string.Empty;

            if (!TryPrepareRetargetContext(playableClip, timelineClip, explicitAvatar, out RetargetContext context, out mode, out details))
            {
                return false;
            }

            if (!TryCreateSomaSamplingAnimator(playableClip, out Animator somaAnimator, out GameObject somaTempRoot, out string somaError))
            {
                details = $"Ensure SOMA avatar failed: {somaError}";
                return false;
            }

            try
            {
                if (!TryBuildSomaMuscleClip(context.SourceSomaBoneClip, somaAnimator, out AnimationClip muscleClip, out string muscleError))
                {
                    details = $"SOMA->Muscle failed: {muscleError}";
                    return false;
                }

                if (context.HadHumanoidAvatar)
                {
                    OverwriteClipCurves(playableClip.clip, muscleClip);
                    mode = KimodoRetargetResultMode.HumanoidMuscle;
                    details = $"Retarget ok (Avatar={context.AvatarSource}, Mode=HumanoidMuscle).";
                    return true;
                }

                if (!TryBuildTargetBoneClipFromMuscle(
                        context,
                        muscleClip,
                        out AnimationClip targetBoneClip,
                        out string toBoneError))
                {
                    details = $"Muscle->TargetBone failed: {toBoneError}";
                    return false;
                }

                OverwriteClipCurves(playableClip.clip, targetBoneClip);
                mode = KimodoRetargetResultMode.TargetBone;
                details = $"Retarget ok (Avatar={context.AvatarSource}, Mode=TargetBone).";
                return true;
            }
            catch (Exception e)
            {
                details = $"Retarget exception: {e.Message}";
                return false;
            }
            finally
            {
                if (somaTempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(somaTempRoot);
                }
            }
        }

        private static bool TryPrepareRetargetContext(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip,
            Avatar explicitAvatar,
            out RetargetContext context,
            out KimodoRetargetResultMode mode,
            out string details)
        {
            context = default;
            mode = KimodoRetargetResultMode.SomaFallback;
            details = string.Empty;

            if (playableClip == null || playableClip.clip == null)
            {
                details = "PlayableClip or animation clip is null.";
                return false;
            }

            if (explicitAvatar == null || !explicitAvatar.isValid || !explicitAvatar.isHuman)
            {
                details = "Explicit retarget avatar is null or invalid humanoid avatar.";
                return false;
            }

            bool hadHumanoidAvatar = false;
            string avatarSource = "ExplicitAvatar";
            context = new RetargetContext(playableClip.clip, null, explicitAvatar, avatarSource, hadHumanoidAvatar);
            return true;
        }

        private static bool TryBuildSomaMuscleClip(
            AnimationClip sourceSomaBoneClip,
            Animator somaAnimator,
            out AnimationClip muscleClip,
            out string error)
        {
            muscleClip = new AnimationClip
            {
                name = $"{sourceSomaBoneClip.name}_Muscle",
                legacy = false,
                frameRate = sourceSomaBoneClip.frameRate > 0f ? sourceSomaBoneClip.frameRate : 30f
            };

            return TryConvertBoneClipToMuscleByAvatar(
                sourceSomaBoneClip,
                somaAnimator,
                muscleClip,
                out error,
                sourceSomaBoneClip.frameRate);
        }

        private static bool TryBuildTargetBoneClipFromMuscle(
            RetargetContext context,
            AnimationClip muscleClip,
            out AnimationClip targetBoneClip,
            out string error)
        {
            targetBoneClip = new AnimationClip
            {
                name = $"{context.SourceSomaBoneClip.name}_TargetBone",
                legacy = false,
                frameRate = context.SourceSomaBoneClip.frameRate > 0f ? context.SourceSomaBoneClip.frameRate : 30f
            };

            error = string.Empty;
            Animator targetSamplingAnimator = CreateTempAnimatorForAvatarObject(context.EnsuredAvatar, out GameObject targetTempRoot);
            if (targetSamplingAnimator == null)
            {
                error = "Failed to create target sampling animator.";
                return false;
            }

            try
            {
                return TryConvertMuscleToTargetBoneClip(
                    muscleClip,
                    targetSamplingAnimator,
                    targetBoneClip,
                    out error,
                    context.SourceSomaBoneClip.frameRate);
            }
            finally
            {
                if (targetTempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(targetTempRoot);
                }
            }
        }


        private static bool TryResolveBoundAnimator(TimelineClip timelineClip, out Animator animator, out string error)
        {
            animator = null;
            error = string.Empty;

            if (timelineClip == null)
            {
                error = "Timeline clip is null.";
                return false;
            }

            TrackAsset track = timelineClip.GetParentTrack();
            if (track == null)
            {
                error = "Timeline parent track not found.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            TrackAsset currentTrack = track;
            while (currentTrack != null)
            {
                animator = director.GetGenericBinding(currentTrack) as Animator;
                if (animator != null)
                {
                    return true;
                }

                currentTrack = currentTrack.parent as TrackAsset;
            }

            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            return true;
        }

        private static bool TryCreateSomaSamplingAnimator(KimodoPlayableClip playableClip, out Animator animator, out GameObject tempRoot, out string error)
        {
            animator = null;
            tempRoot = null;
            error = string.Empty;

            string modelName = playableClip != null ? playableClip.bridgeModelName : string.Empty;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar runtimeAvatar, out string loadError))
            {
                error = loadError;
                return false;
            }
            string avatarResourceName = KimodoRuntimeAvatarSkeletonBuilder.ResolveAvatarResourceName(modelName);

            tempRoot = new GameObject($"KimodoSomaSampling_{avatarResourceName}");
            tempRoot.hideFlags = HideFlags.HideAndDontSave;
            tempRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            tempRoot.transform.localScale = Vector3.one;

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(runtimeAvatar, tempRoot.transform, out string hierarchyError))
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                tempRoot = null;
                error = hierarchyError;
                return false;
            }

            Transform samplingRoot = ResolveBuiltAvatarSkeletonRoot(tempRoot.transform, runtimeAvatar);
            if (samplingRoot == null)
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                tempRoot = null;
                error = "Failed to resolve built avatar skeleton root.";
                return false;
            }

            animator = samplingRoot.gameObject.AddComponent<Animator>();
            animator.avatar = runtimeAvatar;

            animator.enabled = false;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Update(0f);
            return true;
        }

        private static Transform ResolveBuiltAvatarSkeletonRoot(Transform hierarchyRoot, Avatar avatar)
        {
            if (hierarchyRoot == null)
            {
                return null;
            }

            string expectedRootName = KimodoRuntimeAvatarSkeletonBuilder.ResolveSkeletonRootName(avatar);
            if (!string.IsNullOrWhiteSpace(expectedRootName))
            {
                if (string.Equals(hierarchyRoot.name, expectedRootName, StringComparison.Ordinal))
                {
                    return hierarchyRoot;
                }

                Transform directChild = hierarchyRoot.Find(expectedRootName);
                if (directChild != null)
                {
                    return directChild;
                }

                Transform[] all = hierarchyRoot.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    if (t != null && string.Equals(t.name, expectedRootName, StringComparison.Ordinal))
                    {
                        return t;
                    }
                }
            }

            if (hierarchyRoot.childCount > 0)
            {
                return hierarchyRoot.GetChild(0);
            }

            return hierarchyRoot;
        }

        private static int CopyLocalPoseByPathForSampling(Transform sourceRoot, Transform dstRoot)
        {
            if (sourceRoot == null || dstRoot == null)
            {
                return 0;
            }

            var sourceByPath = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Transform[] sourceAll = sourceRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < sourceAll.Length; i++)
            {
                Transform t = sourceAll[i];
                string path = AnimationUtility.CalculateTransformPath(t, sourceRoot) ?? string.Empty;
                if (!sourceByPath.ContainsKey(path))
                {
                    sourceByPath[path] = t;
                }
            }

            int copied = 0;
            Transform[] dstAll = dstRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < dstAll.Length; i++)
            {
                Transform dst = dstAll[i];
                string path = AnimationUtility.CalculateTransformPath(dst, dstRoot) ?? string.Empty;
                if (!sourceByPath.TryGetValue(path, out Transform src) || src == null)
                {
                    continue;
                }

                dst.localPosition = src.localPosition;
                dst.localRotation = src.localRotation;
                copied++;
            }

            return copied;
        }

        private static Animator CreateTempAnimatorForAvatar(
            Animator sourceAnimator,
            Avatar avatar,
            out GameObject tempRoot,
            bool keepCurrentPose = false)
        {
            tempRoot = null;
            if (sourceAnimator == null)
            {
                return null;
            }

            tempRoot = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
            tempRoot.hideFlags = HideFlags.HideAndDontSave;
            tempRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            tempRoot.transform.localScale = Vector3.one;

            Animator tempAnimator = tempRoot.GetComponent<Animator>();
            if (tempAnimator == null)
            {
                return null;
            }

            tempAnimator.avatar = avatar;
            tempAnimator.enabled = false;
            tempAnimator.applyRootMotion = false;
            if (!keepCurrentPose)
            {
                tempAnimator.Rebind();
                tempAnimator.Update(0f);
            }
            return tempAnimator;
        }

        private static Animator CreateTempAnimatorForAvatarObject(Avatar avatar, out GameObject tempRoot)
        {
            tempRoot = null;
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                return null;
            }

            tempRoot = new GameObject("KimodoCustomAvatarSamplingRoot");
            tempRoot.hideFlags = HideFlags.HideAndDontSave;
            tempRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            tempRoot.transform.localScale = Vector3.one;

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, tempRoot.transform, out _))
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                tempRoot = null;
                return null;
            }

            Transform samplingRoot = ResolveBuiltAvatarSkeletonRoot(tempRoot.transform, avatar);
            if (samplingRoot == null)
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                tempRoot = null;
                return null;
            }

            Animator animator = samplingRoot.gameObject.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.enabled = false;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Update(0f);
            return animator;
        }

        private static void OverwriteClipCurves(AnimationClip dst, AnimationClip src)
        {
            dst.ClearCurves();

            EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(src);
            for (int i = 0; i < floatBindings.Length; i++)
            {
                EditorCurveBinding b = floatBindings[i];
                AnimationCurve c = AnimationUtility.GetEditorCurve(src, b);
                dst.SetCurve(b.path, b.type, b.propertyName, c);
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(src);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding b = objectBindings[i];
                ObjectReferenceKeyframe[] k = AnimationUtility.GetObjectReferenceCurve(src, b);
                AnimationUtility.SetObjectReferenceCurve(dst, b, k);
            }

            dst.frameRate = src.frameRate;
            dst.legacy = false;
            dst.EnsureQuaternionContinuity();
            EditorUtility.SetDirty(dst);
        }
    }
}
