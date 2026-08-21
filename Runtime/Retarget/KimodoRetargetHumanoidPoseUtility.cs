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
