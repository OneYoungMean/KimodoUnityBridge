using System;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Retargets the source FK pose to the model profile, converts the source
    /// goal around the two FK endpoints, and solves the converted goal on the
    /// profile Avatar before exporting that pose.
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
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        supportSample,
                        frameRate,
                        profileCache,
                        out _,
                        out MuscleSample profileBaselineSample,
                        out error))
                {
                    return false;
                }

                if (!TryBuildProfileIkSample(
                        sample,
                        profileBaselineSample,
                        bone,
                        targetPosition,
                        targetRotation,
                        enableRotation,
                        out KimodoMarkerSampleResult profileSample,
                        out error))
                {
                    return false;
                }

                if (!KimodoConstraintPosePipeline.TryApplyCanonical(
                        profileSample,
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

        private bool TryBuildProfileIkSample(
            KimodoMarkerSampleResult sourceSample,
            MuscleSample profileBaselineSample,
            HumanBodyBones bone,
            Vector3 sourceGoal,
            Quaternion targetRotation,
            bool enableRotation,
            out KimodoMarkerSampleResult profileSample,
            out string error)
        {
            profileSample = null;
            error = string.Empty;
            if (sourceSample == null || profileBaselineSample == null ||
                sourceCache?.skeletonRoot == null || profileCache?.skeletonRoot == null ||
                sourceAnimator == null || sourceAnimator.GetBoneTransform(bone) == null)
            {
                error = "Runtime effector profile IK inputs are not initialized.";
                return false;
            }

            if (!TryConvertEndpointDeltaGoal(
                    sourceAnimator.transform,
                    sourceAnimator.GetBoneTransform(bone).position,
                    sourceCache.humanScale,
                    profileCache,
                    bone,
                    sourceGoal,
                    out Vector3 profileGoal,
                    out error) ||
                !profileCache.GetBonePose(
                    HumanBodyBones.Hips,
                    out Vector3 profileRootPosition,
                    out Quaternion profileRootRotation))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "Runtime effector profile IK requires a sampled profile Hips pose.";
                }
                return false;
            }

            profileSample = sourceSample.Clone();
            profileSample.sampleData = profileBaselineSample;
            profileSample.rootOverride = new KimodoRigidTransform
            {
                t = profileRootPosition,
                q = profileRootRotation.normalized,
                space = KimodoConstraintSpace.World
            };
            profileSample.effectors = new KimodoConstraintEffectors();
            KimodoRigidTransform target = ResolveEffector(profileSample.effectors, bone);
            target.t = profileGoal;
            target.q = targetRotation.normalized;
            target.enableRotation = enableRotation;
            target.space = KimodoConstraintSpace.World;
            return true;
        }

        internal static bool TryConvertEndpointDeltaGoal(
            Transform sourceRoot,
            Vector3 sourceBaselineEnd,
            float sourceHumanScale,
            RetargetSkeleton profileCache,
            HumanBodyBones bone,
            Vector3 sourceGoal,
            out Vector3 profileGoal,
            out string error)
        {
            profileGoal = Vector3.zero;
            error = string.Empty;
            if (sourceRoot == null || profileCache?.root == null)
            {
                error = "Runtime effector goal conversion requires both skeleton roots.";
                return false;
            }

            if (!profileCache.GetBonePose(bone, out Vector3 profileBaselineEnd, out _))
            {
                error = $"Runtime effector goal conversion requires a sampled profile '{bone}' pose.";
                return false;
            }

            // humanScale is the scale used by Unity's HumanPose RootTQ/FootTQ
            // transport. Use the live Animator's endpoint as the source anchor:
            // setting its HumanPose on a virtual skeleton can move that clone's
            // FK endpoint relative to the source scene Transform.
            sourceHumanScale = Mathf.Max(1e-6f, sourceHumanScale);
            float profileHumanScale = Mathf.Max(1e-6f, profileCache.humanScale);
            Vector3 sourceRootRelativeDelta = sourceRoot.InverseTransformVector(
                sourceGoal - sourceBaselineEnd);
            profileGoal = profileBaselineEnd +
                profileCache.root.transform.TransformVector(
                    sourceRootRelativeDelta * (profileHumanScale / sourceHumanScale));
            return true;
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
