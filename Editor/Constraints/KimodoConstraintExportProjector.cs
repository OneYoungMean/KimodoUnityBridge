using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintExportProjector
    {
        internal static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            KimodoTimelineInOutConstraintContext context)
        {
            return sample => ProjectTimelineSample(sample, context);
        }

        internal static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> CreateProfileNative(
            string modelName)
        {
            return KimodoRuntimeConstraintExportProjector.Create(modelName);
        }

        private static KimodoConstraintProjectedPose ProjectTimelineSample(
            KimodoMarkerSampleResult sample,
            KimodoTimelineInOutConstraintContext context)
        {
            if (context?.Animator == null)
            {
                throw new InvalidOperationException(
                    "Timeline constraint projection requires the bound Character Animator.");
            }

            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(
                    context.Track,
                    context.Animator);
            if (!avatarResult.IsHumanoid ||
                !KimodoRetargetCoreUtility.IsValidHumanoid(avatarResult.Avatar))
            {
                throw new InvalidOperationException(
                    $"Timeline constraint Character Avatar is invalid: {avatarResult.Error}");
            }

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation);
            return ProjectTimelineSample(
                sample,
                context.ModelName,
                avatarResult.Avatar,
                trackOffsetPosition,
                trackOffsetRotation);
        }

        internal static KimodoConstraintProjectedPose ProjectTimelineSample(
            KimodoMarkerSampleResult sample,
            string modelName,
            Avatar sourceAvatar,
            Vector3 trackOffsetPosition,
            Quaternion trackOffsetRotation)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                throw new InvalidOperationException(
                    "Timeline constraint projection requires a valid Character Avatar.");
            }

            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    sourceAvatar,
                    "KimodoConstraintExportCharacter",
                    out RetargetSkeleton characterCache,
                    out string error))
            {
                throw new InvalidOperationException(
                    $"Constraint Character skeleton failed: {error}");
            }

            try
            {
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
                if (!KimodoConstraintPosePipeline.TryApply(
                        sample,
                        frameRate,
                        characterCache,
                        trackOffsetPosition,
                        trackOffsetRotation,
                        out _,
                        out _,
                        out error))
                {
                    throw new InvalidOperationException(
                        $"Constraint Character pose solve failed: {error}");
                }

                Transform hips = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                    characterCache,
                    HumanBodyBones.Hips);
                if (hips == null)
                {
                    throw new InvalidOperationException(
                        "Constraint Character pose has no Hips transform.");
                }

                KimodoTimelineTrackOffsetUtility.WorldToTrackPose(
                    hips.position,
                    hips.rotation,
                    trackOffsetPosition,
                    trackOffsetRotation,
                    out Vector3 trackRootPosition,
                    out Quaternion trackRootRotation);
                hips.SetPositionAndRotation(trackRootPosition, trackRootRotation);

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        characterCache,
                        out MuscleSample solvedTrackPose,
                        out error))
                {
                    throw new InvalidOperationException(
                        $"Constraint Character pose capture failed: {error}");
                }

                return KimodoRuntimeConstraintExportProjector.ProjectSolvedMuscle(
                    solvedTrackPose,
                    modelName);
            }
            finally
            {
                characterCache.Dispose();
            }
        }
    }
}
