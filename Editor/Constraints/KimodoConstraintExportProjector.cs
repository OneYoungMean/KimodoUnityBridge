using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintExportProjector
    {
        internal static Func<CharacterAnimationCli.Unity.CharacterPose, List<Vector3>> Create(string modelName)
        {
            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            return pose => Project(pose, resolvedModelName);
        }

        private static List<Vector3> Project(
            CharacterAnimationCli.Unity.CharacterPose pose,
            string modelName)
        {
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
                    "KimodoConstraintExportProfile",
                    out SkeletonCache cache,
                    out error))
            {
                throw new InvalidOperationException($"Constraint pose projection failed: {error}");
            }

            try
            {
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        CharacterPoseMuscleAdapter.ToMuscleSample(pose),
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
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

                var result = new List<Vector3>(joints.Length);
                for (int i = 0; i < joints.Length; i++)
                {
                    Transform joint = joints[i];
                    Quaternion rotation = i == 0 ? joint.rotation : joint.localRotation;
                    result.Add(KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(rotation));
                }
                return result;
            }
            finally
            {
                cache.Dispose();
            }
        }
    }
}
