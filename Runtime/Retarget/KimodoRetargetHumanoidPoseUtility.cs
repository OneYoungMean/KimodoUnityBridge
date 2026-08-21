using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetHumanoidPoseUtility
    {
        internal static MuscleSample BuildMuscleSampleFromPose(SkeletonCache cache, HumanPose pose)
        {
            var sample = new MuscleSample
            {
                pose = pose,
                leftFootPosition = Vector3.zero,
                leftFootRotation = Quaternion.identity,
                rightFootPosition = Vector3.zero,
                rightFootRotation = Quaternion.identity
            };
            return sample;
        }

        [Obsolete("IK solving was removed; retained only for serialized/test compatibility.")]
        internal static Vector3 BonePositionToEffectorWorldPosition(
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

        [Obsolete("IK solving was removed; retained only for serialized/test compatibility.")]
        internal static Vector3 EffectorPositionToBoneWorldPosition(
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

        // Compatibility math retained for serialized/test readers only.
        [Obsolete("IK solving was removed; retained only for serialized/test compatibility.")]
        internal static void WorldToBodyRelativeEffector(
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

        [Obsolete("IK solving was removed; retained only for serialized/test compatibility.")]
        internal static void BodyRelativeEffectorToWorld(
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

        private static bool UsesAxisEndpoint(HumanBodyBones bone)
        {
            return bone == HumanBodyBones.LeftFoot || bone == HumanBodyBones.RightFoot;
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

    }
}
