using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetMarkerSamplingUtility
    {
        internal static bool TryResolveTargetAvatar(
            Avatar explicitTargetAvatar,
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

            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
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
            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);

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

            Transform endEffector = null;
            if (KimodoMarkerSamplingUtility.TryResolveEndEffectorBone(markerType, out HumanBodyBones endEffectorBone))
            {
                endEffector = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(targetCache, endEffectorBone);
            }

            if (!KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                targetCache.animator,
                targetCache.skeletonRoot,
                resolvedModelName,
                sampleTime,
                markerType,
                jointNames,
                parentIndices,
                jointTransforms,
                out result,
                out error,
                endEffector))
            {
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(targetCache, out MuscleSample muscleSample, out error))
            {
                result = null;
                return false;
            }

            result.characterPose = CharacterPoseMuscleAdapter.FromMuscleSample(muscleSample);
            result.humanScale = Mathf.Max(1e-6f, targetCache.humanScale);
            if (!string.Equals(markerType, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            result.muscles = new List<float>(muscleSample.pose.muscles);
            result.leftFootPosition = muscleSample.leftFootPosition;
            result.leftFootRotation = muscleSample.leftFootRotation;
            result.rightFootPosition = muscleSample.rightFootPosition;
            result.rightFootRotation = muscleSample.rightFootRotation;
            return true;
        }
    }
}
