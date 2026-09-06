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
            if (context != null && context.HasTrackOffsetSnapshot)
            {
                return Create(
                    context,
                    context.TrackOffsetPosition,
                    context.TrackOffsetRotation);
            }
            return sample => ProjectTimelineSample(sample, context);
        }

        internal static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            KimodoTimelineInOutConstraintContext context,
            Vector3 trackOffsetPosition,
            Quaternion trackOffsetRotation)
        {
            return sample => ProjectTimelineSample(
                sample,
                context,
                trackOffsetPosition,
                trackOffsetRotation);
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

            KimodoTimelineTrackOffsetUtility.CaptureWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation,
                out _);
            return ProjectTimelineSample(sample, context, trackOffsetPosition, trackOffsetRotation);
        }

        private static KimodoConstraintProjectedPose ProjectTimelineSample(
            KimodoMarkerSampleResult sample,
            KimodoTimelineInOutConstraintContext context,
            Vector3 trackOffsetPosition,
            Quaternion trackOffsetRotation)
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

                // The solved character is still entirely in world space here.
                // Converting only Hips leaves the avatar's canonical skeleton
                // root (and therefore HumanPose.bodyPosition) in world space.
                // Re-sampling that mixed hierarchy produces a root offset even
                // when all joint rotations are correct. Convert every cached
                // bone by the same rigid world -> track transform instead.
                ConvertCharacterPoseToTrackSpace(
                    characterCache,
                    trackOffsetPosition,
                    trackOffsetRotation);

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    characterCache,
                    out MuscleSample solvedTrackPose,
                    out error))
                {
                    throw new InvalidOperationException(
                        $"Constraint Character pose capture failed: {error}");
                }

                // The solved MuscleSample is the single source of truth for
                // the profile skeleton. Do not overwrite its root from a
                // separately sampled Hips Transform after retargeting; that
                // would insert a second root correction between muscle and
                // bone sampling.
                return KimodoRuntimeConstraintExportProjector.ProjectSolvedMuscle(
                    solvedTrackPose,
                    modelName);
            }
            finally
            {
                characterCache.Dispose();
            }
        }

        private static void ConvertCharacterPoseToTrackSpace(
            RetargetSkeleton cache,
            Vector3 trackPosition,
            Quaternion trackRotation)
        {
            if (cache?.boneTransforms == null || cache.boneTransforms.Length == 0)
            {
                return;
            }

            Quaternion normalizedTrackRotation = trackRotation;
            if (normalizedTrackRotation.x * normalizedTrackRotation.x +
                normalizedTrackRotation.y * normalizedTrackRotation.y +
                normalizedTrackRotation.z * normalizedTrackRotation.z +
                normalizedTrackRotation.w * normalizedTrackRotation.w <= 1e-8f)
            {
                normalizedTrackRotation = Quaternion.identity;
            }
            else
            {
                normalizedTrackRotation.Normalize();
            }

            Quaternion inverseTrackRotation = Quaternion.Inverse(normalizedTrackRotation);
            int count = cache.boneTransforms.Length;
            var worldPositions = new Vector3[count];
            var worldRotations = new Quaternion[count];
            var valid = new bool[count];
            for (int i = 0; i < count; i++)
            {
                Transform bone = cache.boneTransforms[i];
                if (bone == null)
                {
                    continue;
                }

                valid[i] = true;
                worldPositions[i] = inverseTrackRotation * (bone.position - trackPosition);
                worldRotations[i] = (inverseTrackRotation * bone.rotation).normalized;
            }

            // Set world poses from the captured snapshot. Applying a parent
            // first may move its children, but each child is restored from the
            // transformed snapshot when its turn is reached.
            for (int i = 0; i < count; i++)
            {
                if (!valid[i])
                {
                    continue;
                }

                cache.boneTransforms[i].SetPositionAndRotation(
                    worldPositions[i],
                    worldRotations[i]);
            }
        }

    }
}
