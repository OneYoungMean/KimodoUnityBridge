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
            Vector3 currentWorldPosition,
            Quaternion modelToWorldRotation,
            float targetHumanScale,
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
                    // Convert the public world-space target once into the
                    // neutral model space consumed by the shared pipeline.
                    t = ResolveNeutralTargetPosition(
                        player,
                        targetWorldPosition,
                        currentWorldPosition,
                        modelToWorldRotation,
                        player.ConstraintRetargetSkeleton.humanScale,
                        targetHumanScale),
                    q = KimodoRetargetMarkerSamplingUtility.ResolveEffectorTransportRotation(
                        player.ConstraintRetargetSkeleton,
                        bone,
                        targetJoint.rotation)
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

            sample.constraintMode = "root2d";
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;
            sample.rootOverride ??= KimodoUnityBridge.KimodoRigidTransform.Identity;
            Vector3 neutralRootPosition = ResolveNeutralRootPosition(
                player,
                sample,
                out Quaternion capturedRootRotation);
            sample.rootOverride = new KimodoUnityBridge.KimodoRigidTransform
            {
                t = neutralRootPosition + ResolveNeutralWorldDelta(
                    new Vector3(
                        targetWorldPosition.x,
                        currentWorldPosition.y,
                        targetWorldPosition.y),
                    currentWorldPosition,
                    modelToWorldRotation,
                    player.ConstraintRetargetSkeleton.humanScale,
                    targetHumanScale),
                // Keep the complete sampled hips rotation when no heading is
                // authored; Root2D heading changes only its planar yaw.
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
                    Vector3 modelHeading = Quaternion.Inverse(
                        NormalizeModelRotation(modelToWorldRotation)) *
                        new Vector3(worldHeading.Value.x, 0f, worldHeading.Value.y);
                    modelHeading.y = 0f;
                    if (modelHeading.sqrMagnitude < 1e-6f)
                    {
                        modelHeading = Vector3.forward;
                    }
                    Quaternion desiredYaw = Quaternion.LookRotation(
                        modelHeading.normalized,
                        Vector3.up);
                    sample.rootOverride.q =
                        (desiredYaw * Quaternion.Inverse(currentYaw) * sample.rootOverride.q).normalized;
                }
            }

            return true;
        }

        private static Vector3 ResolveNeutralTargetPosition(
            KimodoRuntimeMotionPlayer player,
            Vector3 targetWorldPosition,
            Vector3 currentWorldPosition,
            Quaternion modelToWorldRotation,
            float sourceHumanScale,
            float targetHumanScale)
        {
            return ResolveNeutralRootPosition(
                       player,
                       null,
                       out _) +
                ResolveNeutralWorldDelta(
                    targetWorldPosition,
                    currentWorldPosition,
                    modelToWorldRotation,
                    sourceHumanScale,
                    targetHumanScale);
        }

        private static Vector3 ResolveNeutralRootPosition(
            KimodoRuntimeMotionPlayer player,
            KimodoMarkerSampleResult sample,
            out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (player?.ConstraintRetargetSkeleton != null &&
                player.ConstraintRetargetSkeleton.GetBonePose(
                    HumanBodyBones.Hips,
                    out Vector3 position,
                    out rotation))
            {
                return position;
            }

            if (sample?.sampleData != null && sample.sampleData.IsValid)
            {
                sample.sampleData.GetRoot(out Vector3 rootPosition, out rotation);
                return rootPosition;
            }

            return Vector3.zero;
        }

        private static Vector3 ResolveNeutralWorldDelta(
            Vector3 targetWorldPosition,
            Vector3 currentWorldPosition,
            Quaternion modelToWorldRotation,
            float sourceHumanScale,
            float targetHumanScale)
        {
            Vector3 worldDelta = targetWorldPosition - currentWorldPosition;
            float scale = Mathf.Max(1e-6f, sourceHumanScale) /
                Mathf.Max(1e-6f, targetHumanScale);
            return Quaternion.Inverse(NormalizeModelRotation(modelToWorldRotation)) *
                (worldDelta * scale);
        }

        private static Quaternion NormalizeModelRotation(Quaternion rotation)
        {
            if (rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w <= 1e-8f)
            {
                return Quaternion.identity;
            }

            rotation.Normalize();
            return rotation;
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
            // including body-relative footTQ. Keep it intact so scene-space
            // effector values cannot overwrite transport channels.
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
