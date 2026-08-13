using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeMotionPlayer
    {
        private readonly Queue<KimodoRuntimeGeneratedSegment> queuedSegments = new Queue<KimodoRuntimeGeneratedSegment>();

        private KimodoRawMotionPlaybackBinding sourceBinding;
        private SkeletonCache sourceCache;
        private string sourceCacheModelName;
        private Transform sourceRootJoint;
        private Transform sourceHipsBone;
        private Transform sourceLeftUpperLegBone;
        private Transform sourceLeftLowerLegBone;
        private Transform sourceLeftFootBone;
        private Transform sourceRightUpperLegBone;
        private Transform sourceRightLowerLegBone;
        private Transform sourceRightFootBone;
        private Vector3 currentSegmentRootBaseline;
        private Vector3 lastCompletedWorldOffset;
        private KimodoRuntimeGeneratedSegment currentSegment;
        private KimodoRuntimeGeneratedSegment ardySegment;
        private KimodoArdyMotionBuffer ardyBuffer;
        private readonly KimodoRuntimeHumanoidRetargeter retargeter = new KimodoRuntimeHumanoidRetargeter();
        private float timeSeconds;
        private bool playing;

        public bool HasCurrentSegment => currentSegment != null;
        public string CurrentPromptText => currentSegment != null ? currentSegment.PromptText : string.Empty;
        public Vector3 CurrentRootPosition => sourceRootJoint != null ? sourceRootJoint.position : Vector3.zero;
        public Vector3 NextSegmentRootOrigin => currentSegment != null
            ? currentSegment.WorldAccumulatedOffset + new Vector3(
                currentSegment.LastRootPosition.x - currentSegment.FirstRootPosition.x,
                0f,
                currentSegment.LastRootPosition.z - currentSegment.FirstRootPosition.z)
            : lastCompletedWorldOffset;
        public float SourceHumanScale => sourceCache != null ? Mathf.Max(1e-6f, sourceCache.humanScale) : 1f;
        public Transform ConstraintSkeletonRoot => sourceCache != null ? sourceCache.skeletonRoot : null;
        internal SkeletonCache ConstraintSkeletonCache => sourceCache;
        internal Transform DebugProfileSkeletonRoot => sourceCache != null ? sourceCache.skeletonRoot : null;
        public int LastCompletedSegmentIndex { get; private set; } = -1;
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
                foreach (KimodoRuntimeGeneratedSegment segment in queuedSegments)
                {
                    total += Mathf.Max(0f, segment?.EffectiveLastFrameTimeSeconds ?? 0f);
                }
                return total;
            }
        }

        public int QueuedSegmentCount => queuedSegments.Count;

        public void Enqueue(KimodoRuntimeGeneratedSegment segment, bool verboseLogging)
        {
            if (segment == null)
            {
                return;
            }

            queuedSegments.Enqueue(segment);
            if (verboseLogging)
            {
                Debug.Log($"[KimodoRuntimeMotionDriver] Enqueue segment {segment.Index}. queueCount={queuedSegments.Count}");
            }
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

        public void ClearQueue()
        {
            queuedSegments.Clear();
        }

        public void ResetCompletionState()
        {
            LastCompletedSegmentIndex = -1;
            lastCompletedWorldOffset = Vector3.zero;
        }

        public void Update(
            float deltaTime,
            string modelName,
            IReadOnlyList<Animator> targetAnimators,
            bool allowPartialJoints,
            bool driveFootIkTargets,
            string leftFootIkTargetName,
            string rightFootIkTargetName,
            bool verboseLogging,
            out KimodoRuntimeGeneratedSegment startedSegment,
            out KimodoRuntimeGeneratedSegment completedSegment,
            out string error)
        {
            startedSegment = null;
            completedSegment = null;
            error = string.Empty;
            retargeter.SyncFootIkSetting(driveFootIkTargets, leftFootIkTargetName, rightFootIkTargetName);

            if (playing && sourceBinding != null)
            {
                AdvanceCurrentMotion(deltaTime, out completedSegment, out error);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return;
                }
            }

            if (!playing && ardyBuffer != null && ardySegment != null)
            {
                if (!Play(
                        ardySegment,
                        modelName,
                        targetAnimators,
                        allowPartialJoints,
                        driveFootIkTargets,
                        leftFootIkTargetName,
                        rightFootIkTargetName,
                        out error,
                        verboseLogging))
                {
                    return;
                }
                startedSegment = ardySegment;
                return;
            }

            if (!playing && TryDequeue(out KimodoRuntimeGeneratedSegment next))
            {
                if (verboseLogging)
                {
                    Debug.Log($"[KimodoRuntimeMotionDriver] Attempting to play dequeued segment {next.Index}.");
                }

                if (!Play(
                        next,
                        modelName,
                        targetAnimators,
                        allowPartialJoints,
                        driveFootIkTargets,
                        leftFootIkTargetName,
                        rightFootIkTargetName,
                        out error,
                        verboseLogging))
                {
                    return;
                }

                startedSegment = next;
            }
        }

        public void ApplyLateRetargetCorrection(bool enableFootIk)
        {
            if (!playing)
            {
                return;
            }

            retargeter.ApplyLateCorrection(
                enableFootIk,
                sourceHipsBone,
                sourceLeftUpperLegBone,
                sourceLeftLowerLegBone,
                sourceLeftFootBone,
                sourceRightUpperLegBone,
                sourceRightLowerLegBone,
                sourceRightFootBone);
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
            if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
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
            bool driveFootIkTargets,
            string leftFootIkTargetName,
            string rightFootIkTargetName,
            out string error,
            bool verboseLogging)
        {
            StopActiveMotion();
            if (!TryCreateDirectRetargetBinding(
                    segment.Motion,
                    modelName,
                    targetAnimators,
                    allowPartialJoints,
                    driveFootIkTargets,
                    leftFootIkTargetName,
                    rightFootIkTargetName,
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

        private bool TryDequeue(out KimodoRuntimeGeneratedSegment segment)
        {
            if (queuedSegments.Count == 0)
            {
                segment = null;
                return false;
            }

            segment = queuedSegments.Dequeue();
            return true;
        }

        private KimodoRuntimeGeneratedSegment MarkCurrentSegmentCompleted()
        {
            KimodoRuntimeGeneratedSegment completedSegment = currentSegment;
            if (currentSegment != null && currentSegment.Index > LastCompletedSegmentIndex)
            {
                LastCompletedSegmentIndex = currentSegment.Index;
                Vector3 completedDelta = currentSegment.LastRootPosition - currentSegment.FirstRootPosition;
                lastCompletedWorldOffset = currentSegment.WorldAccumulatedOffset + new Vector3(
                    completedDelta.x,
                    0f,
                    completedDelta.z);
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
            sourceLeftUpperLegBone = null;
            sourceLeftLowerLegBone = null;
            sourceLeftFootBone = null;
            sourceRightUpperLegBone = null;
            sourceRightLowerLegBone = null;
            sourceRightFootBone = null;
            sourceCache?.Dispose();
            sourceCache = null;
            sourceCacheModelName = null;
        }

        private bool TryCreateDirectRetargetBinding(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<Animator> targetAnimators,
            bool allowPartialJoints,
            bool driveFootIkTargets,
            string leftFootIkTargetName,
            string rightFootIkTargetName,
            out string error)
        {
            error = string.Empty;
            if (!retargeter.BindTargets(
                    targetAnimators,
                    driveFootIkTargets,
                    leftFootIkTargetName,
                    rightFootIkTargetName,
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
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
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
            sourceLeftUpperLegBone = sourceCache.animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            sourceLeftLowerLegBone = sourceCache.animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            sourceLeftFootBone = sourceCache.animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            sourceRightUpperLegBone = sourceCache.animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            sourceRightLowerLegBone = sourceCache.animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            sourceRightFootBone = sourceCache.animator.GetBoneTransform(HumanBodyBones.RightFoot);

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
                    rootPosition.y,
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
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
            return true;
        }

        private bool TryApplyHumanoidPose(out string error)
        {
            return retargeter.TryApplyPose(sourceCache, sourceHipsBone, out error);
        }

    }
}
