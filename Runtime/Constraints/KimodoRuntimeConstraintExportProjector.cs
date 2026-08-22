using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Projects the canonical 70D MuscleSample onto the model's protocol skeleton.
    /// Generate never reconstructs the command-layer CharacterPose here.
    /// </summary>
    public static class KimodoRuntimeConstraintExportProjector
    {
        public static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            string modelName)
        {
            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            return sample => Project(sample, resolvedModelName);
        }

        private static KimodoConstraintProjectedPose Project(
            KimodoMarkerSampleResult sample,
            string modelName)
        {
            if (sample?.sampleData == null || !sample.sampleData.IsValid ||
                sample.enableMask?.muscle49 != true)
            {
                throw new InvalidOperationException("Constraint MuscleSample is invalid.");
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    modelName,
                    out Avatar avatar,
                    out string error) ||
                !KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar,
                    "KimodoRuntimeConstraintExportProfile",
                    out RetargetSkeleton cache,
                    out error))
            {
                throw new InvalidOperationException($"Constraint pose projection failed: {error}");
            }

            try
            {
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
                MuscleSample sourceSample = sample.sampleData.Clone();
                MuscleSample projectedMuscleSample;
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        sourceSample,
                        frameRate,
                        cache,
                        out _,
                        out projectedMuscleSample,
                        out error))
                {
                    throw new InvalidOperationException($"Constraint pose projection failed: {error}");
                }

                // IK/effector values are retained for protocol compatibility only.
                // Projection deliberately sends the FK pose reconstructed from muscles.

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
                    localJointAngles = result,
                };
            }
            finally
            {
                cache.Dispose();
            }
        }

    }
}
