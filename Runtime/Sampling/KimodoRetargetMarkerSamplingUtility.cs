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
                out MuscleSample sampleData,
                    out KimodoSampleChannelMask enableMask,
                    out error))
            {
                result = null;
                return false;
            }

            result.sampleData = sampleData;
            result.enableMask = enableMask;
            CaptureWorldTargets(targetCache, result);
            result.enabled = true;
            if (!string.Equals(markerType, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return true;
        }

        /// <summary>
        /// Captures the scene-facing targets from the rebuilt skeleton. This is
        /// deliberately the only production path that creates root2DOverride
        /// and effector positions for an AutoSample result: all values are
        /// Transform world values, never HumanPose body-space values.
        /// </summary>
        internal static void CaptureWorldTargets(
            SkeletonCache cache,
            KimodoMarkerSampleResult result)
        {
            if (result == null) return;
            result.effectors ??= new KimodoConstraintEffectors();
            result.effectors.hands ??= new CharacterPoseSides();
            result.effectors.feet ??= new CharacterPoseSides();
            result.enableMask ??= new KimodoSampleChannelMask();

            Vector3 position;
            Quaternion rotation;
            if (!cache.GetBonePose(HumanBodyBones.Hips, out position, out rotation))
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
            }
            result.root2DOverride.t = position;
            result.root2DOverride.q = rotation;
            result.enableMask.root2DPosition = true;
            result.enableMask.root2DHeading = true;

            CaptureEffector(cache, HumanBodyBones.LeftHand, result.effectors.hands.left,
                result.enableMask, 0, rotationMode: 0);
            CaptureEffector(cache, HumanBodyBones.RightHand, result.effectors.hands.right,
                result.enableMask, 1, rotationMode: 0);
            CaptureEffector(cache, HumanBodyBones.LeftFoot, result.effectors.feet.left,
                result.enableMask, 2, rotationMode: 1);
            CaptureEffector(cache, HumanBodyBones.RightFoot, result.effectors.feet.right,
                result.enableMask, 3, rotationMode: 1);
            result.enableMask.NormalizeDependencies();
        }

        private static void CaptureEffector(
            SkeletonCache cache,
            HumanBodyBones bone,
            KimodoRigidTransform target,
            KimodoSampleChannelMask enableMask,
            int index,
            int rotationMode)
        {
            if (cache != null && cache.GetBonePose(bone, out Vector3 position, out Quaternion rotation))
            {
                target.t = position;
                target.q = ResolveEffectorTransportRotation(cache, bone, rotation, rotationMode);
            }
            else
            {
                target.t = Vector3.zero;
                target.q = Quaternion.identity;
            }
            switch (index)
            {
                case 0: enableMask.leftHandEffector = true; break;
                case 1: enableMask.rightHandEffector = true; break;
                case 2: enableMask.leftFootEffector = true; break;
                case 3: enableMask.rightFootEffector = true; break;
            }
        }

        internal static Quaternion ResolveEffectorTransportRotation(
            SkeletonCache cache,
            HumanBodyBones bone,
            Quaternion currentWorld,
            int rotationMode)
        {
            if (cache == null || !cache.GetBoneBindWorldRotation(bone, out Quaternion initialWorld))
            {
                return currentWorld;
            }

            if (rotationMode == 1)
            {
                // Foot cube rotation is independent of pelvis space. The
                // consumer reconstructs q-current = q-cube * q-initialFoot.
                return currentWorld * Quaternion.Inverse(initialWorld);
            }

            if (cache.skeletonRoot == null)
            {
                return currentWorld * Quaternion.Inverse(initialWorld);
            }

            Quaternion currentInRoot = Quaternion.Inverse(cache.skeletonRoot.rotation) * currentWorld;
            Quaternion initialInRoot = Quaternion.Inverse(cache.bindSkeletonRootWorldRotation) * initialWorld;
            // Hand transport rotation is q-current-in-root * inverse(q-initial-in-root).
            return currentInRoot * Quaternion.Inverse(initialInRoot);
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
