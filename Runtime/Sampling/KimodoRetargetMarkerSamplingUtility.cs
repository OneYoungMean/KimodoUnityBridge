using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
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

            result = CreateSampleShell(markerType, sampleTime);

            if (!KimodoRetargetSamplingUtility.TryCaptureSampleData(
                    targetCache,
                    out float[] sampleData,
                    out KimodoSampleChannelMask enableMask,
                    out error))
            {
                result = null;
                return false;
            }

            result.sampleData = sampleData;
            result.enableMask = enableMask;
            result.enabled = true;
            if (!string.Equals(markerType, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return true;
        }

        private static KimodoMarkerSampleResult CreateSampleShell(
            string markerType,
            double sampleTime)
        {
            return new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                sampleTime = sampleTime,
                mask = KimodoConstraintMask.ForType(markerType),
                enableMask = new KimodoSampleChannelMask()
            };
        }
    }
}
