using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetMarkerSamplingUtility
    {
        public static bool TrySampleMarkerFromClip(
            AnimationClip sourceClip,
            string markerType,
            double sampleTime,
            Avatar sourceAvatar,
            Avatar explicitTargetAvatar,
            Animator fallbackAnimator,
            string modelName,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
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

            if (!TryResolveMarkerTargetAvatar(explicitTargetAvatar, fallbackAnimator, modelName, out Avatar targetAvatar, out error))
            {
                return false;
            }

            SkeletonCache sourceCache = null;
            SkeletonCache targetCache = null;
            try
            {
                if (!KimodoRetargetSamplingUtility.TryResolveSourceHumanoidClip(
                        sourceClip,
                        sourceAvatar,
                        "KimodoMarkerRetarget_SourceHumanoid",
                        null,
                        ref sourceCache,
                        out AnimationClip sourceHumanoidClip,
                        out error))
                {
                    return false;
                }

                try
                {
                    if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(targetAvatar, "KimodoMarkerRetarget_Target", out targetCache, out error))
                    {
                        return false;
                    }

                    if (!KimodoRetargetSamplingUtility.TrySampleTargetFromHumanoidClip(
                            sourceHumanoidClip,
                            targetCache,
                            (float)sampleTime,
                            out BoneSample targetSample,
                            out _,
                            out error))
                    {
                        return false;
                    }

                    return TryBuildMarkerSampleResultFromBoneSample(
                        targetSample,
                        targetCache,
                        modelName,
                        markerType,
                        sampleTime,
                        out result,
                        out error);
                }
                finally
                {
                    if (!ReferenceEquals(sourceHumanoidClip, sourceClip))
                    {
                        UnityEngine.Object.DestroyImmediate(sourceHumanoidClip);
                    }
                }
            }
            finally
            {
                targetCache?.Dispose();
                sourceCache?.Dispose();
            }
        }

        private static bool TryResolveMarkerTargetAvatar(
            Avatar explicitTargetAvatar,
            Animator fallbackAnimator,
            string modelName,
            out Avatar targetAvatar,
            out string error)
        {
            targetAvatar = null;
            error = string.Empty;

            if (KimodoRetargetCoreUtility.IsValidHumanoid(explicitTargetAvatar))
            {
                targetAvatar = explicitTargetAvatar;
                return true;
            }

            if (KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar resolvedAvatar, out string targetError) &&
                KimodoRetargetCoreUtility.IsValidHumanoid(resolvedAvatar))
            {
                targetAvatar = resolvedAvatar;
                return true;
            }

            error = string.IsNullOrWhiteSpace(targetError)
                ? "Failed to resolve target avatar."
                : $"Resolve target avatar failed: {targetError}";
            return false;
        }

        private static bool TryBuildMarkerSampleResultFromBoneSample(
            BoneSample sample,
            SkeletonCache targetCache,
            string modelName,
            string markerType,
            double sampleTime,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;

            if (sample == null || !sample.IsValid)
            {
                error = "Bone sample is invalid.";
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(targetCache, out error))
            {
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(sample, targetCache, out error))
            {
                return false;
            }

            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    modelName,
                    targetCache.skeletonRoot,
                    out string[] jointNames,
                    out int[] parentIndices,
                    out Transform[] jointTransforms,
                    out error))
            {
                return false;
            }

            return KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                targetCache.animator,
                targetCache.skeletonRoot,
                modelName,
                sampleTime,
                markerType,
                jointNames,
                parentIndices,
                jointTransforms,
                out result,
                out error);
        }
    }
}
