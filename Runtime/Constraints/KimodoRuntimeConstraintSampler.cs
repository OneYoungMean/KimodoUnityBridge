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
            if (KimodoMarkerSamplingUtility.TryResolveEndEffectorBone(
                    constraintType,
                    out HumanBodyBones bone))
            {
                CharacterAnimationCli.Unity.KimodoRigidTransform target = new CharacterAnimationCli.Unity.KimodoRigidTransform
                {
                    // Effector positions are protocol world coordinates. They
                    // must not be reconstructed from rootTQ or root-local data.
                    t = targetWorldPosition,
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
            sample.constraintType = "constraint";
            sample.mask = KimodoConstraintMask.ForType(KimodoRuntimeConstraints.Root2DType);
            if (sample.enableMask?.root2DPosition == true)
            {
                sample.root2DOverride = new CharacterAnimationCli.Unity.KimodoRigidTransform
                {
                    t = new Vector3(
                        targetWorldPosition.x,
                        currentWorldPosition.y,
                        targetWorldPosition.y),
                    q = Quaternion.identity
                };
                sample.enableMask.root2DPosition = true;
            }
            sample.enableMask.root2DHeading = worldHeading.HasValue && sample.enableMask.root2DPosition;
            if (worldHeading.HasValue)
            {
                if (sample.enableMask?.root2DPosition == true)
                {
                    sample.root2DOverride.q = Quaternion.LookRotation(
                        new Vector3(worldHeading.Value.x, 0f, worldHeading.Value.y),
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

            sample = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                sampleTime = sampleTime,
                mask = KimodoConstraintMask.ForType(constraintType),
                enableMask = new KimodoSampleChannelMask()
            };

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    player.ConstraintRetargetSkeleton,
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
                    player.ConstraintRetargetSkeleton),
                out _);
            return true;
        }
    }
}
