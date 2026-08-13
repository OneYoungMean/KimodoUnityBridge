using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRuntimeConstraintSampler
    {
        internal static bool TryCreateEndEffector(
            KimodoRuntimeMotionPlayer player,
            string modelName,
            string constraintType,
            string jointName,
            Vector3 targetWorldPosition,
            Vector3 currentWorldBodyPosition,
            Quaternion modelToWorldRotation,
            float sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            if (!TryCapture(player, modelName, constraintType, sampleTime, out sample, out error))
            {
                return false;
            }

            Transform targetJoint = KimodoRetargetAvatarUtility.FindTransformByName(
                player.ConstraintSkeletonRoot,
                jointName);
            if (targetJoint == null)
            {
                sample = null;
                error = $"Cannot find joint '{jointName}' under constraint skeleton root.";
                return false;
            }

            sample.constraintType = constraintType;
            sample.mask = KimodoConstraintMask.ForType(constraintType);
            if (sample.characterPose != null &&
                KimodoMarkerSamplingUtility.TryResolveEndEffectorBone(constraintType, out HumanBodyBones bone))
            {
                // The command target lives in the displayed character's world.
                // The profile skeleton lives in model space, so retain its
                // canonical body root and transform the scene-space delta into
                // that space before storing the HumanPose IK goal.
                Vector3 modelGoalPosition = sample.characterPose.root.t * sample.humanScale +
                    Quaternion.Inverse(modelToWorldRotation) *
                    (targetWorldPosition - currentWorldBodyPosition);
                KimodoRetargetHumanoidIkUtility.WorldToBodyRelativeIkGoal(
                    sample.characterPose.root.t,
                    sample.characterPose.root.q,
                    Mathf.Max(1e-6f, sample.humanScale),
                    modelGoalPosition,
                    targetJoint.rotation,
                    out Vector3 goalPosition,
                    out Quaternion goalRotation);
                CharacterAnimationCli.Unity.CharacterPoseTransform goal = bone switch
                {
                    HumanBodyBones.LeftHand => sample.characterPose.hands.left,
                    HumanBodyBones.RightHand => sample.characterPose.hands.right,
                    HumanBodyBones.LeftFoot => sample.characterPose.feet.left,
                    HumanBodyBones.RightFoot => sample.characterPose.feet.right,
                    _ => null
                };
                if (goal != null)
                {
                    goal.t = goalPosition;
                    goal.q = goalRotation;
                }
            }
            return true;
        }

        internal static bool TryCreateRoot2D(
            KimodoRuntimeMotionPlayer player,
            string modelName,
            Vector2 targetWorldPosition,
            Vector2? worldHeading,
            Vector3 currentWorldPosition,
            Quaternion modelToWorldRotation,
            float targetHumanScale,
            float sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            if (!TryCapture(
                    player,
                    modelName,
                    KimodoRuntimeConstraints.Root2DType,
                    sampleTime,
                    out sample,
                    out error))
            {
                return false;
            }

            Vector3 modelOrigin = KimodoMotionModelProfiles.TryGetArdy(modelName, out _)
                ? Vector3.zero
                : player.NextSegmentRootOrigin;
            Vector2 modelTarget = KimodoRoot2DPlanner.ToModelTarget(
                sample.kimodoRootPosition,
                modelOrigin,
                currentWorldPosition,
                modelToWorldRotation,
                new Vector3(targetWorldPosition.x, currentWorldPosition.y, targetWorldPosition.y),
                player.SourceHumanScale,
                targetHumanScale);
            sample.kimodoRootPosition = new Vector3(
                modelTarget.x,
                sample.kimodoRootPosition.y,
                modelTarget.y);
            sample.unityRootPos = new Vector3(
                targetWorldPosition.x,
                sample.unityRootPos.y,
                targetWorldPosition.y);
            sample.constraintType = KimodoRuntimeConstraints.Root2DType;
            sample.mask = KimodoConstraintMask.ForType(KimodoRuntimeConstraints.Root2DType);
            if (sample.characterPose != null)
            {
                sample.characterPose.root.t = new Vector3(
                    modelTarget.x / Mathf.Max(1e-6f, sample.humanScale),
                    sample.characterPose.root.t.y,
                    modelTarget.y / Mathf.Max(1e-6f, sample.humanScale));
            }
            sample.localAxisAngles = new List<Vector3>();
            sample.sampledJointIndices = new List<int>();
            sample.hasRootHeading = worldHeading.HasValue;
            if (worldHeading.HasValue)
            {
                sample.rootHeading = KimodoRoot2DPlanner.ToModelHeading(
                    modelToWorldRotation,
                    worldHeading.Value);
                if (sample.characterPose != null)
                {
                    sample.characterPose.root.q = Quaternion.LookRotation(
                        new Vector3(sample.rootHeading.x, 0f, sample.rootHeading.y),
                        Vector3.up);
                }
            }

            return true;
        }

        private static bool TryCapture(
            KimodoRuntimeMotionPlayer player,
            string modelName,
            string constraintType,
            float sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            if (player == null)
            {
                sample = null;
                error = "Cannot stage a runtime constraint before the driver is initialized.";
                return false;
            }

            if (!player.EnsureConstraintSkeletonReady(modelName, out error))
            {
                sample = null;
                return false;
            }

            if (!KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                null,
                player.ConstraintSkeletonRoot,
                modelName,
                sampleTime,
                constraintType,
                null,
                null,
                null,
                out sample,
                out error))
            {
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    player.ConstraintSkeletonCache,
                    out MuscleSample muscleSample,
                    out error))
            {
                sample = null;
                return false;
            }

            sample.characterPose = CharacterPoseMuscleAdapter.FromMuscleSample(muscleSample);
            sample.humanScale = player.SourceHumanScale;
            return true;
        }
    }
}
