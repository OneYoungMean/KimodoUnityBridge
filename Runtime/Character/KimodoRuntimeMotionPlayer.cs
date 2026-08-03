using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeMotionPlayer
    {
        private readonly Queue<KimodoRuntimeGeneratedSegment> queuedSegments = new Queue<KimodoRuntimeGeneratedSegment>();
        private readonly object queueGate = new object();

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
        private TargetRetargetState targetState;
        private float timeSeconds;
        private bool playing;

        private sealed class TargetRetargetState : IDisposable
        {
            public Animator Animator;
            public Avatar Avatar;
            public HumanPoseHandler PoseHandler;
            public Transform HipsBone;
            public Transform LeftUpperLegBone;
            public Transform LeftLowerLegBone;
            public Transform LeftFootBone;
            public Transform RightUpperLegBone;
            public Transform RightLowerLegBone;
            public Transform RightFootBone;
            public Transform LeftFootIkTarget;
            public Transform RightFootIkTarget;
            public Vector3 LeftFootTargetBaselinePosition;
            public Quaternion LeftFootTargetBaselineRotation;
            public Vector3 RightFootTargetBaselinePosition;
            public Quaternion RightFootTargetBaselineRotation;
            public Vector3 SourceLeftFootBaselineWorldPosition;
            public Quaternion SourceLeftFootBaselineWorldRotation;
            public Vector3 SourceRightFootBaselineWorldPosition;
            public Quaternion SourceRightFootBaselineWorldRotation;
            public bool LeftFootIkInitialized;
            public bool RightFootIkInitialized;
            public Vector3 LeftKneePoleLocalDirection;
            public Vector3 RightKneePoleLocalDirection;
            public bool LeftKneePoleInitialized;
            public bool RightKneePoleInitialized;
            public bool AnimatorWasEnabled;
            public bool AnimatorDisabledForRetarget;
            public bool DriveFootIk;

            public void Dispose()
            {
                if (Animator != null && AnimatorDisabledForRetarget)
                {
                    Animator.enabled = AnimatorWasEnabled;
                }

                PoseHandler = null;
                HipsBone = null;
                LeftUpperLegBone = null;
                LeftLowerLegBone = null;
                LeftFootBone = null;
                RightUpperLegBone = null;
                RightLowerLegBone = null;
                RightFootBone = null;
                LeftFootIkTarget = null;
                RightFootIkTarget = null;
                AnimatorDisabledForRetarget = false;
                AnimatorWasEnabled = false;

                Animator = null;
                Avatar = null;
            }
        }

        public bool HasCurrentSegment => currentSegment != null;
        public string CurrentPromptText => currentSegment != null ? currentSegment.PromptText : string.Empty;
        public Vector3 CurrentRootPosition => sourceRootJoint != null ? sourceRootJoint.position : Vector3.zero;
        public Transform ConstraintSkeletonRoot => sourceCache != null ? sourceCache.skeletonRoot : null;
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
                lock (queueGate)
                {
                    foreach (KimodoRuntimeGeneratedSegment segment in queuedSegments)
                    {
                        total += Mathf.Max(0f, segment?.EffectiveLastFrameTimeSeconds ?? 0f);
                    }
                }
                return total;
            }
        }

        public int QueuedSegmentCount
        {
            get
            {
                lock (queueGate)
                {
                    return queuedSegments.Count;
                }
            }
        }

        public void Enqueue(KimodoRuntimeGeneratedSegment segment, bool verboseLogging)
        {
            if (segment == null)
            {
                return;
            }

            lock (queueGate)
            {
                queuedSegments.Enqueue(segment);
                if (verboseLogging)
                {
                    Debug.Log($"[KimodoRuntimeMotionDriver] Enqueue segment {segment.Index}. queueCount={queuedSegments.Count}");
                }
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
            lock (queueGate)
            {
                queuedSegments.Clear();
            }
        }

        public void ResetCompletionState()
        {
            LastCompletedSegmentIndex = -1;
            lastCompletedWorldOffset = Vector3.zero;
        }

        public void Update(
            float deltaTime,
            string modelName,
            Animator targetAnimator,
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
            SyncFootIkSetting(driveFootIkTargets, leftFootIkTargetName, rightFootIkTargetName);

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
                        targetAnimator,
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
                        targetAnimator,
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
            if (!playing ||
                targetState?.Animator == null ||
                !targetState.Animator.applyRootMotion ||
                sourceHipsBone == null ||
                targetState.HipsBone == null)
            {
                return;
            }

            Vector3 hipsOffset = sourceHipsBone.position - targetState.HipsBone.position;
            targetState.Animator.transform.position += new Vector3(hipsOffset.x, 0f, hipsOffset.z);

            // The setting drives explicitly named IK targets.  Do not run the
            // legacy source-foot solver when a target is absent: it mixes the
            // temporary source skeleton with the destination avatar and can
            // move the legs at a segment boundary.
            if (ShouldSolveFootIk(enableFootIk, targetState.LeftFootIkTarget))
            {
                SolveTwoBoneLeg(
                    targetState.HipsBone,
                    targetState.LeftUpperLegBone,
                    targetState.LeftLowerLegBone,
                    targetState.LeftFootBone,
                    sourceHipsBone,
                    sourceLeftUpperLegBone,
                    sourceLeftLowerLegBone,
                    sourceLeftFootBone,
                    ref targetState.LeftKneePoleLocalDirection,
                    ref targetState.LeftKneePoleInitialized);
            }
            if (ShouldSolveFootIk(enableFootIk, targetState.RightFootIkTarget))
            {
                SolveTwoBoneLeg(
                    targetState.HipsBone,
                    targetState.RightUpperLegBone,
                    targetState.RightLowerLegBone,
                    targetState.RightFootBone,
                    sourceHipsBone,
                    sourceRightUpperLegBone,
                    sourceRightLowerLegBone,
                    sourceRightFootBone,
                    ref targetState.RightKneePoleLocalDirection,
                    ref targetState.RightKneePoleInitialized);
            }
        }

        internal static bool ShouldSolveFootIk(bool enabled, Transform ikTarget)
        {
            return enabled && ikTarget != null;
        }

        public void Stop()
        {
            StopActiveMotion();
            ardyBuffer?.Dispose();
            ardyBuffer = null;
            ardySegment = null;
            DisposeRetargetCache();
        }

        public void DrawDebugSkeleton(Color boneColor, Color jointColor, float jointMarkerSize)
        {
            Transform[] joints = sourceBinding != null ? sourceBinding.joints : null;
            KimodoRawMotionData motion = sourceBinding != null ? sourceBinding.motion : null;
            int[] parents = ardyBuffer != null ? ardyBuffer.JointParents : motion?.jointParents;
            if (joints == null || parents == null)
            {
                return;
            }

            int count = Mathf.Min(joints.Length, parents.Length);
            for (int i = 0; i < count; i++)
            {
                Transform joint = joints[i];
                if (joint == null)
                {
                    continue;
                }

                Vector3 position = joint.position;
                DrawJointMarker(position, jointMarkerSize, jointColor);

                if (parents == null || i >= parents.Length)
                {
                    continue;
                }

                int parentIndex = parents[i];
                if (parentIndex < 0 || parentIndex >= count)
                {
                    continue;
                }

                Transform parent = joints[parentIndex];
                if (parent == null)
                {
                    continue;
                }

                Debug.DrawLine(parent.position, position, boneColor, 0f, false);
            }
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
            Animator targetAnimator,
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
                    targetAnimator,
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
            currentSegment.WorldAccumulatedOffset = ResolveNextWorldOffset(segment.FirstRootPosition);
            currentSegmentRootBaseline = segment.FirstRootPosition;
            ResetTargetFootIkBaselines();
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
            lock (queueGate)
            {
                if (queuedSegments.Count == 0)
                {
                    segment = null;
                    return false;
                }

                segment = queuedSegments.Dequeue();
                return true;
            }
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
            DisposeTargetState();
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

        private void DisposeTargetState()
        {
            targetState?.Dispose();
            targetState = null;
        }

        private Vector3 ResolveNextWorldOffset(Vector3 nextSegmentFirstRootPosition)
        {
            return lastCompletedWorldOffset;
        }

        private bool TryCreateDirectRetargetBinding(
            KimodoRawMotionData motion,
            string modelName,
            Animator targetAnimator,
            bool allowPartialJoints,
            bool driveFootIkTargets,
            string leftFootIkTargetName,
            string rightFootIkTargetName,
            out string error)
        {
            error = string.Empty;
            if (!TrySyncTargetState(
                    targetAnimator,
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
            error = string.Empty;
            if (sourceCache == null || targetState == null)
            {
                return true;
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(sourceCache, out MuscleSample sample, out error))
            {
                return false;
            }

            HumanPose pose = sample.pose;
            KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref pose);
            BuildFootWorldPose(
                sample,
                out Vector3 leftFootWorldPosition,
                out Quaternion leftFootWorldRotation,
                out Vector3 rightFootWorldPosition,
                out Quaternion rightFootWorldRotation);

            if (targetState.PoseHandler == null)
            {
                error = "Target pose handler is not initialized.";
                return false;
            }

            targetState.PoseHandler.SetHumanPose(ref pose);
            ApplyFootIkTargets(
                targetState,
                leftFootWorldPosition,
                leftFootWorldRotation,
                rightFootWorldPosition,
                rightFootWorldRotation);
            return true;
        }

        private bool TrySyncTargetState(
            Animator animator,
            bool driveFootIkTargets,
            string leftFootIkTargetName,
            string rightFootIkTargetName,
            out bool hasTarget,
            out string error)
        {
            error = string.Empty;
            hasTarget = animator != null;

            if (animator == null)
            {
                DisposeTargetState();
                return true;
            }

            Avatar avatar = animator.avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                error = "Humanoid retarget animator avatar is null, invalid, or not humanoid.";
                return false;
            }

            bool needsNewState = targetState == null || !ReferenceEquals(targetState.Animator, animator);
            bool needsNewPoseHandler = needsNewState || targetState.PoseHandler == null || !ReferenceEquals(targetState.Avatar, avatar);
            if (needsNewState)
            {
                DisposeTargetState();
                targetState = new TargetRetargetState
                {
                    Animator = animator
                };
            }

            targetState.Avatar = avatar;
            if (needsNewPoseHandler)
            {
                targetState.PoseHandler = new HumanPoseHandler(avatar, animator.transform);
            }

            targetState.HipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
            targetState.LeftUpperLegBone = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            targetState.LeftLowerLegBone = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            targetState.LeftFootBone = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            targetState.RightUpperLegBone = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            targetState.RightLowerLegBone = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            targetState.RightFootBone = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            targetState.LeftFootIkTarget = driveFootIkTargets
                ? FindChildByNameRecursive(animator.transform, leftFootIkTargetName)
                : null;
            targetState.RightFootIkTarget = driveFootIkTargets
                ? FindChildByNameRecursive(animator.transform, rightFootIkTargetName)
                : null;

            if (!targetState.AnimatorDisabledForRetarget)
            {
                targetState.AnimatorWasEnabled = animator.enabled;
                targetState.AnimatorDisabledForRetarget = true;
            }

            targetState.DriveFootIk = driveFootIkTargets;
            animator.enabled = false;
            return true;
        }

        private void ResetTargetFootIkBaselines()
        {
            if (targetState == null)
            {
                return;
            }

            targetState.LeftFootIkInitialized = false;
            targetState.RightFootIkInitialized = false;
            targetState.LeftKneePoleInitialized = false;
            targetState.RightKneePoleInitialized = false;
        }

        private void SyncFootIkSetting(
            bool driveFootIkTargets,
            string leftFootIkTargetName,
            string rightFootIkTargetName)
        {
            if (targetState == null || targetState.DriveFootIk == driveFootIkTargets)
            {
                return;
            }

            targetState.DriveFootIk = driveFootIkTargets;
            targetState.LeftFootIkTarget = driveFootIkTargets
                ? FindChildByNameRecursive(targetState.Animator.transform, leftFootIkTargetName)
                : null;
            targetState.RightFootIkTarget = driveFootIkTargets
                ? FindChildByNameRecursive(targetState.Animator.transform, rightFootIkTargetName)
                : null;
            ResetTargetFootIkBaselines();
        }

        private static void BuildFootWorldPose(
            MuscleSample sample,
            out Vector3 leftFootWorldPosition,
            out Quaternion leftFootWorldRotation,
            out Vector3 rightFootWorldPosition,
            out Quaternion rightFootWorldRotation)
        {
            HumanPose pose = sample != null ? sample.pose : default;
            Vector3 rootPosition = pose.bodyPosition;
            Quaternion rootRotation = pose.bodyRotation;
            leftFootWorldPosition = rootPosition + rootRotation * (sample != null ? sample.leftFootPosition : Vector3.zero);
            leftFootWorldRotation = rootRotation * (sample != null ? sample.leftFootRotation : Quaternion.identity);
            rightFootWorldPosition = rootPosition + rootRotation * (sample != null ? sample.rightFootPosition : Vector3.zero);
            rightFootWorldRotation = rootRotation * (sample != null ? sample.rightFootRotation : Quaternion.identity);
        }

        private static void DrawJointMarker(Vector3 position, float size, Color color)
        {
            float markerSize = Mathf.Max(0.001f, size);
            Debug.DrawLine(position + Vector3.left * markerSize, position + Vector3.right * markerSize, color, 0f, false);
            Debug.DrawLine(position + Vector3.up * markerSize, position + Vector3.down * markerSize, color, 0f, false);
            Debug.DrawLine(position + Vector3.forward * markerSize, position + Vector3.back * markerSize, color, 0f, false);
        }

        private static void ApplyFootIkTargets(
            TargetRetargetState state,
            Vector3 leftFootWorldPosition,
            Quaternion leftFootWorldRotation,
            Vector3 rightFootWorldPosition,
            Quaternion rightFootWorldRotation)
        {
            if (state == null || !state.DriveFootIk)
            {
                return;
            }

            ApplyFootIkTarget(
                state.LeftFootBone,
                state.LeftFootIkTarget,
                ref state.LeftFootIkInitialized,
                ref state.LeftFootTargetBaselinePosition,
                ref state.LeftFootTargetBaselineRotation,
                ref state.SourceLeftFootBaselineWorldPosition,
                ref state.SourceLeftFootBaselineWorldRotation,
                leftFootWorldPosition,
                leftFootWorldRotation);

            ApplyFootIkTarget(
                state.RightFootBone,
                state.RightFootIkTarget,
                ref state.RightFootIkInitialized,
                ref state.RightFootTargetBaselinePosition,
                ref state.RightFootTargetBaselineRotation,
                ref state.SourceRightFootBaselineWorldPosition,
                ref state.SourceRightFootBaselineWorldRotation,
                rightFootWorldPosition,
                rightFootWorldRotation);
        }

        private static void ApplyFootIkTarget(
            Transform footBone,
            Transform ikTarget,
            ref bool initialized,
            ref Vector3 targetBaselinePosition,
            ref Quaternion targetBaselineRotation,
            ref Vector3 sourceBaselineWorldPosition,
            ref Quaternion sourceBaselineWorldRotation,
            Vector3 sourceCurrentWorldPosition,
            Quaternion sourceCurrentWorldRotation)
        {
            if (ikTarget == null)
            {
                return;
            }

            if (!initialized)
            {
                Vector3 alignedPosition = footBone != null ? footBone.position : ikTarget.position;
                Quaternion alignedRotation = footBone != null ? footBone.rotation : ikTarget.rotation;
                ikTarget.SetPositionAndRotation(alignedPosition, alignedRotation);
                targetBaselinePosition = alignedPosition;
                targetBaselineRotation = alignedRotation;
                sourceBaselineWorldPosition = sourceCurrentWorldPosition;
                sourceBaselineWorldRotation = sourceCurrentWorldRotation;
                initialized = true;
                return;
            }

            Vector3 deltaPosition = sourceCurrentWorldPosition - sourceBaselineWorldPosition;
            Quaternion deltaRotation = sourceCurrentWorldRotation * Quaternion.Inverse(sourceBaselineWorldRotation);
            ikTarget.SetPositionAndRotation(
                targetBaselinePosition + deltaPosition,
                deltaRotation * targetBaselineRotation);
        }

        internal static void SolveTwoBoneLeg(
            Transform targetHips,
            Transform upperLeg,
            Transform lowerLeg,
            Transform foot,
            Transform sourceHips,
            Transform sourceUpperLeg,
            Transform sourceLowerLeg,
            Transform sourceFoot,
            ref Vector3 previousPoleLocalDirection,
            ref bool poleInitialized)
        {
            if (targetHips == null || upperLeg == null || lowerLeg == null || foot == null || sourceFoot == null)
            {
                return;
            }

            Vector3 upperPosition = upperLeg.position;
            Vector3 upperToLower = lowerLeg.position - upperPosition;
            Vector3 lowerToFoot = foot.position - lowerLeg.position;
            float upperLength = upperToLower.magnitude;
            float lowerLength = lowerToFoot.magnitude;
            if (upperLength <= 1e-5f || lowerLength <= 1e-5f)
            {
                return;
            }

            Vector3 upperToTarget = sourceFoot.position - upperPosition;
            float targetDistance = upperToTarget.magnitude;
            Vector3 targetDirection = targetDistance > 1e-5f
                ? upperToTarget / targetDistance
                : (foot.position - upperPosition).normalized;
            if (targetDirection.sqrMagnitude <= 1e-8f)
            {
                return;
            }

            float totalLength = upperLength + lowerLength;
            float minimumReach = Mathf.Abs(upperLength - lowerLength) + 1e-4f;
            float maximumReach = Mathf.Min(totalLength - 1e-4f, totalLength * 0.995f);
            if (maximumReach <= minimumReach)
            {
                return;
            }

            float reachableDistance = Mathf.Clamp(targetDistance, minimumReach, maximumReach);
            Vector3 reachableTarget = upperPosition + targetDirection * reachableDistance;
            Vector3 previousBendDirection = poleInitialized
                ? Vector3.ProjectOnPlane(
                    targetHips.TransformDirection(previousPoleLocalDirection),
                    targetDirection)
                : Vector3.zero;
            Vector3 bendDirection = Vector3.zero;
            if (sourceHips != null && sourceUpperLeg != null && sourceLowerLeg != null)
            {
                Vector3 sourceTargetDirection = sourceFoot.position - sourceUpperLeg.position;
                Vector3 sourceBendDirection = Vector3.ProjectOnPlane(
                    sourceLowerLeg.position - sourceUpperLeg.position,
                    sourceTargetDirection);
                if (sourceBendDirection.sqrMagnitude > 1e-8f)
                {
                    Vector3 sourcePoleLocalDirection =
                        sourceHips.InverseTransformDirection(sourceBendDirection.normalized);
                    bendDirection = Vector3.ProjectOnPlane(
                        targetHips.TransformDirection(sourcePoleLocalDirection),
                        targetDirection);
                }
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = previousBendDirection;
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = Vector3.ProjectOnPlane(upperToLower, targetDirection);
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = Vector3.ProjectOnPlane(upperLeg.forward, targetDirection);
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = Vector3.ProjectOnPlane(upperLeg.right, targetDirection);
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                return;
            }
            bendDirection.Normalize();
            if (previousBendDirection.sqrMagnitude > 1e-8f &&
                Vector3.Dot(bendDirection, previousBendDirection) < 0f)
            {
                bendDirection = -bendDirection;
            }
            previousPoleLocalDirection =
                targetHips.InverseTransformDirection(bendDirection).normalized;
            poleInitialized = true;

            float alongTarget =
                (upperLength * upperLength + reachableDistance * reachableDistance - lowerLength * lowerLength) /
                (2f * reachableDistance);
            float awayFromTarget = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - alongTarget * alongTarget));
            Vector3 desiredLowerPosition =
                upperPosition + targetDirection * alongTarget + bendDirection * awayFromTarget;
            Quaternion footWorldRotation = foot.rotation;

            upperLeg.rotation =
                Quaternion.FromToRotation(upperToLower, desiredLowerPosition - upperPosition) * upperLeg.rotation;
            Vector3 adjustedLowerToFoot = foot.position - lowerLeg.position;
            Vector3 adjustedLowerToTarget = reachableTarget - lowerLeg.position;
            if (adjustedLowerToFoot.sqrMagnitude > 1e-8f && adjustedLowerToTarget.sqrMagnitude > 1e-8f)
            {
                lowerLeg.rotation =
                    Quaternion.FromToRotation(adjustedLowerToFoot, adjustedLowerToTarget) * lowerLeg.rotation;
            }
            foot.rotation = footWorldRotation;
        }

        private static Transform FindChildByNameRecursive(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
            {
                return null;
            }

            if (string.Equals(root.name, childName, StringComparison.Ordinal))
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                Transform found = FindChildByNameRecursive(child, childName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
