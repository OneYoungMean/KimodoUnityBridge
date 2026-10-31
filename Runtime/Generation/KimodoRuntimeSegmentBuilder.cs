using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRuntimeSegmentBuilder
    {
        internal static async Task<KimodoRuntimeGeneratedSegment> BuildAsync(
            KimodoGenerationResultDto bridgeResult,
            string modelName,
            string prompt,
            int segmentIndex,
            bool isArdy,
            KimodoSegmentTrimTrailSettings trimTrail,
            bool allowPartialJoints,
            CancellationToken token)
        {
            KimodoRawMotionMetadata metadata = isArdy
                ? ReadArdyMetadata(bridgeResult.motionData)
                : await ReadKimodoMetadataAsync(
                    bridgeResult,
                    modelName,
                    allowPartialJoints,
                    token);

            int effectiveLastFrameIndex = isArdy
                ? metadata.Motion.FrameCount - 1
                : KimodoRuntimeSegmentAnalysisUtility.ResolveEffectiveLastFrameIndex(
                    metadata.Motion,
                    trimTrail);
            if (!metadata.Motion.TryReadUnityRootPosition(
                    effectiveLastFrameIndex,
                    out Vector3 effectiveLastRootPosition))
            {
                throw new InvalidOperationException(
                    $"Failed to read effective tail root position for frame {effectiveLastFrameIndex}.");
            }

            KimodoConstraintInternalData terminalConstraint = null;
            if (!isArdy && !KimodoRawMotionConstraintBuilder.TryBuildFullBodyFrame(
                    metadata.Motion,
                    modelName,
                    effectiveLastFrameIndex,
                    out terminalConstraint,
                    out string terminalError))
            {
                throw new InvalidOperationException(terminalError);
            }

            return new KimodoRuntimeGeneratedSegment
            {
                Index = segmentIndex,
                PromptText = prompt,
                Motion = metadata.Motion,
                TerminalConstraint = terminalConstraint,
                FirstRootPosition = metadata.FirstRootPosition,
                LastRootPosition = effectiveLastRootPosition,
                WorldAccumulatedOffset = Vector3.zero,
                EffectiveLastFrameIndex = effectiveLastFrameIndex,
                EffectiveLastFrameTimeSeconds = metadata.Motion.FrameRate > 0f
                    ? (isArdy ? metadata.Motion.FrameCount : effectiveLastFrameIndex) / metadata.Motion.FrameRate
                    : metadata.Motion.LastFrameTimeSeconds,
                MotionBytes = bridgeResult?.motionBytes,
                MotionRepFingerprint = bridgeResult?.motionRepFingerprint ?? string.Empty,
                ResolvedSeed = bridgeResult?.resolvedSeed,
                UseRawRootPosition = isArdy
            };
        }

        internal static void ValidateArdyResult(
            KimodoGenerationResultDto result,
            KimodoMotionModelProfile profile,
            int requestedSeed)
        {
            if (result == null ||
                !string.Equals(result.motionFormat, "kmb_v1", StringComparison.OrdinalIgnoreCase) ||
                result.endFrameExclusive < result.startFrame)
            {
                throw new InvalidOperationException("ARDY result metadata is invalid.");
            }

            int expectedFrames = result.endFrameExclusive - result.startFrame;
            if (expectedFrames == 0)
            {
                if (result.motionData != null || result.motionBytes == null || result.motionBytes.Length != 0)
                {
                    throw new InvalidOperationException("Empty ARDY result contains unexpected KMB data.");
                }
            }
            else if (result.motionData == null ||
                result.motionBytes == null ||
                result.motionBytes.Length == 0 ||
                result.motionData.FrameCount != expectedFrames ||
                result.motionData.JointCount != profile.JointCount ||
                Mathf.Abs(result.motionData.FrameRate - profile.SourceFps) > 1e-4f)
            {
                throw new InvalidOperationException("ARDY KMB frame count, FPS, or rig metadata does not match its response.");
            }

            if (!string.Equals(
                    result.motionRepFingerprint,
                    profile.MotionRepFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ARDY result motion representation fingerprint mismatch.");
            }

            if (!result.resolvedSeed.HasValue || result.resolvedSeed.Value != requestedSeed)
            {
                throw new InvalidOperationException("ARDY result resolved_seed does not match the requested seed.");
            }
        }

        private static KimodoRawMotionMetadata ReadArdyMetadata(KimodoRawMotionData motion)
        {
            if (motion == null ||
                !motion.TryReadUnityRootPosition(0, out Vector3 firstRootPosition) ||
                !motion.TryReadUnityRootPosition(motion.FrameCount - 1, out Vector3 lastRootPosition))
            {
                throw new InvalidOperationException("Failed to read ARDY KMB root positions.");
            }

            return new KimodoRawMotionMetadata(
                motion,
                firstRootPosition,
                lastRootPosition);
        }

        private static Task<KimodoRawMotionMetadata> ReadKimodoMetadataAsync(
            KimodoGenerationResultDto bridgeResult,
            string modelName,
            bool allowPartialJoints,
            CancellationToken token) =>
            Task.Run(() =>
            {
                if (!KimodoRawMotionUtility.TryAnalyzeGenerationResult(
                        bridgeResult,
                        modelName,
                        out KimodoRawMotionMetadata metadata,
                        out string error,
                        KimodoRuntimeConstraints.FullBodyType,
                        0.0,
                        allowPartialJoints))
                {
                    throw new InvalidOperationException(error);
                }

                return metadata;
            }, token);

    }
}
