using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Projects a canonical CharacterPose onto the model's protocol skeleton.
    /// Runtime generation must use this instead of the root-only exporter fallback.
    /// </summary>
    public static class KimodoRuntimeConstraintExportProjector
    {
        public static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(string modelName)
        {
            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            return sample => Project(sample, resolvedModelName);
        }

        private static KimodoConstraintProjectedPose Project(
            KimodoMarkerSampleResult sample,
            string modelName)
        {
            CharacterAnimationCli.Unity.CharacterPose pose = sample?.characterPose;
            string poseError = null;
            if (pose == null || !pose.TryValidate(out poseError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(poseError)
                    ? "Constraint pose is invalid."
                    : poseError);
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    modelName,
                    out Avatar avatar,
                    out string error) ||
                !KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoRuntimeConstraintExportProfile",
                    out SkeletonCache cache,
                    out error))
            {
                throw new InvalidOperationException($"Constraint pose projection failed: {error}");
            }

            try
            {
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample.mask, sample.constraintType);
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        CharacterPoseMuscleAdapter.ToMuscleSample(pose),
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                        cache,
                        out _,
                        out MuscleSample projectedMuscleSample,
                        out error,
                        solveLeftHandIk: mask.leftHand,
                        solveRightHandIk: mask.rightHand,
                        applyFootIk: mask.leftFoot || mask.rightFoot))
                {
                    throw new InvalidOperationException($"Constraint pose projection failed: {error}");
                }

                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        modelName,
                        cache,
                        out _,
                        out _,
                        out Transform[] joints,
                        out error))
                {
                    throw new InvalidOperationException($"Constraint profile skeleton failed: {error}");
                }

                if (joints == null || joints.Length == 0 || joints[0] == null || projectedMuscleSample == null)
                {
                    throw new InvalidOperationException("Constraint profile skeleton has no Hips joint after projection.");
                }

                var result = new List<Vector3>(joints.Length);
                for (int i = 0; i < joints.Length; i++)
                {
                    Transform joint = joints[i];
                    Quaternion rotation = i == 0 ? joint.rotation : joint.localRotation;
                    result.Add(KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(rotation));
                }

                return new KimodoConstraintProjectedPose
                {
                    rootPositionMeters = joints[0].position,
                    localJointAngles = result
                };
            }
            finally
            {
                cache.Dispose();
            }
        }
    }
}
