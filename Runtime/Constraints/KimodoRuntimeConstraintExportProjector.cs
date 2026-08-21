using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
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
        public static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            string modelName,
            Avatar sourceAvatar = null)
        {
            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            return sample => Project(sample, resolvedModelName, sourceAvatar);
        }

        private static KimodoConstraintProjectedPose Project(
            KimodoMarkerSampleResult sample,
            string modelName,
            Avatar sourceAvatar)
        {
            CharacterAnimationCli.Unity.CharacterPose pose = null;
            if (sample != null && sample.enableMask?.muscle49 == true &&
                CharacterPoseMuscleAdapter.TryFromSampleData(
                    sample.sampleData,
                    out CharacterAnimationCli.Unity.CharacterPose decoded,
                    out _))
            {
                pose = decoded;
            }
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

            SkeletonCache sourceCache = null;
            try
            {
                if (KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar) &&
                    !KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        sourceAvatar,
                        "KimodoRuntimeConstraintSourceProfile",
                        out sourceCache,
                        out error))
                {
                    throw new InvalidOperationException($"Constraint source Avatar cache failed: {error}");
                }
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample.mask, sample.constraintType);
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
                MuscleSample sourceSample = CharacterPoseMuscleAdapter.ToBodyMuscleSample(pose);
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
                    projectedPose = CharacterPoseMuscleAdapter.FromMuscleSample(projectedMuscleSample, cache)
                };
            }
            finally
            {
                sourceCache?.Dispose();
                cache.Dispose();
            }
        }

    }
}
