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
                bone => ResolveHumanBoneTransform(cache, bone));
        }

        internal static MuscleSample BuildMuscleSampleFromPose(
            Avatar avatar,
            float humanScale,
            HumanPose pose,
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
            TryGetHumanoidIkGoalPose(avatar, resolveHumanBone, AvatarIKGoal.LeftFoot, pose.bodyPosition, pose.bodyRotation, scale, out sample.leftFootPosition, out sample.leftFootRotation);
            TryGetHumanoidIkGoalPose(avatar, resolveHumanBone, AvatarIKGoal.RightFoot, pose.bodyPosition, pose.bodyRotation, scale, out sample.rightFootPosition, out sample.rightFootRotation);
            TryGetHumanoidIkGoalPose(avatar, resolveHumanBone, AvatarIKGoal.LeftHand, pose.bodyPosition, pose.bodyRotation, scale, out sample.leftHandPosition, out sample.leftHandRotation);
            TryGetHumanoidIkGoalPose(avatar, resolveHumanBone, AvatarIKGoal.RightHand, pose.bodyPosition, pose.bodyRotation, scale, out sample.rightHandPosition, out sample.rightHandRotation);
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
            Quaternion worldGoalRotation = transform.rotation * postRotation;
            Vector3 worldGoalPosition = BonePositionToIkGoalWorldPosition(
                avatar,
                bone,
                transform.position,
                worldGoalRotation);

            WorldToBodyRelativeIkGoal(
                bodyPosition,
                bodyRotation,
                humanScale,
                worldGoalPosition,
                worldGoalRotation,
                out goalPosition,
                out goalRotation);
            return true;
        }

        internal static Vector3 BonePositionToIkGoalWorldPosition(
            Avatar avatar,
            HumanBodyBones bone,
            Vector3 boneWorldPosition,
            Quaternion goalWorldRotation)
        {
            float axisLength = avatar != null && UsesAxisEndpoint(bone)
                ? AvatarRuntimeAccess.GetAvatarAxisLengthOrZero(avatar, (int)bone)
                : 0f;
            return boneWorldPosition + goalWorldRotation * new Vector3(axisLength, 0f, 0f);
        }

        internal static Vector3 IkGoalPositionToBoneWorldPosition(
            Avatar avatar,
            HumanBodyBones bone,
            Vector3 goalWorldPosition,
            Quaternion goalWorldRotation)
        {
            float axisLength = avatar != null && UsesAxisEndpoint(bone)
                ? AvatarRuntimeAccess.GetAvatarAxisLengthOrZero(avatar, (int)bone)
                : 0f;
            return goalWorldPosition - goalWorldRotation * new Vector3(axisLength, 0f, 0f);
        }

        private static bool UsesAxisEndpoint(HumanBodyBones bone)
        {
            return bone == HumanBodyBones.LeftFoot || bone == HumanBodyBones.RightFoot;
        }

        internal static void WorldToBodyRelativeIkGoal(
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            float humanScale,
            Vector3 worldGoalPosition,
            Quaternion worldGoalRotation,
            out Vector3 goalPosition,
            out Quaternion goalRotation)
        {
            float scale = Mathf.Max(1e-6f, humanScale);
            Quaternion inverseBodyRotation = Quaternion.Inverse(bodyRotation);
            goalPosition = inverseBodyRotation * (worldGoalPosition - bodyPosition * scale) / scale;
            goalRotation = inverseBodyRotation * worldGoalRotation;
        }

        internal static void BodyRelativeIkGoalToWorld(
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            float humanScale,
            Vector3 goalPosition,
            Quaternion goalRotation,
            out Vector3 worldGoalPosition,
            out Quaternion worldGoalRotation)
        {
            float scale = Mathf.Max(1e-6f, humanScale);
            worldGoalPosition = bodyPosition * scale + bodyRotation * (goalPosition * scale);
            worldGoalRotation = bodyRotation * goalRotation;
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
