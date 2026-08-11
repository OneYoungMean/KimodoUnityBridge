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

            Vector3 offset = targetWorldPosition - targetJoint.position;
            sample.kimodoRootPosition += offset;
            sample.unityRootPos += offset;
            sample.constraintType = constraintType;
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
            sample.localAxisAngles = new List<Vector3>();
            sample.sampledJointIndices = new List<int>();
            sample.hasRootHeading = worldHeading.HasValue;
            if (worldHeading.HasValue)
            {
                sample.rootHeading = KimodoRoot2DPlanner.ToModelHeading(
                    modelToWorldRotation,
                    worldHeading.Value);
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

            return KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                null,
                player.ConstraintSkeletonRoot,
                modelName,
                sampleTime,
                constraintType,
                null,
                null,
                null,
                out sample,
                out error);
        }
    }
}
