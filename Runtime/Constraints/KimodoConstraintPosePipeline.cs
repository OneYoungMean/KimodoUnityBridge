using System;
using CharacterAnimationCli.Unity;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoBridge
{
    /// <summary>
    /// One canonical constraint pose path shared by preview and protocol
    /// projection: FK, complete world hips override, then SampleResult IK
    /// targets. Root2D is projected only at its protocol boundary.
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
            boneSample = null;
            muscleSample = null;
            error = string.Empty;

            if (sample == null || cache == null)
            {
                error = "Constraint pose input is null.";
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                    sample.sampleData,
                    frameRate,
                    cache,
                    out boneSample,
                    out _,
                    out error))
            {
                return false;
            }

            if (!KimodoConstraintIkSolver.TryApply(sample, frameRate, cache, out error))
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

    }

    /// <summary>
    /// Humanoid IK job whose targets are copied value data from SampleResult.
    /// No scene Transform or external rig is read by the job.
    /// </summary>
    internal static class KimodoConstraintIkSolver
    {
        private struct SolveJob : IAnimationJob
        {
            public bool solveHips;
            public TransformStreamHandle hipsHandle;
            public Vector3 hipsPosition;
            public Quaternion hipsRotation;
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
                if (solveHips)
                {
                    hipsHandle.SetPosition(stream, hipsPosition);
                    hipsHandle.SetRotation(stream, hipsRotation);
                }
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
                human.SetGoalWeightPosition(goal, enabled ? 1f : 0f);
                human.SetGoalWeightRotation(goal, enabled ? 1f : 0f);
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
            BoneSample input = KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
            if (!KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                    new[] { input },
                    frameRate,
                    out AnimationClip clip,
                    out error))
            {
                return false;
            }

            Vector3 rootPosition = cache.skeletonRoot.position;
            Quaternion rootRotation = cache.skeletonRoot.rotation;
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

                if (!TryBuildJob(sample, cache, out SolveJob job, out bool any, out error) || !any)
                {
                    return string.IsNullOrEmpty(error);
                }

                cache.skeletonRoot.SetPositionAndRotation(rootPosition, rootRotation);
                graph = PlayableGraph.Create("KimodoConstraintIkGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationClipPlayable clipPlayable = AnimationClipPlayable.Create(graph, clip);
                clipPlayable.SetApplyFootIK(false);
                clipPlayable.SetApplyPlayableIK(false);
                AnimationScriptPlayable ikPlayable = AnimationScriptPlayable.Create(
                    graph,
                    job,
                    1);
                graph.Connect(clipPlayable, 0, ikPlayable, 0);
                ikPlayable.SetInputWeight(0, 1f);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                    graph,
                    "KimodoConstraintIkOutput",
                    cache.animator);
                output.SetSourcePlayable(ikPlayable);
                graph.Play();
                graph.Evaluate(0f);
                cache.skeletonRoot.SetPositionAndRotation(rootPosition, rootRotation);
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
                cache.skeletonRoot.SetPositionAndRotation(rootPosition, rootRotation);
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
            if (sample == null || cache == null)
            {
                return true;
            }

            KimodoSampleChannelMask mask = sample.enableMask;
            if (mask?.root2DPosition == true && sample.rootOverride != null)
            {
                Transform hips = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                    cache,
                    HumanBodyBones.Hips);
                if (hips == null || cache.animator == null)
                {
                    error = "Constraint root override requires an Animator Hips transform.";
                    return false;
                }

                job.solveHips = true;
                job.hipsHandle = cache.animator.BindStreamTransform(hips);
                job.hipsPosition = sample.rootOverride.t;
                job.hipsRotation = sample.rootOverride.q.normalized;
                any = true;
            }

            any |= job.solveLeftHand = mask?.leftHandEffector == true;
            any |= job.solveRightHand = mask?.rightHandEffector == true;
            any |= job.solveLeftFoot = mask?.leftFootEffector == true;
            any |= job.solveRightFoot = mask?.rightFootEffector == true;

            if (!TryResolveTarget(sample.effectors?.leftHand, job.solveLeftHand, cache,
                    HumanBodyBones.LeftHand, false, out job.leftHandPosition, out job.leftHandRotation, out error) ||
                !TryResolveTarget(sample.effectors?.rightHand, job.solveRightHand, cache,
                    HumanBodyBones.RightHand, false, out job.rightHandPosition, out job.rightHandRotation, out error) ||
                !TryResolveTarget(sample.effectors?.leftFoot, job.solveLeftFoot, cache,
                    HumanBodyBones.LeftFoot, true, out job.leftFootPosition, out job.leftFootRotation, out error) ||
                !TryResolveTarget(sample.effectors?.rightFoot, job.solveRightFoot, cache,
                    HumanBodyBones.RightFoot, true, out job.rightFootPosition, out job.rightFootRotation, out error))
            {
                return false;
            }
            return true;
        }

        private static bool TryResolveTarget(
            KimodoRigidTransform value,
            bool enabled,
            RetargetSkeleton cache,
            HumanBodyBones bone,
            bool foot,
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
            rotation = KimodoRetargetMarkerSamplingUtility.ResolveEffectorWorldRotation(
                cache,
                bone,
                value.q,
                foot ? 1 : 0);
            return true;
        }
    }
}
