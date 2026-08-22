using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetHumanoidPoseUtility
    {
        internal static MuscleSample BuildMuscleSampleFromPose(RetargetSkeleton cache, HumanPose pose)
        {
            var sample = new MuscleSample();
            float[] muscles = pose.muscles ?? Array.Empty<float>();
            for (int i = 0; i < CharacterPoseMuscleAdapter.UnityBodyMuscleIndices.Length; i++)
            {
                int unityIndex = CharacterPoseMuscleAdapter.UnityBodyMuscleIndices[i];
                sample.data[i] = unityIndex < muscles.Length ? muscles[unityIndex] : 0f;
            }
            sample.SetRoot(pose.bodyPosition, pose.bodyRotation);
            if (cache != null)
            {
                cache.GetBonePose(HumanBodyBones.LeftFoot, out Vector3 leftPosition, out Quaternion leftRotation);
                cache.GetBonePose(HumanBodyBones.RightFoot, out Vector3 rightPosition, out Quaternion rightRotation);
                sample.SetLeftFoot(leftPosition, leftRotation);
                sample.SetRightFoot(rightPosition, rightRotation);
            }
            return sample;
        }

        internal static Transform ResolveHumanBoneTransform(RetargetSkeleton cache, HumanBodyBones bone)
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
