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
    /// projection. Constraint targets are world-space values; the single
    /// playable graph evaluates the authored MuscleSample, root override and
    /// IK together. Timeline generation converts the resulting world pose
    /// back to track space before writing the character clip.
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
            Vector3 unusedTrackPosition,
            Quaternion unusedTrackRotation,
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

            if (!KimodoConstraintIkSolver.TryApply(pipelineSample, frameRate, cache, out error))
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
            public bool applyRoot;
            public bool rootAfterEffectors;
            public bool rootPlanar;
            public bool rootHeading;
            public Vector3 rootPosition;
            public Quaternion rootRotation;
            public float humanScale;

            public void ProcessRootMotion(AnimationStream stream)
            {
                if (applyRoot && !rootAfterEffectors && stream.isHumanStream)
                {
                    ApplyRoot(stream.AsHuman());
                }
            }

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

                if (applyRoot && rootAfterEffectors)
                {
                    ApplyRoot(human);
                }
            }

            private void ApplyRoot(AnimationHumanStream human)
            {
                Vector3 position = rootPosition;
                Quaternion rotation = rootRotation;
                float scale = Mathf.Max(1e-6f, humanScale);
                if (rootPlanar)
                {
                    Vector3 currentWorldPosition = human.bodyPosition * scale;
                    position = KimodoMotionMath.ApplyPlanarPosition(currentWorldPosition, rootPosition) / scale;
                    if (rootHeading)
                    {
                        rotation = KimodoMotionMath.ApplyPlanarHeading(human.bodyRotation, rootRotation);
                    }
                    else
                    {
                        rotation = human.bodyRotation;
                    }
                }
                else
                {
                    position /= scale;
                    if (!rootHeading)
                    {
                        rotation = human.bodyRotation;
                    }
                }

                human.bodyPosition = position;
                human.bodyRotation = rotation.normalized;
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
            if (!TryBuildJob(sample, cache, out SolveJob job, out bool any, out error) || !any)
            {
                return string.IsNullOrEmpty(error);
            }

            if (sample?.sampleData == null || !sample.sampleData.IsValid)
            {
                error = "Constraint IK requires a valid MuscleSample payload.";
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                    new[] { sample.sampleData },
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
            if (sample == null || cache == null)
            {
                return true;
            }

            any |= job.solveLeftHand = KimodoConstraintMask.IsActive(sample, "lefthand");
            any |= job.solveRightHand = KimodoConstraintMask.IsActive(sample, "righthand");
            any |= job.solveLeftFoot = KimodoConstraintMask.IsActive(sample, "leftfoot");
            any |= job.solveRightFoot = KimodoConstraintMask.IsActive(sample, "rightfoot");

            if (KimodoConstraintMask.IsActive(sample, "rootposition") &&
                sample.rootOverride != null)
            {
                job.applyRoot = true;
                job.rootAfterEffectors = sample.rootOverrideAfterEffectors;
                job.rootPlanar = KimodoConstraintInternal.NormalizeMode(sample.constraintMode) == "root2d" ||
                    KimodoConstraintInternal.NormalizeMode(sample.constraintMode) == "mix";
                job.rootHeading = KimodoConstraintMask.IsActive(sample, "rootheading");
                job.rootPosition = sample.rootOverride.t;
                job.rootRotation = sample.rootOverride.q.normalized;
                job.humanScale = cache.humanScale;
                any = true;
            }

            if (!TryResolveTarget(sample.effectors?.leftHand, job.solveLeftHand,
                    HumanBodyBones.LeftHand, cache, out job.leftHandPosition, out job.leftHandRotation, out error) ||
                !TryResolveTarget(sample.effectors?.rightHand, job.solveRightHand,
                    HumanBodyBones.RightHand, cache, out job.rightHandPosition, out job.rightHandRotation, out error) ||
                !TryResolveTarget(sample.effectors?.leftFoot, job.solveLeftFoot,
                    HumanBodyBones.LeftFoot, cache, out job.leftFootPosition, out job.leftFootRotation, out error) ||
                !TryResolveTarget(sample.effectors?.rightFoot, job.solveRightFoot,
                    HumanBodyBones.RightFoot, cache, out job.rightFootPosition, out job.rightFootRotation, out error))
            {
                return false;
            }
            return true;
        }

        private static bool TryResolveTarget(
            KimodoRigidTransform value,
            bool enabled,
            HumanBodyBones bone,
            RetargetSkeleton cache,
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
            if (bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand)
            {
                // Effector q is the bind-relative delta expected directly by
                // the Humanoid IK goal; it is not a world or track rotation.
                rotation = value.q.normalized;
            }
            else
            {
                // Foot effectors retain their existing transport protocol.
                rotation = value.q;
            }
            return true;
        }

    }
}
