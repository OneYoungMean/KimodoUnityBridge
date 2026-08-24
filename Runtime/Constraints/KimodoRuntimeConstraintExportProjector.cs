using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Projects model-native constraint samples onto the protocol skeleton.
    /// Timeline character samples use ProjectSolvedMuscle after their
    /// Character FK/root/IK pass has completed.
    /// </summary>
    public static class KimodoRuntimeConstraintExportProjector
    {
        public static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            string modelName)
        {
            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            return sample => ProjectProfileNative(sample, resolvedModelName);
        }

        private static KimodoConstraintProjectedPose ProjectProfileNative(
            KimodoMarkerSampleResult sample,
            string modelName)
        {
            KimodoConstraintMask mask = KimodoConstraintMask.FromSample(sample);
            string mode = KimodoConstraintInternal.NormalizeMode(sample?.constraintMode);
            bool rootOnly = (mode == "root2d" || mode == "mix") &&
                mask.rootPosition &&
                sample?.rootOverride != null &&
                !mask.muscle &&
                !mask.leftHand && !mask.rightHand &&
                !mask.leftFoot && !mask.rightFoot;
            if (!rootOnly && (sample?.sampleData == null || !sample.sampleData.IsValid || !mask.muscle))
            {
                throw new InvalidOperationException("Constraint MuscleSample is invalid.");
            }

            if (!TryBuildProfileCache(modelName, out RetargetSkeleton cache, out string error))
            {
                throw new InvalidOperationException($"Constraint pose projection failed: {error}");
            }

            try
            {
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
                if (!KimodoConstraintPosePipeline.TryApply(
                        sample,
                        frameRate,
                        cache,
                        out _,
                        out _,
                        out error))
                {
                    throw new InvalidOperationException($"Constraint pose projection failed: {error}");
                }

                return CaptureProfilePose(modelName, cache);
            }
            finally
            {
                cache.Dispose();
            }
        }

        internal static KimodoConstraintProjectedPose ProjectSolvedMuscle(
            MuscleSample solvedTrackPose,
            string modelName)
        {
            if (solvedTrackPose == null || !solvedTrackPose.IsValid)
            {
                throw new InvalidOperationException("Solved Character MuscleSample is invalid.");
            }

            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            if (!TryBuildProfileCache(resolvedModelName, out RetargetSkeleton cache, out string error))
            {
                throw new InvalidOperationException($"Constraint profile projection failed: {error}");
            }

            try
            {
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        solvedTrackPose,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(resolvedModelName),
                        cache,
                        out _,
                        out _,
                        out error))
                {
                    throw new InvalidOperationException($"Constraint profile retarget failed: {error}");
                }

                return CaptureProfilePose(resolvedModelName, cache);
            }
            finally
            {
                cache.Dispose();
            }
        }

        private static bool TryBuildProfileCache(
            string modelName,
            out RetargetSkeleton cache,
            out string error)
        {
            cache = null;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    modelName,
                    out Avatar avatar,
                    out error))
            {
                return false;
            }

            return KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar,
                "KimodoRuntimeConstraintExportProfile",
                out cache,
                out error);
        }

        private static KimodoConstraintProjectedPose CaptureProfilePose(
            string modelName,
            RetargetSkeleton cache)
        {
            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    modelName,
                    cache,
                    out string[] jointNames,
                    out _,
                    out Transform[] joints,
                    out string error))
            {
                throw new InvalidOperationException($"Constraint profile skeleton failed: {error}");
            }

            if (joints == null || joints.Length == 0 || joints[0] == null)
            {
                throw new InvalidOperationException("Constraint profile skeleton has no Hips joint after projection.");
            }

            var jointPositions = new Vector3[joints.Length];
            var jointRotations = new Quaternion[joints.Length];
            var localJointAngles = new List<Vector3>(joints.Length);
            for (int i = 0; i < joints.Length; i++)
            {
                Transform joint = joints[i];
                Quaternion rotation = i == 0 ? joint.rotation : joint.localRotation;
                jointPositions[i] = joint.position;
                jointRotations[i] = joint.rotation;
                localJointAngles.Add(KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(rotation));
            }

            return new KimodoConstraintProjectedPose
            {
                profileRootPosition = joints[0].position,
                jointNames = jointNames,
                jointPositions = jointPositions,
                jointRotations = jointRotations,
                localJointAngles = localJointAngles,
            };
        }

    }
}
