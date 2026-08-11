using System;
using KimodoUnityBridge.Command;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineGenerationOutputPlanner
    {
        internal static AnimationClip CreateTargetClip(KimodoPlayableClip clip)
        {
            if (clip == null) throw new InvalidOperationException("Playable clip is null.");
            string outputFolder = string.IsNullOrWhiteSpace(clip.generatedOutputFolder)
                ? KimodoEditorClipWritebackService.GeneratedClipFolder
                : command_context.NormalizeOutputFolder(clip.generatedOutputFolder);
            string assetName = string.IsNullOrWhiteSpace(clip.generatedAssetName)
                ? BuildTargetClipName(clip.bridgeModelName, DateTime.Now)
                : clip.generatedAssetName.Trim();
            return KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(assetName, outputFolder);
        }

        internal static string BuildTargetClipName(string modelName, DateTime timestamp) =>
            $"{(KimodoMotionModelProfiles.TryGetArdy(modelName, out _) ? "ARDY" : "Kimodo")}_Playable_{timestamp:yyyyMMdd_HHmmss_fff}";

        internal static Avatar ResolveOriginRetargetAvatar(string modelName)
        {
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar avatar, out _))
            {
                return null;
            }
            return KimodoRetargetCoreUtility.IsValidHumanoid(avatar) ? avatar : null;
        }

        internal static KimodoEditorGenerateOutputPlan Capture(
            KimodoPlayableClip clip,
            Avatar explicitRetargetAvatar,
            string modelName,
            GameObject bindingObject)
        {
            if (clip == null) throw new InvalidOperationException("Playable clip is null.");
            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            Avatar originRetargetAvatar = ResolveOriginRetargetAvatar(resolvedModelName);
            Avatar targetRetargetAvatar = ResolveTargetRetargetAvatar(
                clip, explicitRetargetAvatar, bindingObject, out bool hasBindingAvatar);
            bool hasValidRetargetAvatar =
                KimodoRetargetCoreUtility.IsValidHumanoid(originRetargetAvatar) &&
                hasBindingAvatar &&
                KimodoRetargetCoreUtility.IsValidHumanoid(targetRetargetAvatar);
            var outputPlan = new KimodoEditorGenerateOutputPlan
            {
                OriginRetargetAvatar = originRetargetAvatar,
                TargetRetargetAvatar = targetRetargetAvatar,
                ExportMuscleClip = hasValidRetargetAvatar && TryResolveBindingAnimatorAvatar(bindingObject, out _),
                CurveFilterOptions = CloneCurveFilterOptions(clip.curveFilterOptions)
            };
            switch (clip.generationOutputMode)
            {
                case KimodoGenerationOutputMode.HumanoidMuscle:
                    outputPlan.ExportMuscleClip = true;
                    break;
                case KimodoGenerationOutputMode.CharacterBone:
                    outputPlan.ExportMuscleClip = false;
                    break;
                case KimodoGenerationOutputMode.ModelBone:
                    outputPlan.SkipRetarget = true;
                    outputPlan.ExportMuscleClip = false;
                    break;
            }
            return outputPlan;
        }

        internal static KimodoEditorGenerateOutputPlan Resolve(
            KimodoEditorGenerateOutputPlan snapshot,
            GameObject bindingObject,
            AnimationClip generatedClip,
            string modelName)
        {
            if (snapshot == null) throw new InvalidOperationException("Timeline output plan snapshot is null.");
            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            bool canSkipRetarget = bindingObject != null &&
                KimodoEditorClipUtility.CanApplyClipDirectlyToProfileSkeleton(
                    generatedClip, bindingObject, resolvedModelName, out _);
            return new KimodoEditorGenerateOutputPlan
            {
                OriginRetargetAvatar = snapshot.OriginRetargetAvatar,
                TargetRetargetAvatar = snapshot.TargetRetargetAvatar,
                ExportMuscleClip = snapshot.ExportMuscleClip,
                CurveFilterOptions = snapshot.CurveFilterOptions,
                SkipRetarget = snapshot.SkipRetarget || canSkipRetarget
            };
        }

        private static Avatar ResolveTargetRetargetAvatar(
            KimodoPlayableClip clip,
            Avatar explicitRetargetAvatar,
            GameObject bindingObject,
            out bool hasBindingAvatar)
        {
            hasBindingAvatar = false;
            if (explicitRetargetAvatar != null && explicitRetargetAvatar.isValid && explicitRetargetAvatar.isHuman)
            {
                hasBindingAvatar = true;
                return explicitRetargetAvatar;
            }
            if (bindingObject != null)
            {
                KimodoLocalAvatarUtility.AvatarResolveResult result =
                    KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(bindingObject);
                if (result.IsHumanoid && result.Avatar != null)
                {
                    Animator animator = bindingObject.GetComponent<Animator>();
                    hasBindingAvatar = animator != null && animator.avatar != null;
                    return result.Avatar;
                }
            }
            return clip.CustomRetargetAvatar != null && clip.CustomRetargetAvatar.isValid && clip.CustomRetargetAvatar.isHuman
                ? clip.CustomRetargetAvatar
                : null;
        }

        private static bool TryResolveBindingAnimatorAvatar(GameObject bindingObject, out Avatar avatar)
        {
            avatar = null;
            if (bindingObject == null) return false;
            KimodoLocalAvatarUtility.AvatarResolveResult result =
                KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(bindingObject);
            if (!result.IsHumanoid || result.Avatar == null ||
                !string.Equals(result.Source, "Animator", StringComparison.Ordinal))
            {
                return false;
            }
            avatar = result.Avatar;
            return true;
        }

        private static KimodoCurveFilterOptions CloneCurveFilterOptions(KimodoCurveFilterOptions source)
        {
            source ??= new KimodoCurveFilterOptions();
            return new KimodoCurveFilterOptions
            {
                enabled = source.enabled,
                positionError = source.positionError,
                rotationError = source.rotationError,
                floatError = source.floatError,
                ensureQuaternionContinuity = source.ensureQuaternionContinuity
            };
        }
    }
}
