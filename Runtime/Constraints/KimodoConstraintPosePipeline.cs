using System;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoBridge
{
    /// <summary>
    /// Canonical constraint pose path shared by preview and protocol
    /// projection: FK, optional hips override, and SampleResult IK. Explicit
    /// targets use the Transform space occupied by the supplied skeleton.
    /// Timeline generation supplies its track-to-world FK placement; preview
    /// and model-native projection use the identity overload.
    /// </summary>
    internal static class KimodoConstraintPosePipeline
    {
        internal static bool TryApply(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton cache,
            out BoneSample boneSample,
            out MuscleSample muscleSample,
            out string error)
        {
            return TryApply(
                sample,
                frameRate,
                cache,
                Vector3.zero,
                Quaternion.identity,
                out boneSample,
                out muscleSample,
                out error);
        }

        internal static bool TryApply(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton cache,
            Vector3 fkToTargetPosition,
            Quaternion fkToTargetRotation,
            out BoneSample boneSample,
            out MuscleSample muscleSample,
            out string error)
        {
            boneSample = null;
            muscleSample = null;
            error = string.Empty;

            if (sample == null || cache == null)
            {
                error = "Constraint pose input is null.";
                return false;
            }

            // A Root2D constraint has no authored MuscleSample, but it still
            // follows the same profile-skeleton path as FullBody: use the
            // target avatar's initial pose as the FK input, apply the explicit
            // hips override, then run the common IK stage.
            KimodoMarkerSampleResult pipelineSample = sample;
            if (IsRootOnlySample(sample))
            {
                if (!TryBuildRootOnlyPipelineSample(sample, cache, out pipelineSample, out error))
                {
                    return false;
                }
            }

            if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                    pipelineSample.sampleData,
                    frameRate,
                    cache,
                    out boneSample,
                    out _,
                    out error))
            {
                return false;
            }

            if (!TryPlaceFkPoseInTargetSpace(
                    cache,
                    fkToTargetPosition,
                    fkToTargetRotation,
                    out error))
            {
                return false;
            }

            if (!pipelineSample.rootOverrideAfterEffectors &&
                !TryApplyRootOverride(pipelineSample, cache, out error))
            {
                return false;
            }

            if (!KimodoConstraintIkSolver.TryApply(pipelineSample, frameRate, cache, out error))
            {
                return false;
            }

            if (pipelineSample.rootOverrideAfterEffectors &&
                !TryApplyRootOverride(pipelineSample, cache, out error))
            {
                return false;
            }

            boneSample = KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    cache,
                    out muscleSample,
                    out error))
            {
                return false;
            }

            return true;
        }

        private static bool TryPlaceFkPoseInTargetSpace(
            RetargetSkeleton cache,
            Vector3 position,
            Quaternion rotation,
            out string error)
        {
            error = string.Empty;
            float rotationLength = rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w;
            rotation = rotationLength > 1e-8f ? rotation.normalized : Quaternion.identity;
            if (position == Vector3.zero && rotation == Quaternion.identity)
            {
                return true;
            }

            Transform hips = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                cache,
                HumanBodyBones.Hips);
            if (hips == null)
            {
                error = "Constraint FK target-space placement requires an Hips transform.";
                return false;
            }

            hips.SetPositionAndRotation(
                position + rotation * hips.position,
                rotation * hips.rotation);
            return true;
        }

        internal static bool IsRootOnlySample(KimodoMarkerSampleResult sample)
        {
            if (sample == null || !KimodoConstraintMask.IsActive(sample, "rootposition"))
            {
                return false;
            }

            string mode = KimodoConstraintInternal.NormalizeMode(sample.constraintMode);
            if (mode == "root2d")
            {
                return sample.rootOverride != null;
            }
            if (mode != "mix")
            {
                return false;
            }

            if (KimodoConstraintMask.IsActive(sample, "muscle") ||
                KimodoConstraintMask.IsActive(sample, "lefthand") ||
                KimodoConstraintMask.IsActive(sample, "righthand") ||
                KimodoConstraintMask.IsActive(sample, "leftfoot") ||
                KimodoConstraintMask.IsActive(sample, "rightfoot"))
            {
                return false;
            }

            return sample.rootOverride != null;
        }

        private static bool TryBuildRootOnlyPipelineSample(
            KimodoMarkerSampleResult source,
            RetargetSkeleton cache,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;
            if (source == null || cache == null || source.rootOverride == null)
            {
                error = "Root2D pipeline input is invalid.";
                return false;
            }

            KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(cache);
            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    cache,
                    out MuscleSample initialPose,
                    out error) ||
                initialPose == null ||
                !initialPose.IsValid)
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "Failed to capture the target skeleton initial pose for Root2D.";
                }
                return false;
            }

            result = source.Clone();
            result.sampleData = initialPose;
            result.constraintMode = "root2d";
            result.enableMask ??= new KimodoConstraintMask();
            result.validMask ??= new KimodoConstraintMask();
            result.enableMask.muscle = false;
            result.enableMask.rootTQ = false;
            result.enableMask.leftFootTQ = false;
            result.enableMask.rightFootTQ = false;
            result.validMask.muscle = false;
            result.validMask.rootTQ = false;
            result.validMask.leftFootTQ = false;
            result.validMask.rightFootTQ = false;
            result.enableMask.leftHand = false;
            result.enableMask.rightHand = false;
            result.enableMask.leftFoot = false;
            result.enableMask.rightFoot = false;
            result.validMask.leftHand = false;
            result.validMask.rightHand = false;
            result.validMask.leftFoot = false;
            result.validMask.rightFoot = false;
            result.effectors = new KimodoConstraintEffectors();
            return true;
        }

        private static bool TryApplyRootOverride(
            KimodoMarkerSampleResult sample,
            RetargetSkeleton cache,
            out string error)
        {
            error = string.Empty;
            if (!KimodoConstraintMask.IsActive(sample, "rootposition") ||
                sample.rootOverride == null)
            {
                return true;
            }

            Transform hips = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                cache,
                HumanBodyBones.Hips);
            if (hips == null)
            {
                error = "Constraint root override requires an Hips transform.";
                return false;
            }

            hips.SetPositionAndRotation(
                sample.rootOverride.t,
                sample.rootOverride.q.normalized);
            return true;
        }

    }

    /// <summary>
    /// Humanoid IK job whose targets are copied value data from SampleResult.
    /// No scene Transform or external rig is read by the job.
    /// </summary>
    internal static class KimodoConstraintIkSolver
    {
        private struct SolveJob : IAnimationJob
        {
            public bool solveLeftHand;
            public bool solveRightHand;
            public bool solveLeftFoot;
            public bool solveRightFoot;
            public Vector3 leftHandPosition;
            public Quaternion leftHandRotation;
            public Vector3 rightHandPosition;
            public Quaternion rightHandRotation;
            public Vector3 leftFootPosition;
            public Quaternion leftFootRotation;
            public Vector3 rightFootPosition;
            public Quaternion rightFootRotation;

            public void ProcessRootMotion(AnimationStream stream) { }

            public void ProcessAnimation(AnimationStream stream)
            {
                if (!stream.isHumanStream)
                {
                    return;
                }

                AnimationHumanStream human = stream.AsHuman();
                ApplyGoal(human, AvatarIKGoal.LeftHand, solveLeftHand,
                    leftHandPosition, leftHandRotation);
                ApplyGoal(human, AvatarIKGoal.RightHand, solveRightHand,
                    rightHandPosition, rightHandRotation);
                ApplyGoal(human, AvatarIKGoal.LeftFoot, solveLeftFoot,
                    leftFootPosition, leftFootRotation);
                ApplyGoal(human, AvatarIKGoal.RightFoot, solveRightFoot,
                    rightFootPosition, rightFootRotation);

                if (solveLeftHand || solveRightHand || solveLeftFoot || solveRightFoot)
                {
                    human.SolveIK();
                }
            }

            private static void ApplyGoal(
                AnimationHumanStream human,
                AvatarIKGoal goal,
                bool enabled,
                Vector3 position,
                Quaternion rotation)
            {
                bool isHand = goal == AvatarIKGoal.LeftHand || goal == AvatarIKGoal.RightHand;

                human.SetGoalWeightPosition(goal, enabled ? 1f : 0f);
                human.SetGoalWeightRotation(goal, enabled && !isHand ? 1f : 0f);
                if (!enabled)
                {
                    return;
                }

                human.SetGoalPosition(goal, position);
                human.SetGoalRotation(goal, rotation);
            }
        }
        internal static bool TryApply(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton cache,
            out string error)
        {
            error = string.Empty;
            if (!TryBuildJob(sample, cache, out SolveJob job, out bool any, out error) || !any)
            {
                return string.IsNullOrEmpty(error);
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    cache,
                    out MuscleSample inputMuscle,
                    out error) ||
                inputMuscle == null ||
                !inputMuscle.IsValid)
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "Failed to capture a valid retargeted MuscleSample before IK.";
                }
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                    new[] { inputMuscle },
                    frameRate,
                    out AnimationClip clip,
                    out error))
            {
                return false;
            }

        
            PlayableGraph graph = default;
            Avatar originalAvatar = null;
            bool restoreAvatar = false;
            BoneSample solved = null;
            try
            {
                if (!KimodoRetargetClipSamplingUtility.TryConfigureAnimatorForClipSampling(
                        cache,
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out originalAvatar,
                        out restoreAvatar,
                        out error))
                {
                    return false;
                }
                graph = PlayableGraph.Create("KimodoConstraintIkGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationClipPlayable clipPlayable =
                    KimodoRetargetClipSamplingUtility.CreateClipPlayable(graph, clip);
                Playable sourcePlayable = AnimationOffsetPlayableAccess.CreateMotionXToDeltaAndConnect(
                    graph,
                    clipPlayable);
                AnimationScriptPlayable ikPlayable = AnimationScriptPlayable.Create(
                    graph,
                    job,
                    1);
                graph.Connect(sourcePlayable, 0, ikPlayable, 0);
                ikPlayable.SetInputWeight(0, 1f);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                    graph,
                    "KimodoConstraintIkOutput",
                    cache.animator);
                output.SetSourcePlayable(ikPlayable);
                clipPlayable.SetTime(0f);
                graph.Play();
                graph.Evaluate(0f);

                solved = KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (graph.IsValid())
                {
                    graph.Destroy();
                }
                if (restoreAvatar)
                {
                    KimodoRetargetClipSamplingUtility.RestoreAnimatorAfterClipSampling(
                        cache,
                        originalAvatar);
                }
                UnityEngine.Object.DestroyImmediate(clip);
               
            }

            return solved != null &&
                KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                    solved,
                    cache,
                    out error);
        }

        private static bool TryBuildJob(
            KimodoMarkerSampleResult sample,
            RetargetSkeleton cache,
            out SolveJob job,
            out bool any,
            out string error)
        {
            job = default;
            any = false;
            error = string.Empty;
            if (sample?.effectors == null || cache == null)
            {
                return true;
            }

            any |= job.solveLeftHand = KimodoConstraintMask.IsActive(sample, "lefthand");
            any |= job.solveRightHand = KimodoConstraintMask.IsActive(sample, "righthand");
            any |= job.solveLeftFoot = KimodoConstraintMask.IsActive(sample, "leftfoot");
            any |= job.solveRightFoot = KimodoConstraintMask.IsActive(sample, "rightfoot");

            if (!TryResolveTarget(sample.effectors.leftHand, job.solveLeftHand,
                    HumanBodyBones.LeftHand, out job.leftHandPosition, out job.leftHandRotation, out error) ||
                !TryResolveTarget(sample.effectors.rightHand, job.solveRightHand,
                    HumanBodyBones.RightHand, out job.rightHandPosition, out job.rightHandRotation, out error) ||
                !TryResolveTarget(sample.effectors.leftFoot, job.solveLeftFoot,
                    HumanBodyBones.LeftFoot, out job.leftFootPosition, out job.leftFootRotation, out error) ||
                !TryResolveTarget(sample.effectors.rightFoot, job.solveRightFoot,
                    HumanBodyBones.RightFoot, out job.rightFootPosition, out job.rightFootRotation, out error))
            {
                return false;
            }
            return true;
        }

        private static bool TryResolveTarget(
            KimodoRigidTransform value,
            bool enabled,
            HumanBodyBones bone,
            out Vector3 position,
            out Quaternion rotation,
            out string error)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            error = string.Empty;
            if (!enabled)
            {
                return true;
            }
            if (value == null)
            {
                error = $"Constraint effector '{bone}' is enabled but has no value.";
                return false;
            }
            position = value.t;
            // Effector q is already the final IKGoal rotation. Do not convert
            // it back through skeleton-root or bind space here.
            rotation = value.q;
            return true;
        }

    }
}
