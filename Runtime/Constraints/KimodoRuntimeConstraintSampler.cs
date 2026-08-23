using TimelineInject;
using KimodoUnityBridge;
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

            // Runtime command samples combine the captured body with the
            // explicitly enabled target channel.
            sample.constraintMode = "effector";
            sample.effectors ??= new KimodoConstraintEffectors();
            sample.effectors.leftHand ??= KimodoRigidTransform.Identity;
            sample.effectors.rightHand ??= KimodoRigidTransform.Identity;
            sample.effectors.leftFoot ??= KimodoRigidTransform.Identity;
            sample.effectors.rightFoot ??= KimodoRigidTransform.Identity;
            if (KimodoMarkerSamplingUtility.TryResolveEndEffectorBone(
                    constraintType,
                    out HumanBodyBones bone))
            {
                KimodoUnityBridge.KimodoRigidTransform target = new KimodoUnityBridge.KimodoRigidTransform
                {
                    // Effector positions are protocol world coordinates. They
                    // must not be reconstructed from rootTQ or root-local data.
                    t = targetWorldPosition,
                    q = targetJoint.rotation
                };
                switch (bone)
                {
                    case HumanBodyBones.LeftHand:
                        sample.effectors.leftHand = target;
                        sample.enableMask.leftHand = true;
                        sample.validMask.leftHand = true;
                        break;
                    case HumanBodyBones.RightHand:
                        sample.effectors.rightHand = target;
                        sample.enableMask.rightHand = true;
                        sample.validMask.rightHand = true;
                        break;
                    case HumanBodyBones.LeftFoot:
                        sample.effectors.leftFoot = target;
                        sample.enableMask.leftFoot = true;
                        sample.validMask.leftFoot = true;
                        break;
                    case HumanBodyBones.RightFoot:
                        sample.effectors.rightFoot = target;
                        sample.enableMask.rightFoot = true;
                        sample.validMask.rightFoot = true;
                        break;
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

            // Kimodo generation anchors this target against the terminal
            // FullBody frame 0. Keep it in
            // absolute model space; subtracting NextSegmentRootOrigin here
            // would apply the same translation a second time during generation.
            sample.constraintMode = "root2d";
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;
            sample.rootOverride ??= KimodoUnityBridge.KimodoRigidTransform.Identity;
            Quaternion capturedRootRotation = sample.rootOverride.q;
            sample.rootOverride = new KimodoUnityBridge.KimodoRigidTransform
            {
                t = new Vector3(
                    targetWorldPosition.x,
                    currentWorldPosition.y,
                    targetWorldPosition.y),
                // Keep the complete sampled hips rotation. Root2D's
                // heading projection is applied only by protocol export.
                q = capturedRootRotation
            };
            sample.enableMask.rootHeading = worldHeading.HasValue && sample.enableMask.rootPosition;
            sample.validMask.rootHeading = sample.enableMask.rootHeading;
            if (worldHeading.HasValue)
            {
                if (KimodoConstraintMask.IsActive(sample, "rootposition"))
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

        internal static bool TryCreateRootGoal(
            KimodoRuntimeMotionPlayer player,
            string modelName,
            Vector3 targetWorldPosition,
            Quaternion targetWorldRotation,
            Vector3 currentWorldPosition,
            float sampleTime,
            out KimodoMarkerSampleResult sample,
            out KimodoRigidTransform root2DLoss,
            out string error)
        {
            root2DLoss = null;
            if (!IsFinite(targetWorldPosition) || !IsFinite(targetWorldRotation) ||
                targetWorldRotation.x * targetWorldRotation.x +
                targetWorldRotation.y * targetWorldRotation.y +
                targetWorldRotation.z * targetWorldRotation.z +
                targetWorldRotation.w * targetWorldRotation.w <= 1e-8f)
            {
                sample = null;
                error = "RootGoal position and rotation must be finite, and rotation must be non-zero.";
                return false;
            }

            targetWorldRotation.Normalize();
            Vector3 forward = Vector3.ProjectOnPlane(
                targetWorldRotation * Vector3.forward,
                Vector3.up);
            Quaternion planarRotation = forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
            Vector3 planarPosition = new Vector3(
                targetWorldPosition.x,
                currentWorldPosition.y,
                targetWorldPosition.z);
            Vector3 planarForward = planarRotation * Vector3.forward;
            if (!TryCreateRoot2D(
                    player,
                    modelName,
                    new Vector2(planarPosition.x, planarPosition.z),
                    new Vector2(planarForward.x, planarForward.z),
                    currentWorldPosition,
                    sampleTime,
                    out sample,
                    out error))
            {
                return false;
            }

            Quaternion lossRotation =
                (targetWorldRotation * Quaternion.Inverse(planarRotation)).normalized;
            root2DLoss = new KimodoRigidTransform
            {
                q = lossRotation,
                t = targetWorldPosition - lossRotation * planarPosition
            };
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
                enableMask = new KimodoConstraintMask(),
                validMask = new KimodoConstraintMask()
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
            sample.enableMask.muscle = true;
            sample.enableMask.rootTQ = true;
            sample.enableMask.leftFootTQ = true;
            sample.enableMask.rightFootTQ = true;
            sample.validMask.muscle = true;
            sample.validMask.rootTQ = true;
            sample.validMask.leftFootTQ = true;
            sample.validMask.rightFootTQ = true;
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
