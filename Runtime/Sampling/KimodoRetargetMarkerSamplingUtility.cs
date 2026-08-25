using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetMarkerSamplingUtility
    {
        internal static bool TryResolveTargetAvatar(
            Avatar explicitTargetAvatar,
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

            error = "An explicit target avatar is required; profile avatar fallback is disabled.";
            return false;
        }

        internal static bool TryBuildMarkerSampleResultFromBoneSample(
            BoneSample sample,
            RetargetSkeleton targetCache,
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

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(sample, targetCache, out error))
            {
                return false;
            }

            result = CreateSampleShell(markerType, sampleTime);

            if (!KimodoRetargetSamplingUtility.TryCaptureSampleData(
                    targetCache,
                    out MuscleSample sampleData,
                    out KimodoConstraintMask validMask,
                    out error))
            {
                result = null;
                return false;
            }

            result.sampleData = sampleData;
            result.validMask = validMask;
            CaptureWorldTargets(targetCache, result);
            result.enabled = true;
            return true;
        }

        /// <summary>
        /// Captures the scene-facing targets from the rebuilt skeleton. The
        /// caller establishes whether that Transform space is world or track;
        /// values are never HumanPose body-space values.
        /// </summary>
        internal static void CaptureWorldTargets(
            RetargetSkeleton cache,
            KimodoMarkerSampleResult result)
        {
            if (result == null) return;
            result.effectors ??= new KimodoConstraintEffectors();
            result.effectors.leftHand ??= KimodoRigidTransform.Identity;
            result.effectors.rightHand ??= KimodoRigidTransform.Identity;
            result.effectors.leftFoot ??= KimodoRigidTransform.Identity;
            result.effectors.rightFoot ??= KimodoRigidTransform.Identity;
            result.rootOverride ??= KimodoRigidTransform.Identity;
            result.enableMask ??= new KimodoConstraintMask();
            result.validMask ??= new KimodoConstraintMask();

            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            bool rootValid = cache != null &&
                cache.GetBonePose(HumanBodyBones.Hips, out position, out rotation);
            if (!rootValid)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
            }
            result.rootOverride.t = position;
            result.rootOverride.q = rotation;
            result.validMask.rootPosition = rootValid;
            result.validMask.rootHeading = rootValid;

            CaptureEffector(cache, HumanBodyBones.LeftHand, result.effectors.leftHand,
                result.validMask, 0);
            CaptureEffector(cache, HumanBodyBones.RightHand, result.effectors.rightHand,
                result.validMask, 1);
            CaptureEffector(cache, HumanBodyBones.LeftFoot, result.effectors.leftFoot,
                result.validMask, 2);
            CaptureEffector(cache, HumanBodyBones.RightFoot, result.effectors.rightFoot,
                result.validMask, 3);
        }

        private static void CaptureEffector(
            RetargetSkeleton cache,
            HumanBodyBones bone,
            KimodoRigidTransform target,
            KimodoConstraintMask validMask,
            int index)
        {
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            bool valid = cache != null &&
                cache.GetBonePose(bone, out position, out rotation);
            if (valid)
            {
                target.t = position;
                target.q = ResolveEffectorTransportRotation(cache, bone, rotation);
            }
            else
            {
                target.t = Vector3.zero;
                target.q = Quaternion.identity;
            }
            switch (index)
            {
                case 0: validMask.leftHand = valid; break;
                case 1: validMask.rightHand = valid; break;
                case 2: validMask.leftFoot = valid; break;
                case 3: validMask.rightFoot = valid; break;
            }
        }

        internal static Quaternion ResolveEffectorTransportRotation(
            RetargetSkeleton cache,
            HumanBodyBones bone,
            Quaternion currentWorld)
        {
            if (cache == null || !cache.GetBoneBindWorldRotation(bone, out Quaternion initialWorld))
            {
                return currentWorld;
            }

            // This world-space delta is the final rotation sent directly to
            // AnimationHumanStream.SetGoalRotation for every effector.
            return currentWorld * Quaternion.Inverse(initialWorld);
        }

        private static KimodoMarkerSampleResult CreateSampleShell(
            string markerType,
            double sampleTime)
        {
            return new KimodoMarkerSampleResult
            {
                sampleData = new MuscleSample(),
                constraintMode = string.Equals(markerType, "fullbody", StringComparison.OrdinalIgnoreCase)
                    ? "fullbody"
                    : string.Equals(markerType, "root2d", StringComparison.OrdinalIgnoreCase)
                        ? "root2d"
                        : "effector",
                sampleTime = sampleTime,
                enableMask = new KimodoConstraintMask(),
                effectors = new KimodoConstraintEffectors(),
                rootOverride = KimodoRigidTransform.Identity
            };
        }
    }
}
