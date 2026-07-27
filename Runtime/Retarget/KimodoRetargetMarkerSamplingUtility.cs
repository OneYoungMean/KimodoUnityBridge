using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetMarkerSamplingUtility
    {
        private const string DefaultModelName = "Kimodo-SOMA-RP-v1";

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

            if (!TryResolveTargetAvatar(explicitTargetAvatar, fallbackAnimator, modelName, out Avatar targetAvatar, out error))
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

        internal static bool TryResolveTargetAvatar(
            Avatar explicitTargetAvatar,
            Animator fallbackAnimator,
            string modelName,
            out Avatar targetAvatar,
            out string error)
        {
            targetAvatar = null;
            error = string.Empty;
            _ = fallbackAnimator;

            if (KimodoRetargetCoreUtility.IsValidHumanoid(explicitTargetAvatar))
            {
                targetAvatar = explicitTargetAvatar;
                return true;
            }

            string resolvedModelName = string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName.Trim();
            if (KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(resolvedModelName, out Avatar resolvedAvatar, out string targetError) &&
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

        internal static bool TryBuildMarkerSampleResultFromBoneSample(
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
            string resolvedModelName = string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName.Trim();

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
                    resolvedModelName,
                    targetCache,
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
                resolvedModelName,
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
