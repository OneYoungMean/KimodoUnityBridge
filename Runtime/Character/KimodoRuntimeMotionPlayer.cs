using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeMotionPlayer
    {
        private KimodoRawMotionPlaybackBinding sourceBinding;
        private RetargetSkeleton sourceCache;
        private string sourceCacheModelName;
        private Transform sourceRootJoint;
        private Transform sourceHipsBone;
        private Vector3 currentSegmentRootBaseline;
        private Vector3 lastCompletedWorldOffset;
        private KimodoRuntimeGeneratedSegment currentSegment;
        private KimodoRuntimeGeneratedSegment nextSegment;
        private float? nextSegmentSwitchTimeSeconds;
        private KimodoRuntimeGeneratedSegment ardySegment;
        private KimodoArdyMotionBuffer ardyBuffer;
        private readonly KimodoRuntimeHumanoidRetargeter retargeter = new KimodoRuntimeHumanoidRetargeter();
        private float timeSeconds;
        private bool playing;
        private bool hasCompletedSegment;

        public bool HasCurrentSegment => currentSegment != null;
        public float CurrentSegmentTimeSeconds => timeSeconds;
        public float CurrentSegmentDurationSeconds => currentSegment != null
            ? Mathf.Max(0f, currentSegment.EffectiveLastFrameTimeSeconds)
            : 0f;
        public string CurrentPromptText => currentSegment != null ? currentSegment.PromptText : string.Empty;
        public Vector3 CurrentRootPosition => sourceRootJoint != null ? sourceRootJoint.position : Vector3.zero;
        public Vector3 NextSegmentRootOrigin => currentSegment != null
            ? currentSegment.WorldAccumulatedOffset + new Vector3(
                currentSegment.LastRootPosition.x - currentSegment.FirstRootPosition.x,
                0f,
                currentSegment.LastRootPosition.z - currentSegment.FirstRootPosition.z)
            : lastCompletedWorldOffset;
        public Transform ConstraintSkeletonRoot => sourceCache != null ? sourceCache.skeletonRoot : null;
        internal Transform DebugProfileSkeletonRoot => sourceCache != null ? sourceCache.skeletonRoot : null;
        internal RetargetSkeleton ConstraintRetargetSkeleton => sourceCache;
        public double PlaybackTimeAsDouble => timeSeconds;
        public float BufferedDurationSeconds
        {
            get
            {
                if (ardyBuffer != null)
                {
                    return Mathf.Max(0f, ardyBuffer.EndTimeSeconds - timeSeconds);
                }
                float total = currentSegment != null
                    ? Mathf.Max(0f, currentSegment.EffectiveLastFrameTimeSeconds - timeSeconds)
                    : 0f;
                if (nextSegment != null)
                {
                    total += Mathf.Max(0f, nextSegment.EffectiveLastFrameTimeSeconds);
                }
                return total;
            }
        }

        public bool HasNextSegment => nextSegment != null;

        public bool TrySetNextSegment(KimodoRuntimeGeneratedSegment segment, bool verboseLogging) =>
            TrySetNextSegment(segment, null, verboseLogging);

        public bool TrySetNextSegment(
            KimodoRuntimeGeneratedSegment segment,
            float? switchTimeSeconds,
            bool verboseLogging)
        {
            if (segment == null || nextSegment != null)
            {
                return false;
            }

            nextSegment = segment;
            nextSegmentSwitchTimeSeconds = switchTimeSeconds;
            if (verboseLogging)
            {
                string switchDescription = switchTimeSeconds.HasValue
                    ? $" at {switchTimeSeconds.Value:0.###}s"
                    : string.Empty;
                Debug.Log($"[KimodoRuntimeMotionDriver] Set next segment {segment.Index}{switchDescription}.");
            }
            return true;
        }

        public bool ReplaceArdy(
            KimodoRuntimeGeneratedSegment segment,
            int startFrame,
            bool verboseLogging,
            out string error)
        {
            error = string.Empty;
            if (segment?.Motion == null)
            {
                error = "ARDY KMB segment is empty.";
                return false;
            }

            bool createdBuffer = ardyBuffer == null;
            if (createdBuffer)
            {
                if (startFrame != 0)
                {
                    error = $"First ARDY KMB segment must start at frame 0, got {startFrame}.";
                    return false;
                }
                ardyBuffer = new KimodoArdyMotionBuffer(segment.Motion);
                ardySegment = segment;
            }

            int protectedFrameExclusive = playing && ReferenceEquals(currentSegment, ardySegment)
                ? ardyBuffer.ResolveProtectedFrameExclusive(timeSeconds)
                : ardyBuffer.StartFrame;
            if (!ardyBuffer.TryReplace(
                    segment.Motion,
                    startFrame,
                    protectedFrameExclusive,
                    out int writtenStartFrame,
                    out error))
            {
                if (createdBuffer)
                {
                    ardyBuffer.Dispose();
                    ardyBuffer = null;
                    ardySegment = null;
                }
                return false;
            }
            if (ardySegment != null)
            {
                ardySegment.PromptText = segment.PromptText;
                ardySegment.LastRootPosition = segment.LastRootPosition;
                ardySegment.EffectiveLastFrameIndex = ardyBuffer.EndFrameExclusive - 1;
                ardySegment.EffectiveLastFrameTimeSeconds = ardyBuffer.EndTimeSeconds;
                ardySegment.MotionRepFingerprint = segment.MotionRepFingerprint;
                ardySegment.ResolvedSeed = segment.ResolvedSeed;
            }
            if (verboseLogging)
            {
                Debug.Log(
                    $"[KimodoRuntimeMotionDriver] ARDY replace [{startFrame},{startFrame + segment.Motion.FrameCount}) " +
                    $"wrote [{writtenStartFrame},{ardyBuffer.EndFrameExclusive}); protectedBefore={protectedFrameExclusive}.");
            }
            return true;
        }

        public void ClearNextSegment()
        {
            nextSegment = null;
            nextSegmentSwitchTimeSeconds = null;
        }

        public bool TryBuildInterruptionConstraint(
            float predictedTimeSeconds,
            string modelName,
            out KimodoConstraintInternalData constraint,
            out float resolvedTimeSeconds)
        {
            constraint = null;
            resolvedTimeSeconds = 0f;
            if (!playing || currentSegment?.Motion == null || currentSegment.UseRawRootPosition ||
                currentSegment.EffectiveLastFrameIndex <= 0)
            {
                return false;
            }

            float duration = CurrentSegmentDurationSeconds;
            float frameDuration = currentSegment.Motion.FrameRate > 0f
                ? 1f / currentSegment.Motion.FrameRate
                : 0f;
            if (predictedTimeSeconds <= timeSeconds || predictedTimeSeconds >= duration - frameDuration)
            {
                return false;
            }

            int frameIndex = KimodoFrameTimeUtility.SecondsToFrameIndex(
                predictedTimeSeconds,
                currentSegment.Motion.FrameRate);
            frameIndex = Mathf.Clamp(frameIndex, 0, currentSegment.EffectiveLastFrameIndex - 1);
            if (!KimodoRawMotionConstraintBuilder.TryBuildFullBodyFrame(
                    currentSegment.Motion,
                    modelName,
                    frameIndex,
                    out constraint,
                    out _))
            {
                constraint = null;
                return false;
            }

            resolvedTimeSeconds = currentSegment.Motion.FrameRate > 0f
                ? frameIndex / currentSegment.Motion.FrameRate
                : predictedTimeSeconds;
            constraint.sampleTime = 0.0;
            return true;
        }

        public void ResetCompletionState()
        {
            lastCompletedWorldOffset = Vector3.zero;
            hasCompletedSegment = false;
        }

        public void Update(
            float deltaTime,
            string modelName,
            IReadOnlyList<Animator> targetAnimators,
            bool allowPartialJoints,
            bool verboseLogging,
            out KimodoRuntimeGeneratedSegment startedSegment,
            out KimodoRuntimeGeneratedSegment completedSegment,
            out string error)
        {
            startedSegment = null;
            completedSegment = null;
            error = string.Empty;
            if (playing && sourceBinding != null)
            {
                AdvanceCurrentMotion(deltaTime, out completedSegment, out error);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return;
                }
            }

            if (playing && nextSegment != null && nextSegmentSwitchTimeSeconds.HasValue &&
                timeSeconds >= nextSegmentSwitchTimeSeconds.Value)
            {
                completedSegment = MarkCurrentSegmentCompleted(timeSeconds);
                StopActiveMotion();
            }

            if (!playing && ardyBuffer != null && ardySegment != null)
            {
                if (!Play(
                        ardySegment,
                        modelName,
                        targetAnimators,
                        allowPartialJoints,
                        out error,
                        verboseLogging))
                {
                    return;
                }
                startedSegment = ardySegment;
                return;
            }

            if (!playing && TakeNextSegment(out KimodoRuntimeGeneratedSegment next))
            {
                if (verboseLogging)
                {
                    Debug.Log($"[KimodoRuntimeMotionDriver] Attempting to play next segment {next.Index}.");
                }

                if (!Play(
                        next,
                        modelName,
                        targetAnimators,
                        allowPartialJoints,
                        out error,
                        verboseLogging))
                {
                    return;
                }

                startedSegment = next;
            }
        }

        public void ApplyLateRetargetCorrection()
        {
            if (!playing)
            {
                return;
            }

            retargeter.ApplyLateCorrection(sourceHipsBone);
        }

        public void Stop()
        {
            StopActiveMotion();
            ardyBuffer?.Dispose();
            ardyBuffer = null;
            ardySegment = null;
            DisposeRetargetCache();
        }

        public bool EnsureConstraintSkeletonReady(string modelName, out string error)
        {
            error = string.Empty;
            if (sourceCache != null && string.Equals(sourceCacheModelName, modelName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar sourceAvatar, out error))
            {
                return false;
            }

            DisposeSourceRetargetCache();
            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    sourceAvatar,
                    "KimodoRuntimeMotionDriver_SourceConstraint",
                    out sourceCache,
                    out error))
            {
                return false;
            }

            sourceCacheModelName = modelName;
            return true;
        }

        private bool Play(
            KimodoRuntimeGeneratedSegment segment,
            string modelName,
            IReadOnlyList<Animator> targetAnimators,
            bool allowPartialJoints,
            out string error,
            bool verboseLogging)
        {
            StopActiveMotion();
            if (!TryCreateDirectRetargetBinding(
                    segment.Motion,
                    modelName,
                    targetAnimators,
                    allowPartialJoints,
                    out error))
            {
                if (verboseLogging)
                {
                    Debug.LogWarning($"[KimodoRuntimeMotionDriver] Play segment {segment?.Index ?? -1} failed while creating retarget binding: {error}");
                }

                StopActiveMotion();
                return false;
            }

            currentSegment = segment;
            bool isArdy = segment.UseRawRootPosition && ardyBuffer != null && ReferenceEquals(segment, ardySegment);
            currentSegment.WorldAccumulatedOffset = lastCompletedWorldOffset;
            // Keep ordinary segment joins continuous in Y as well as X/Z.
            if (!isArdy && !hasCompletedSegment)
            {
                currentSegment.WorldAccumulatedOffset.y = segment.FirstRootPosition.y;
            }
            currentSegmentRootBaseline = segment.FirstRootPosition;
            retargeter.ResetAnchors();
            timeSeconds = isArdy ? ardyBuffer.StartFrame / ardyBuffer.FrameRate : 0f;
            if (isArdy ? !TryApplyArdyTime(timeSeconds, out error) : !TryApplyFrame(0, out error))
            {
                if (verboseLogging)
                {
                    Debug.LogWarning($"[KimodoRuntimeMotionDriver] Play segment {segment?.Index ?? -1} failed while applying frame 0: {error}");
                }

                StopActiveMotion();
                return false;
            }

            playing = true;
            return true;
        }

        private void AdvanceCurrentMotion(float deltaTime, out KimodoRuntimeGeneratedSegment completedSegment, out string error)
        {
            completedSegment = null;
            error = string.Empty;
            if (!playing || sourceBinding == null)
            {
                return;
            }

            timeSeconds += Mathf.Max(0f, deltaTime);
            bool reachedEnd = false;
            float segmentEndTime = ardyBuffer != null
                ? ardyBuffer.EndTimeSeconds
                : currentSegment != null
                ? Mathf.Max(0f, currentSegment.EffectiveLastFrameTimeSeconds)
                : (sourceBinding.motion != null ? sourceBinding.motion.LastFrameTimeSeconds : 0f);
            if (sourceBinding.motion != null && timeSeconds >= segmentEndTime)
            {
                timeSeconds = segmentEndTime;
                reachedEnd = true;
            }

            if (ardyBuffer != null
                ? !TryApplyArdyTime(timeSeconds, out error)
                : !TryApplyTime(timeSeconds, out error))
            {
                StopActiveMotion();
                return;
            }

            if (reachedEnd)
            {
                if (ardyBuffer != null)
                {
                    return;
                }
                completedSegment = MarkCurrentSegmentCompleted();
                StopActiveMotion();
            }
        }

        private bool TakeNextSegment(out KimodoRuntimeGeneratedSegment segment)
        {
            if (nextSegment == null)
            {
                segment = null;
                return false;
            }

            segment = nextSegment;
            nextSegment = null;
            nextSegmentSwitchTimeSeconds = null;
            return true;
        }

        private KimodoRuntimeGeneratedSegment MarkCurrentSegmentCompleted(float? completedTimeSeconds = null)
        {
            KimodoRuntimeGeneratedSegment completedSegment = currentSegment;
            if (currentSegment != null)
            {
                Vector3 completedRootPosition = currentSegment.LastRootPosition;
                if (completedTimeSeconds.HasValue)
                {
                    KimodoRawMotionUtility.ResolveInterpolatedRootPosition(
                        currentSegment.Motion,
                        completedTimeSeconds.Value,
                        false,
                        out completedRootPosition);
                }
                Vector3 completedDelta = completedRootPosition - currentSegment.FirstRootPosition;
                lastCompletedWorldOffset = currentSegment.WorldAccumulatedOffset + new Vector3(
                    completedDelta.x,
                    completedDelta.y,
                    completedDelta.z);
                hasCompletedSegment = true;
            }

            return completedSegment;
        }

        private void StopActiveMotion()
        {
            sourceBinding = null;
            sourceRootJoint = null;
            currentSegment = null;
            currentSegmentRootBaseline = Vector3.zero;
            timeSeconds = 0f;
            playing = false;
        }

        private void DisposeRetargetCache()
        {
            DisposeSourceRetargetCache();
            retargeter.Dispose();
        }

        private void DisposeSourceRetargetCache()
        {
            sourceBinding = null;
            sourceHipsBone = null;
            sourceCache?.Dispose();
            sourceCache = null;
            sourceCacheModelName = null;
        }

        private bool TryCreateDirectRetargetBinding(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<Animator> targetAnimators,
            bool allowPartialJoints,
            out string error)
        {
            error = string.Empty;
            if (!retargeter.BindTargets(
                    targetAnimators,
                    out bool hasTarget,
                    out error))
            {
                return false;
            }

            if (!hasTarget)
            {
                sourceBinding = null;
                return true;
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar sourceAvatar, out error))
            {
                return false;
            }

            if (sourceCache == null || !string.Equals(sourceCacheModelName, modelName, StringComparison.OrdinalIgnoreCase))
            {
                DisposeSourceRetargetCache();
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        sourceAvatar,
                        "KimodoRuntimeMotionDriver_SourceRetarget",
                        out sourceCache,
                        out error))
                {
                    return false;
                }

                sourceCacheModelName = modelName;
            }

            if (!KimodoRawMotionUtility.TryCreatePlaybackBinding(
                    motion,
                    modelName,
                    sourceCache.skeletonRoot,
                    out sourceBinding,
                    out error,
                    allowPartialJoints))
            {
                return false;
            }

            sourceRootJoint = sourceBinding.joints != null && sourceBinding.joints.Length > 0
                ? sourceBinding.joints[0]
                : null;
            sourceHipsBone = sourceCache.animator.GetBoneTransform(HumanBodyBones.Hips);

            return true;
        }

        private bool TryApplyFrame(int frameIndex, out string error)
        {
            if (sourceBinding != null && !KimodoRawMotionUtility.TryApplyFrame(sourceBinding, frameIndex, out error, applyRootPosition: false))
            {
                return false;
            }

            if (!TryApplySourceDeltaRoot(frameIndex, out error))
            {
                return false;
            }

            ApplyRootGoalLoss();

            return TryApplyHumanoidPose(out error);
        }

        private bool TryApplyTime(float sampleTimeSeconds, out string error)
        {
            if (sourceBinding != null &&
                !KimodoRawMotionUtility.TryApplyTime(sourceBinding, sampleTimeSeconds, out error, loop: false, applyRootPosition: false))
            {
                return false;
            }

            if (!TryApplySourceDeltaRoot(sampleTimeSeconds, out error))
            {
                return false;
            }

            ApplyRootGoalLoss();

            return TryApplyHumanoidPose(out error);
        }

        private bool TryApplyArdyTime(float sampleTimeSeconds, out string error)
        {
            error = string.Empty;
            if (ardyBuffer == null ||
                !ardyBuffer.TryResolveSampleFrames(sampleTimeSeconds, out int frame0, out int frame1, out float blend))
            {
                error = "ARDY motion buffer has no playable frames.";
                return false;
            }

            if (sourceBinding != null)
            {
                for (int i = 0; i < sourceBinding.joints.Length; i++)
                {
                    Transform joint = sourceBinding.joints[i];
                    int motionJoint = sourceBinding.motionJointIndices[i];
                    if (joint == null || motionJoint < 0 ||
                        !ardyBuffer.TryReadLocalRotation(frame0, motionJoint, out Quaternion q0))
                    {
                        continue;
                    }

                    if (blend > 0f && ardyBuffer.TryReadLocalRotation(frame1, motionJoint, out Quaternion q1))
                    {
                        joint.localRotation = Quaternion.Slerp(q0, q1, blend);
                    }
                    else
                    {
                        joint.localRotation = q0;
                    }
                }
            }

            if (!ardyBuffer.TryReadRootPosition(frame0, out Vector3 p0))
            {
                error = $"Failed to read ARDY root position at frame {frame0}.";
                return false;
            }
            Vector3 rootPosition = p0;
            if (blend > 0f && ardyBuffer.TryReadRootPosition(frame1, out Vector3 p1))
            {
                rootPosition = Vector3.Lerp(p0, p1, blend);
            }
            if (sourceBinding?.joints != null && sourceBinding.joints.Length > 0 && sourceBinding.joints[0] != null)
            {
                sourceBinding.joints[0].localPosition = rootPosition;
            }
            return TryApplyHumanoidPose(out error);
        }

        private bool TryApplySourceDeltaRoot(int frameIndex, out string error)
        {
            error = string.Empty;
            if (sourceBinding?.joints == null || sourceBinding.joints.Length == 0 || currentSegment == null)
            {
                return true;
            }

            if (!currentSegment.Motion.TryReadUnityRootPosition(frameIndex, out Vector3 rootPosition))
            {
                error = $"Failed to read source root position for frame {frameIndex}.";
                return false;
            }

            Vector3 delta = rootPosition - currentSegmentRootBaseline;
            sourceBinding.joints[0].localPosition = currentSegment.UseRawRootPosition
                ? rootPosition
                : new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    currentSegment.WorldAccumulatedOffset.y + delta.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
            return true;
        }

        private bool TryApplySourceDeltaRoot(float sampleTimeSeconds, out string error)
        {
            error = string.Empty;
            if (sourceBinding?.joints == null || sourceBinding.joints.Length == 0 || currentSegment == null)
            {
                return true;
            }

            if (!KimodoRawMotionUtility.ResolveInterpolatedRootPosition(currentSegment.Motion, sampleTimeSeconds, false, out Vector3 rootPosition))
            {
                error = $"Failed to sample source root position at time {sampleTimeSeconds:0.###}.";
                return false;
            }

            Vector3 delta = rootPosition - currentSegmentRootBaseline;
            sourceBinding.joints[0].localPosition = currentSegment.UseRawRootPosition
                ? rootPosition
                : new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    currentSegment.WorldAccumulatedOffset.y + delta.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
            return true;
        }

        private bool TryApplyHumanoidPose(out string error)
        {
            return retargeter.TryApplyPose(sourceCache, sourceHipsBone, out error);
        }

        private void ApplyRootGoalLoss()
        {
            if (sourceRootJoint == null || currentSegment?.RootGoalLoss == null)
            {
                return;
            }

            Quaternion rotation = currentSegment.RootGoalLoss.q.normalized;
            sourceRootJoint.localPosition =
                rotation * sourceRootJoint.localPosition + currentSegment.RootGoalLoss.t;
            sourceRootJoint.localRotation =
                (rotation * sourceRootJoint.localRotation).normalized;
        }

    }
}
