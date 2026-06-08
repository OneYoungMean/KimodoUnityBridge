using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetHumanoidIkUtility
    {
        internal static MuscleSample BuildMuscleSampleFromPose(SkeletonCache cache, HumanPose pose)
        {
            var sample = new MuscleSample
            {
                pose = pose,
                leftFootPosition = Vector3.zero,
                leftFootRotation = Quaternion.identity,
                rightFootPosition = Vector3.zero,
                rightFootRotation = Quaternion.identity,
                leftHandPosition = Vector3.zero,
                leftHandRotation = Quaternion.identity,
                rightHandPosition = Vector3.zero,
                rightHandRotation = Quaternion.identity
            };

            if (cache == null || cache.animator == null || cache.avatar == null)
            {
                return sample;
            }

            float humanScale = Mathf.Max(1e-6f, cache.humanScale);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.LeftFoot, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.leftFootPosition, out sample.leftFootRotation);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.RightFoot, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.rightFootPosition, out sample.rightFootRotation);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.LeftHand, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.leftHandPosition, out sample.leftHandRotation);
            TryGetHumanoidIkGoalPose(cache, AvatarIKGoal.RightHand, pose.bodyPosition, pose.bodyRotation, humanScale, out sample.rightHandPosition, out sample.rightHandRotation);
            return sample;
        }

        internal static bool TryGetHumanoidIkGoalPose(
            SkeletonCache cache,
            AvatarIKGoal avatarIKGoal,
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            float humanScale,
            out Vector3 goalPosition,
            out Quaternion goalRotation)
        {
            goalPosition = Vector3.zero;
            goalRotation = Quaternion.identity;

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out _))
            {
                return false;
            }

            HumanBodyBones bone = HumanBodyBoneFromAvatarIKGoal(avatarIKGoal);
            if (bone == HumanBodyBones.LastBone)
            {
                return false;
            }

            Transform transform = ResolveHumanBoneTransform(cache, bone);
            if (transform == null)
            {
                return false;
            }

            int humanId = (int)bone;
            float humanScaleLimit = Mathf.Max(1e-6f, humanScale);
            Quaternion postRotation = AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(cache.avatar, humanId);
            Quaternion worldGoalRotation = transform.rotation * postRotation;
            Vector3 worldGoalPosition = transform.position;

            if (avatarIKGoal == AvatarIKGoal.LeftFoot || avatarIKGoal == AvatarIKGoal.RightFoot)
            {
                float axisLength = AvatarRuntimeAccess.GetAvatarAxisLengthOrZero(cache.avatar, humanId);
                worldGoalPosition += worldGoalRotation * new Vector3(axisLength, 0f, 0f);
            }

            Quaternion inverseBodyRotation = Quaternion.Inverse(bodyRotation);
            goalPosition = inverseBodyRotation * (worldGoalPosition - bodyPosition);
            goalRotation = inverseBodyRotation * worldGoalRotation;
            goalPosition /= humanScaleLimit;
            return true;
        }

        internal static Vector3 ComputeRootTFromHumanoidIkGoalPose(
            Vector3 worldGoalPosition,
            Quaternion rootQ,
            Vector3 goalPosition,
            float humanScale)
        {
            float scale = Mathf.Max(1e-6f, humanScale);
            return (worldGoalPosition - rootQ * (goalPosition * scale)) / scale;
        }

        internal static Transform ResolveHumanBoneTransform(SkeletonCache cache, HumanBodyBones bone)
        {
            if (cache?.animator == null)
            {
                return null;
            }

            if (cache.animator.avatar != null)
            {
                Transform resolved = cache.animator.GetBoneTransform(bone);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(cache.avatar))
            {
                return null;
            }

            HumanBone[] humanBones = cache.avatar.humanDescription.human;
            string humanName = bone.ToString();
            for (int i = 0; i < humanBones.Length; i++)
            {
                HumanBone humanBone = humanBones[i];
                if (!string.Equals(humanBone.humanName, humanName, StringComparison.Ordinal))
                {
                    continue;
                }

                return KimodoRetargetAvatarUtility.FindTransformByName(cache.skeletonRoot, humanBone.boneName);
            }

            return null;
        }

        internal static HumanBodyBones HumanBodyBoneFromAvatarIKGoal(AvatarIKGoal avatarIKGoal)
        {
            switch (avatarIKGoal)
            {
                case AvatarIKGoal.LeftFoot:
                    return HumanBodyBones.LeftFoot;
                case AvatarIKGoal.RightFoot:
                    return HumanBodyBones.RightFoot;
                case AvatarIKGoal.LeftHand:
                    return HumanBodyBones.LeftHand;
                case AvatarIKGoal.RightHand:
                    return HumanBodyBones.RightHand;
                default:
                    return HumanBodyBones.LastBone;
            }
        }
    }
}
