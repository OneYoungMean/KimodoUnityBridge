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

            sample.constraintType = "constraint";
            sample.mask = KimodoConstraintMask.ForType(constraintType);
            if (sample.characterPose != null &&
                KimodoMarkerSamplingUtility.TryResolveEndEffectorBone(constraintType, out HumanBodyBones bone))
            {
                // The command target lives in the displayed character's world.
                // The profile skeleton lives in model space, so retain its
                // canonical body root and transform the scene-space delta into
                // that space before storing the HumanPose IK goal.
                float humanScale = player.SourceHumanScale;
                Vector3 modelGoalPosition = sample.characterPose.root.t * humanScale +
                    Quaternion.Inverse(modelToWorldRotation) *
                    (targetWorldPosition - currentWorldBodyPosition);
                KimodoRetargetHumanoidIkUtility.WorldToBodyRelativeIkGoal(
                    sample.characterPose.root.t,
                    sample.characterPose.root.q,
                    humanScale,
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
                sample.characterPose != null ? new Vector3(sample.characterPose.root.t.x, 0f, sample.characterPose.root.t.z) : Vector3.zero,
                modelOrigin,
                currentWorldPosition,
                modelToWorldRotation,
                new Vector3(targetWorldPosition.x, currentWorldPosition.y, targetWorldPosition.y),
                player.SourceHumanScale,
                targetHumanScale);
            sample.constraintType = "constraint";
            sample.mask = KimodoConstraintMask.ForType(KimodoRuntimeConstraints.Root2DType);
            if (sample.characterPose != null)
            {
                sample.characterPose.root.t = new Vector3(
                    modelTarget.x / player.SourceHumanScale,
                    sample.characterPose.root.t.y,
                    modelTarget.y / player.SourceHumanScale);
            }
            sample.hasRootHeading = worldHeading.HasValue;
            if (worldHeading.HasValue)
            {
                Vector2 modelHeading = KimodoRoot2DPlanner.ToModelHeading(
                    modelToWorldRotation,
                    worldHeading.Value);
                if (sample.characterPose != null)
                {
                    sample.characterPose.root.q = Quaternion.LookRotation(
                        new Vector3(modelHeading.x, 0f, modelHeading.y),
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
            return true;
        }
    }
}
