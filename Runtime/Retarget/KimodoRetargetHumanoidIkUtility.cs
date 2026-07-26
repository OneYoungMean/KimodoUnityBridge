using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetHumanoidIkUtility
    {
        internal static MuscleSample BuildMuscleSampleFromPose(SkeletonCache cache, HumanPose pose)
        {
            return BuildMuscleSampleFromPose(
                cache != null ? cache.avatar : null,
                cache != null ? cache.humanScale : 1f,
                pose,
                cache != null ? cache.skeletonRoot : null,
                bone => ResolveHumanBoneTransform(cache, bone));
        }

        internal static MuscleSample BuildMuscleSampleFromPose(
            Avatar avatar,
            float humanScale,
            HumanPose pose,
            Transform humanPoseRoot,
            Func<HumanBodyBones, Transform> resolveHumanBone)
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

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar) || resolveHumanBone == null)
            {
                return sample;
            }

            float scale = Mathf.Max(1e-6f, humanScale);
            TryGetHumanoidIkGoalPose(avatar, humanPoseRoot, resolveHumanBone, AvatarIKGoal.LeftFoot, pose.bodyPosition, pose.bodyRotation, scale, out sample.leftFootPosition, out sample.leftFootRotation);
            TryGetHumanoidIkGoalPose(avatar, humanPoseRoot, resolveHumanBone, AvatarIKGoal.RightFoot, pose.bodyPosition, pose.bodyRotation, scale, out sample.rightFootPosition, out sample.rightFootRotation);
            TryGetHumanoidIkGoalPose(avatar, humanPoseRoot, resolveHumanBone, AvatarIKGoal.LeftHand, pose.bodyPosition, pose.bodyRotation, scale, out sample.leftHandPosition, out sample.leftHandRotation);
            TryGetHumanoidIkGoalPose(avatar, humanPoseRoot, resolveHumanBone, AvatarIKGoal.RightHand, pose.bodyPosition, pose.bodyRotation, scale, out sample.rightHandPosition, out sample.rightHandRotation);
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
            return TryGetHumanoidIkGoalPose(
                cache != null ? cache.avatar : null,
                cache != null ? cache.skeletonRoot : null,
                bone => ResolveHumanBoneTransform(cache, bone),
                avatarIKGoal,
                bodyPosition,
                bodyRotation,
                humanScale,
                out goalPosition,
                out goalRotation);
        }

        private static bool TryGetHumanoidIkGoalPose(
            Avatar avatar,
            Transform humanPoseRoot,
            Func<HumanBodyBones, Transform> resolveHumanBone,
            AvatarIKGoal avatarIKGoal,
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            float humanScale,
            out Vector3 goalPosition,
            out Quaternion goalRotation)
        {
            goalPosition = Vector3.zero;
            goalRotation = Quaternion.identity;

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar) || resolveHumanBone == null)
            {
                return false;
            }

            HumanBodyBones bone = HumanBodyBoneFromAvatarIKGoal(avatarIKGoal);
            if (bone == HumanBodyBones.LastBone)
            {
                return false;
            }

            Transform transform = resolveHumanBone(bone);
            if (transform == null)
            {
                return false;
            }

            int humanId = (int)bone;
            Quaternion postRotation = AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(avatar, humanId);
            Quaternion inverseRootRotation = Quaternion.Inverse(
                humanPoseRoot != null ? humanPoseRoot.rotation : Quaternion.identity);
            Vector3 rootGoalPosition = humanPoseRoot != null
                ? humanPoseRoot.InverseTransformPoint(transform.position)
                : transform.position;
            Quaternion rootGoalRotation = inverseRootRotation * transform.rotation * postRotation;

            if (avatarIKGoal == AvatarIKGoal.LeftFoot || avatarIKGoal == AvatarIKGoal.RightFoot)
            {
                float axisLength = AvatarRuntimeAccess.GetAvatarAxisLengthOrZero(avatar, humanId);
                rootGoalPosition += rootGoalRotation * new Vector3(axisLength, 0f, 0f);
            }

            Quaternion inverseBodyRotation = Quaternion.Inverse(bodyRotation);
            goalPosition = inverseBodyRotation * (rootGoalPosition - bodyPosition * humanScale);
            goalRotation = inverseBodyRotation * rootGoalRotation;
            goalPosition /= humanScale;
            return true;
        }

        internal static Transform ResolveHumanBoneTransform(SkeletonCache cache, HumanBodyBones bone)
        {
            if (cache == null)
            {
                return null;
            }

            if (cache.humanBoneTransforms != null &&
                cache.humanBoneTransforms.TryGetValue(bone, out Transform cached) &&
                cached != null)
            {
                return cached;
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

                if (KimodoRetargetAvatarUtility.TryGetUniqueCachedTransformByName(cache, humanBone.boneName, out Transform resolved, out _))
                {
                    return resolved;
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
