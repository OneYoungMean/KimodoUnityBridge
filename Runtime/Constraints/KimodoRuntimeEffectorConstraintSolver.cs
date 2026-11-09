using System;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Solves one runtime effector on the source Avatar, then retargets the
    /// solved muscle pose to the model profile. The profile pose is only
    /// captured for export; it must not run a second IK pass.
    /// </summary>
    internal sealed class KimodoRuntimeEffectorConstraintSolver : IDisposable
    {
        private RetargetSkeleton sourceCache;
        private RetargetSkeleton profileCache;
        private HumanPoseHandler sourcePoseHandler;
        private Animator sourceAnimator;
        private Avatar sourceAvatar;
        private string profileModelName;

        internal bool EnsureReady(
            Animator source,
            string modelName,
            out string error)
        {
            error = string.Empty;
            if (source == null || !KimodoRetargetCoreUtility.IsValidHumanoid(source.avatar))
            {
                error = "Runtime effector solving requires a valid source humanoid Animator.";
                return false;
            }

            if (sourceCache != null &&
                profileCache != null &&
                ReferenceEquals(sourceAnimator, source) &&
                ReferenceEquals(sourceAvatar, source.avatar) &&
                string.Equals(profileModelName, modelName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            DisposeCaches();
            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    source.avatar,
                    "KimodoRuntimeEffectorSource",
                    out sourceCache,
                    out error))
            {
                return false;
            }

            try
            {
                sourcePoseHandler = new HumanPoseHandler(source.avatar, source.transform);
            }
            catch (Exception ex)
            {
                error = $"Build runtime effector source pose handler failed: {ex.Message}";
                DisposeCaches();
                return false;
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    modelName,
                    out Avatar profileAvatar,
                    out error) ||
                !KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    profileAvatar,
                    "KimodoRuntimeEffectorProfile",
                    out profileCache,
                    out error))
            {
                DisposeCaches();
                return false;
            }

            sourceAnimator = source;
            sourceAvatar = source.avatar;
            profileModelName = modelName;
            return true;
        }

        internal bool TryBuild(
            Animator source,
            string modelName,
            HumanBodyBones bone,
            Vector3 targetPosition,
            Quaternion targetRotation,
            bool enableRotation,
            double sampleTime,
            out KimodoRuntimeEffectorConstraint result,
            out string error)
        {
            result = null;
            error = string.Empty;
            string jointType = ResolveJointType(bone);
            if (jointType == null)
            {
                error = $"Unsupported runtime effector bone '{bone}'.";
                return false;
            }

            if (!EnsureReady(source, modelName, out error))
            {
                return false;
            }

            try
            {
                if (!TryApplySourcePose(out MuscleSample supportSample, out error))
                {
                    return false;
                }

                if (!sourceCache.GetBonePose(
                        HumanBodyBones.Hips,
                        out Vector3 hipsPosition,
                        out Quaternion hipsRotation))
                {
                    error = "Runtime effector solving requires a sampled source Hips pose.";
                    return false;
                }

                var sample = new KimodoMarkerSampleResult
                {
                    sampleData = supportSample,
                    sampleValid = true,
                    enabled = true,
                    constraintMode = "effector",
                    sampleTime = sampleTime,
                    rootOverride = new KimodoRigidTransform
                    {
                        t = hipsPosition,
                        q = hipsRotation.normalized,
                        space = KimodoConstraintSpace.World
                    },
                    enableMask = KimodoConstraintMask.ForType(jointType),
                    effectors = new KimodoConstraintEffectors()
                };

                KimodoRigidTransform target = ResolveEffector(sample.effectors, bone);
                target.t = targetPosition;
                target.q = targetRotation.normalized;
                target.enableRotation = enableRotation;
                target.space = KimodoConstraintSpace.World;

                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
                if (!KimodoConstraintPosePipeline.TryApplyCanonical(
                        sample,
                        frameRate,
                        sourceCache,
                        out _,
                        out MuscleSample solvedMuscle,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        solvedMuscle,
                        frameRate,
                        profileCache,
                        out _,
                        out _,
                        out error))
                {
                    return false;
                }

                KimodoConstraintProjectedPose projected =
                    KimodoRuntimeConstraintExportProjector.CaptureProfilePose(
                        modelName,
                        profileCache);
                KimodoConstraintJson json =
                    KimodoConstraintJsonExporter.BuildEndEffectorFromProjectedPose(
                        projected,
                        jointType,
                        sampleTime,
                        0.0,
                        null,
                        frameRate);
                result = new KimodoRuntimeEffectorConstraint
                {
                    sampleTime = sampleTime,
                    json = json
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool TryApplySourcePose(out MuscleSample sample, out string error)
        {
            sample = null;
            error = string.Empty;
            if (sourceAnimator == null || sourceCache == null)
            {
                error = "Runtime effector source cache is not initialized.";
                return false;
            }

            if (sourcePoseHandler == null)
            {
                error = "Runtime effector target pose handler is not initialized.";
                return false;
            }

            var pose = new HumanPose();
            sourcePoseHandler.GetHumanPose(ref pose);

            sourceCache.root.transform.SetPositionAndRotation(
                sourceAnimator.transform.position,
                sourceAnimator.transform.rotation);
            sourceCache.root.transform.localScale = sourceAnimator.transform.lossyScale;
            sourceCache.animator.Rebind();
            sourceCache.animator.Update(0f);
            sourceCache.poseHandler.SetHumanPose(ref pose);
            sourceCache.animator.Update(0f);
            return KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                sourceCache,
                out sample,
                out error);
        }

        private static KimodoRigidTransform ResolveEffector(
            KimodoConstraintEffectors effectors,
            HumanBodyBones bone)
        {
            return bone switch
            {
                HumanBodyBones.LeftHand => effectors.leftHand,
                HumanBodyBones.RightHand => effectors.rightHand,
                HumanBodyBones.LeftFoot => effectors.leftFoot,
                HumanBodyBones.RightFoot => effectors.rightFoot,
                _ => throw new InvalidOperationException($"Unsupported runtime effector bone '{bone}'.")
            };
        }

        private static string ResolveJointType(HumanBodyBones bone)
        {
            return bone switch
            {
                HumanBodyBones.LeftHand => "left-hand",
                HumanBodyBones.RightHand => "right-hand",
                HumanBodyBones.LeftFoot => "left-foot",
                HumanBodyBones.RightFoot => "right-foot",
                _ => null
            };
        }

        public void Dispose()
        {
            DisposeCaches();
            sourceAnimator = null;
            sourceAvatar = null;
            profileModelName = null;
        }

        private void DisposeCaches()
        {
            sourcePoseHandler?.Dispose();
            sourceCache?.Dispose();
            profileCache?.Dispose();
            sourcePoseHandler = null;
            sourceCache = null;
            profileCache = null;
        }
    }

    internal sealed class KimodoRuntimeEffectorConstraint
    {
        internal double sampleTime;
        internal KimodoConstraintJson json;

        internal KimodoRuntimeEffectorConstraint Clone()
        {
            return new KimodoRuntimeEffectorConstraint
            {
                sampleTime = sampleTime,
                json = KimodoRuntimeConstraints.CloneJson(json)
            };
        }
    }
}
