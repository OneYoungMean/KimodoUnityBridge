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
                    out KimodoSampleChannelMask enableMask,
                    out error))
            {
                result = null;
                return false;
            }

            result.sampleData = sampleData;
            result.enableMask = enableMask;
            result.validMask = new KimodoConstraintMask
            {
                muscle = enableMask.muscle49,
                rootTQ = enableMask.rootTQ,
                leftFootTQ = enableMask.leftFootTQ,
                rightFootTQ = enableMask.rightFootTQ
            };
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
        /// deliberately the only production path that creates the serialized
        /// root override (legacy field name: root2DOverride) and effector
        /// positions for an AutoSample result: all values are Transform world
        /// values, never HumanPose body-space values.
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
            result.enableMask ??= new KimodoSampleChannelMask();
            result.validMask ??= new KimodoConstraintMask();

            Vector3 position;
            Quaternion rotation;
            if (!cache.GetBonePose(HumanBodyBones.Hips, out position, out rotation))
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
            }
            result.rootOverride.t = position;
            result.rootOverride.q = rotation;
            result.enableMask.root2DPosition = true;
            result.enableMask.root2DHeading = true;
            result.validMask.rootPosition = true;
            result.validMask.rootHeading = true;

            CaptureEffector(cache, HumanBodyBones.LeftHand, result.effectors.leftHand,
                result.enableMask, result.validMask, 0, rotationMode: 0);
            CaptureEffector(cache, HumanBodyBones.RightHand, result.effectors.rightHand,
                result.enableMask, result.validMask, 1, rotationMode: 0);
            CaptureEffector(cache, HumanBodyBones.LeftFoot, result.effectors.leftFoot,
                result.enableMask, result.validMask, 2, rotationMode: 1);
            CaptureEffector(cache, HumanBodyBones.RightFoot, result.effectors.rightFoot,
                result.enableMask, result.validMask, 3, rotationMode: 1);
            result.enableMask.NormalizeDependencies();
        }

        private static void CaptureEffector(
            RetargetSkeleton cache,
            HumanBodyBones bone,
            KimodoRigidTransform target,
            KimodoSampleChannelMask enableMask,
            KimodoConstraintMask validMask,
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
                case 0: enableMask.leftHandEffector = true; validMask.leftHand = true; break;
                case 1: enableMask.rightHandEffector = true; validMask.rightHand = true; break;
                case 2: enableMask.leftFootEffector = true; validMask.leftFoot = true; break;
                case 3: enableMask.rightFootEffector = true; validMask.rightFoot = true; break;
            }
        }

        internal static Quaternion ResolveEffectorTransportRotation(
            RetargetSkeleton cache,
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
                // Foot q is the final rotation sent directly to IKGoal.
                return currentWorld * Quaternion.Inverse(initialWorld);
            }

            if (cache.skeletonRoot == null)
            {
                return currentWorld * Quaternion.Inverse(initialWorld);
            }

            Quaternion currentInRoot = Quaternion.Inverse(cache.skeletonRoot.rotation) * currentWorld;
            // Hand q is the final rotation sent directly to IKGoal.
            return currentInRoot * Quaternion.Inverse(initialWorld);
        }

        private static KimodoMarkerSampleResult CreateSampleShell(
            string markerType,
            double sampleTime)
        {
            return new KimodoMarkerSampleResult
            {
                sampleData = new MuscleSample(),
                constraintMode = "constraint",
                sampleTime = sampleTime,
                enableMask = new KimodoSampleChannelMask(),
                effectors = new KimodoConstraintEffectors(),
                rootOverride = KimodoRigidTransform.Identity
            };
        }
    }
}
