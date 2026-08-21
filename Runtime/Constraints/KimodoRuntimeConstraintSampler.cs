using TimelineInject;
using CharacterAnimationCli.Unity;
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
            sample.effectors ??= new KimodoConstraintEffectors();
            sample.effectors.hands ??= new CharacterPoseSides();
            sample.effectors.feet ??= new CharacterPoseSides();
            if (KimodoSampleResultPoseUtility.TryDecode(
                    sample,
                    out CharacterPose referencePose,
                    out _) &&
                KimodoMarkerSamplingUtility.TryResolveEndEffectorBone(constraintType, out HumanBodyBones bone))
            {
                float humanScale = player.SourceHumanScale;
                Vector3 modelGoalPosition = referencePose.root.t * humanScale +
                    Quaternion.Inverse(modelToWorldRotation) *
                    (targetWorldPosition - currentWorldBodyPosition);
                CharacterAnimationCli.Unity.CharacterPoseTransform target = new CharacterAnimationCli.Unity.CharacterPoseTransform
                {
                    t = modelGoalPosition,
                    q = targetJoint.rotation
                };
                switch (bone)
                {
                    case HumanBodyBones.LeftHand: sample.effectors.hands.left = target; break;
                    case HumanBodyBones.RightHand: sample.effectors.hands.right = target; break;
                    case HumanBodyBones.LeftFoot: sample.effectors.feet.left = target; break;
                    case HumanBodyBones.RightFoot: sample.effectors.feet.right = target; break;
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

            // Kimodo generation normalizes constraints to the earliest anchor
            // (normally the overlap FullBody frame 0). Keep this target in
            // absolute model space; subtracting NextSegmentRootOrigin here
            // would apply the same translation a second time during generation.
            Vector2 modelTarget = KimodoRoot2DPlanner.ToModelTarget(
                KimodoSampleResultPoseUtility.TryDecode(sample, out CharacterPose rootPose, out _)
                    ? new Vector3(rootPose.root.t.x, 0f, rootPose.root.t.z)
                    : Vector3.zero,
                Vector3.zero,
                currentWorldPosition,
                modelToWorldRotation,
                new Vector3(targetWorldPosition.x, currentWorldPosition.y, targetWorldPosition.y),
                player.SourceHumanScale,
                targetHumanScale);
            sample.constraintType = "constraint";
            sample.mask = KimodoConstraintMask.ForType(KimodoRuntimeConstraints.Root2DType);
            if (KimodoSampleResultPoseUtility.TryDecode(sample, out _, out _))
            {
                sample.root2DOverride = new CharacterAnimationCli.Unity.CharacterPoseTransform
                {
                    t = new Vector3(
                        modelTarget.x / player.SourceHumanScale,
                        0f,
                        modelTarget.y / player.SourceHumanScale),
                    q = Quaternion.identity
                };
                sample.validMask.root2DPosition = true;
            }
            sample.validMask.root2DHeading = worldHeading.HasValue && sample.validMask.root2DPosition;
            if (worldHeading.HasValue)
            {
                Vector2 modelHeading = KimodoRoot2DPlanner.ToModelHeading(
                    modelToWorldRotation,
                    worldHeading.Value);
                if (sample.root2DOverride != null)
                {
                    sample.root2DOverride.q = Quaternion.LookRotation(
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

            KimodoSampleResultPoseUtility.TryEncode(
                sample,
                CharacterPoseMuscleAdapter.FromMuscleSample(
                    muscleSample,
                    player.ConstraintSkeletonCache),
                out _);
            return true;
        }
    }
}
