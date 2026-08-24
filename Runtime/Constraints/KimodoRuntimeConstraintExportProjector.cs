using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Projects constraint samples onto the model's protocol skeleton.
    /// FullBody uses its canonical 70D MuscleSample; Root2D-only samples use
    /// the profile skeleton initial pose while preserving their world target.
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

                if (joints == null || joints.Length == 0 || joints[0] == null)
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
