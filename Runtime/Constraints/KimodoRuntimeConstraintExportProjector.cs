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

            SkeletonCache sourceCache = null;
            KimodoRetargetClipSamplingUtility.HumanoidIkTargetScope targetScope = null;
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
                MuscleSample sourceSample = CharacterPoseMuscleAdapter.ToMuscleSample(pose);
                MuscleSample projectedMuscleSample;
                bool hasWorldIkTargets = sample.worldIkTargets != null &&
                    ((mask.leftHand && sample.worldIkTargets.hands?.left != null) ||
                     (mask.rightHand && sample.worldIkTargets.hands?.right != null) ||
                     (mask.leftFoot && sample.worldIkTargets.feet?.left != null) ||
                     (mask.rightFoot && sample.worldIkTargets.feet?.right != null));
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

                if (hasWorldIkTargets)
                {
                    KimodoRetargetClipSamplingUtility.HumanoidWorldIkTargets worldTargets =
                        ConvertWorldIkTargetsToTargetAvatarSpace(
                            sample.worldIkTargets,
                            sample.sourceRootWorldPose,
                            sourceCache != null ? sourceCache.humanScale : 1f,
                            cache,
                            projectedMuscleSample,
                            mask);
                    targetScope = KimodoRetargetClipSamplingUtility.HumanoidIkTargetScope.Create(
                        worldTargets,
                        out error);
                    if (targetScope == null ||
                        !KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                            projectedMuscleSample,
                            frameRate,
                            cache,
                            out _,
                            out projectedMuscleSample,
                            out error,
                            solveLeftHandIk: mask.leftHand,
                            solveRightHandIk: mask.rightHand,
                            applyFootIk: mask.leftFoot || mask.rightFoot,
                            solveLeftFootIk: mask.leftFoot,
                            solveRightFootIk: mask.rightFoot,
                            ikGoalsAlreadyInTargetSpace: true,
                            sceneTargets: targetScope.Targets))
                    {
                        throw new InvalidOperationException($"Constraint pose IK solve failed: {error}");
                    }
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
                sourceCache?.Dispose();
                targetScope?.Dispose();
                cache.Dispose();
            }
        }

        private static KimodoRetargetClipSamplingUtility.HumanoidWorldIkTargets ConvertWorldIkTargetsToTargetAvatarSpace(
            KimodoConstraintIkTargets source,
            CharacterPoseTransform sourceRoot,
            float sourceHumanScale,
            SkeletonCache targetCache,
            MuscleSample projectedSample,
            KimodoConstraintMask mask)
        {
            var result = new KimodoRetargetClipSamplingUtility.HumanoidWorldIkTargets();
            if (source == null || targetCache == null || projectedSample?.pose == null) return result;
            Vector3 sourcePosition = sourceRoot != null ? sourceRoot.t : Vector3.zero;
            Quaternion sourceRotation = sourceRoot != null ? sourceRoot.q.normalized : Quaternion.identity;
            float scale = Mathf.Max(1e-6f, targetCache.humanScale) / Mathf.Max(1e-6f, sourceHumanScale);
            Vector3 targetPosition = projectedSample.pose.bodyPosition * targetCache.humanScale;
            Quaternion targetRotation = projectedSample.pose.bodyRotation;
            CopyTarget(source.hands?.left, mask.leftHand, ref result.leftHand, ref result.leftHandPosition, ref result.leftHandRotation,
                sourcePosition, sourceRotation, scale, targetPosition, targetRotation);
            CopyTarget(source.hands?.right, mask.rightHand, ref result.rightHand, ref result.rightHandPosition, ref result.rightHandRotation,
                sourcePosition, sourceRotation, scale, targetPosition, targetRotation);
            CopyTarget(source.feet?.left, mask.leftFoot, ref result.leftFoot, ref result.leftFootPosition, ref result.leftFootRotation,
                sourcePosition, sourceRotation, scale, targetPosition, targetRotation);
            CopyTarget(source.feet?.right, mask.rightFoot, ref result.rightFoot, ref result.rightFootPosition, ref result.rightFootRotation,
                sourcePosition, sourceRotation, scale, targetPosition, targetRotation);
            return result;
        }

        private static void CopyTarget(
            CharacterPoseTransform source,
            bool enabled,
            ref bool targetEnabled,
            ref Vector3 targetPosition,
            ref Quaternion targetRotation,
            Vector3 sourceRootPosition,
            Quaternion sourceRootRotation,
            float scale,
            Vector3 targetRootPosition,
            Quaternion targetRootRotation)
        {
            if (!enabled || source == null) return;
            Vector3 local = Quaternion.Inverse(sourceRootRotation) * (source.t - sourceRootPosition);
            targetPosition = targetRootPosition + targetRootRotation * (local * scale);
            targetRotation = targetRootRotation * (Quaternion.Inverse(sourceRootRotation) * source.q.normalized);
            targetEnabled = true;
        }
    }
}
