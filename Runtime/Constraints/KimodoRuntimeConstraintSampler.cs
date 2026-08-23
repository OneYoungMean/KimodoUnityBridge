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

            sample.constraintMode = "constraint";
            sample.effectors ??= new KimodoConstraintEffectors();
            sample.effectors.leftHand ??= KimodoRigidTransform.Identity;
            sample.effectors.rightHand ??= KimodoRigidTransform.Identity;
            sample.effectors.leftFoot ??= KimodoRigidTransform.Identity;
            sample.effectors.rightFoot ??= KimodoRigidTransform.Identity;
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
                    case HumanBodyBones.LeftHand: sample.effectors.leftHand = target; break;
                    case HumanBodyBones.RightHand: sample.effectors.rightHand = target; break;
                    case HumanBodyBones.LeftFoot: sample.effectors.leftFoot = target; break;
                    case HumanBodyBones.RightFoot: sample.effectors.rightFoot = target; break;
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
            sample.constraintMode = "constraint";
            if (sample.enableMask?.root2DPosition == true)
            {
                Quaternion capturedRootRotation = sample.rootOverride != null
                    ? sample.rootOverride.q
                    : Quaternion.identity;
                sample.rootOverride = new CharacterAnimationCli.Unity.KimodoRigidTransform
                {
                    t = new Vector3(
                        targetWorldPosition.x,
                        currentWorldPosition.y,
                        targetWorldPosition.y),
                    // Keep the complete sampled hips rotation. Root2D's
                    // heading projection is applied only by protocol export.
                    q = capturedRootRotation
                };
                sample.enableMask.root2DPosition = true;
            }
            sample.enableMask.root2DHeading = worldHeading.HasValue && sample.enableMask.root2DPosition;
            if (worldHeading.HasValue)
            {
                if (sample.enableMask?.root2DPosition == true)
                {
                    Vector3 currentForward = sample.rootOverride.q * Vector3.forward;
                    currentForward.y = 0f;
                    if (currentForward.sqrMagnitude < 1e-6f)
                    {
                        currentForward = Vector3.forward;
                    }
                    Quaternion currentYaw = Quaternion.LookRotation(
                        currentForward.normalized,
                        Vector3.up);
                    Quaternion desiredYaw = Quaternion.LookRotation(
                        new Vector3(worldHeading.Value.x, 0f, worldHeading.Value.y),
                        Vector3.up);
                    sample.rootOverride.q =
                        (desiredYaw * Quaternion.Inverse(currentYaw) * sample.rootOverride.q).normalized;
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
                constraintMode = "constraint",
                sampleTime = sampleTime,
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

            // The evaluated sampler already owns the canonical 70D payload,
            // including body-relative footTQ. Do not round-trip through
            // CharacterPose, which exposes scene/world foot values and would
            // overwrite the transport channels.
            sample.sampleData = muscleSample.Clone();
            sample.enableMask.muscle49 = true;
            sample.enableMask.rootTQ = true;
            sample.enableMask.leftFootTQ = true;
            sample.enableMask.rightFootTQ = true;
            return true;
        }
    }
}
