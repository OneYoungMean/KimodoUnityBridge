using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetHumanoidPoseUtility
    {
        /// <summary>
        /// Captures the canonical 70D sample from one already evaluated
        /// retarget skeleton. HumanPose and the world bones are read from the
        /// same evaluation, so rootTQ and footTQ cannot come from different
        /// frames or from a later CharacterPose conversion.
        /// </summary>
        internal static bool TryCaptureEvaluatedMuscleSample(
            RetargetSkeleton cache,
            out MuscleSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(cache, out error))
            {
                return false;
            }

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref pose);
                sample = BuildMuscleSampleFromPose(cache, pose);
                if (sample == null || !sample.IsValid)
                {
                    sample = null;
                    error = "Evaluated retarget skeleton produced an invalid 70D muscle sample.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static MuscleSample BuildMuscleSampleFromPose(RetargetSkeleton cache, HumanPose pose)
        {
            var sample = new MuscleSample();
            float[] muscles = pose.muscles ?? Array.Empty<float>();
            for (int i = 0; i < KimodoMuscleSampleHumanPoseAdapter.UnityBodyMuscleIndices.Length; i++)
            {
                int unityIndex = KimodoMuscleSampleHumanPoseAdapter.UnityBodyMuscleIndices[i];
                sample.data[i] = unityIndex < muscles.Length ? muscles[unityIndex] : 0f;
            }
            sample.SetRoot(pose.bodyPosition, pose.bodyRotation);
            if (cache != null)
            {
                if (TryBuildFootTq(
                    cache,
                    pose,
                    HumanBodyBones.LeftFoot,
                    out Vector3 leftPosition,
                    out Quaternion leftRotation))
                {
                    sample.SetLeftFoot(leftPosition, leftRotation);
                }

                if (TryBuildFootTq(
                    cache,
                    pose,
                    HumanBodyBones.RightFoot,
                    out Vector3 rightPosition,
                    out Quaternion rightRotation))
                {
                    sample.SetRightFoot(rightPosition, rightRotation);
                }
            }
            return sample;
        }

        /// <summary>
        /// Encodes the legacy Humanoid footTQ transport. FootTQ is not a
        /// world-space bone transform and is not an IK-effector transport:
        /// first convert the foot bone to the avatar IK goal (post rotation
        /// plus the avatar axis endpoint), then express that goal relative to
        /// the HumanPose body using Unity's human-scale convention.
        /// </summary>
        private static bool TryBuildFootTq(
            RetargetSkeleton cache,
            HumanPose pose,
            HumanBodyBones bone,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (cache == null || cache.avatar == null ||
                !cache.GetBonePose(bone, out Vector3 footBoneWorldPosition,
                    out Quaternion footBoneWorldRotation))
            {
                return false;
            }

            Quaternion worldFootRotation = footBoneWorldRotation *
                AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(cache.avatar, (int)bone);
            float axisLength = AvatarRuntimeAccess.GetAvatarAxisLengthOrZero(
                cache.avatar,
                (int)bone);
            Vector3 worldFootPosition = footBoneWorldPosition +
                worldFootRotation * new Vector3(axisLength, 0f, 0f);

            float humanScale = Mathf.Max(1e-6f, cache.animator != null ? cache.animator.humanScale : 1f);
            Quaternion bodyRotation = pose.bodyRotation.normalized;
            Quaternion inverseBodyRotation = Quaternion.Inverse(bodyRotation);
            position = inverseBodyRotation *
                (worldFootPosition - pose.bodyPosition * humanScale) /
                humanScale;
            rotation = (inverseBodyRotation * worldFootRotation).normalized;
            return true;
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
