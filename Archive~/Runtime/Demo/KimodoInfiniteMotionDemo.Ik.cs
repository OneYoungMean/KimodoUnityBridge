using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    public sealed partial class KimodoInfiniteMotionDemo : MonoBehaviour
    {
        private bool ApplyMarkerSampleImmediately(KimodoMarkerSampleResult sample, out string error)
        {
            error = string.Empty;
            if (sample == null)
            {
                error = "Marker sample is null.";
                return false;
            }

            if (profileSkeletonRoot == null)
            {
                error = "Profile skeleton root is not assigned.";
                return false;
            }

            Dictionary<string, Transform> nameMap = BuildUniqueNameMap(profileSkeletonRoot);
            if (!KimodoRetargetAvatarUtility.TryApplyMarkerSampleToTransformMap(
                    sample,
                    modelName,
                    profileSkeletonRoot,
                    nameMap,
                    out error))
            {
                return false;
            }

            return motionPlayer.TryApplyMarkerSample(
                sample,
                modelName,
                humanoidRetargetAnimators,
                driveFootIkTargets,
                leftFootIkTargetName,
                rightFootIkTargetName,
                out error);
        }

        private static Dictionary<string, Transform> BuildUniqueNameMap(Transform root)
        {
            var uniqueMap = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null)
            {
                return uniqueMap;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform current = all[i];
                if (current == null || string.IsNullOrWhiteSpace(current.name) || ambiguous.Contains(current.name))
                {
                    continue;
                }

                if (uniqueMap.TryGetValue(current.name, out Transform existing) && existing != current)
                {
                    uniqueMap.Remove(current.name);
                    ambiguous.Add(current.name);
                    continue;
                }

                uniqueMap[current.name] = current;
            }

            return uniqueMap;
        }

        private sealed class GeneratedSegment
        {
            public int Index;
            public string PromptText;
            public KimodoRawMotionData Motion;
            public List<KimodoMarkerSampleResult> ConstraintOverlapPoses;
            public Vector3 FirstRootPosition;
            public Vector3 LastRootPosition;
            public Vector3 WorldAccumulatedOffset;
            public int EffectiveLastFrameIndex;
            public float EffectiveLastFrameTimeSeconds;
        }

        private sealed class RawMotionPlayer
        {
            private readonly Queue<GeneratedSegment> queuedSegments = new Queue<GeneratedSegment>();
            private readonly object queueGate = new object();
            private readonly List<TargetRetargetState> targetStates = new List<TargetRetargetState>();

            private KimodoRawMotionPlaybackBinding profileBinding;
            private KimodoRawMotionPlaybackBinding sourceBinding;
            private SkeletonCache sourceCache;
            private string sourceCacheModelName;
            private Transform profileRootJoint;
            private Vector3 currentSegmentRootBaseline;
            private Vector3 lastCompletedWorldOffset;
            private GeneratedSegment currentSegment;
            private float timeSeconds;
            private bool playing;

            private sealed class TargetRetargetState : IDisposable
            {
                public Animator Animator;
                public Avatar Avatar;
                public HumanPoseHandler PoseHandler;
                public Transform LeftFootBone;
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
                public bool AnimatorWasEnabled;
                public bool AnimatorDisabledForRetarget;

                public void Dispose()
                {
                    if (Animator != null && AnimatorDisabledForRetarget)
                    {
                        Animator.enabled = AnimatorWasEnabled;
                    }

                    Animator = null;
                    Avatar = null;
                    PoseHandler = null;
                    AnimatorDisabledForRetarget = false;
                    AnimatorWasEnabled = false;
                }
            }

            public bool IsPlaying => playing;
            public int LastCompletedSegmentIndex { get; private set; } = -1;

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

            public void Enqueue(GeneratedSegment segment, bool verboseLogging, QueueDebugState debugState)
            {
                if (segment == null)
                {
                    return;
                }

                lock (queueGate)
                {
                    queuedSegments.Enqueue(segment);
                    if (debugState != null)
                    {
                        debugState.LastEnqueuedSegmentIndex = segment.Index;
                    }

                    if (verboseLogging)
                    {
                        Debug.Log(
                            $"[KimodoInfiniteMotionDemo] Enqueue segment {segment.Index}. queueCount={queuedSegments.Count}");
                    }
                }
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
                Transform profileSkeletonRoot,
                IReadOnlyList<Animator> humanoidRetargetAnimators,
                bool allowPartialJoints,
                bool driveFootIkTargets,
                string leftFootIkTargetName,
                string rightFootIkTargetName,
                bool verboseLogging,
                QueueDebugState debugState,
                out GeneratedSegment startedSegment,
                out string error)
            {
                startedSegment = null;
                error = string.Empty;

                if (playing && profileBinding != null)
                {
                    AdvanceCurrentMotion(deltaTime, out error);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return;
                    }
                }

                if (!playing && TryDequeue(out GeneratedSegment next))
                {
                    if (debugState != null)
                    {
                        debugState.LastDequeuedSegmentIndex = next.Index;
                    }

                    if (verboseLogging)
                    {
                        Debug.Log($"[KimodoInfiniteMotionDemo] Attempting to play dequeued segment {next.Index}.");
                    }

                    if (!Play(
                            next,
                            modelName,
                            profileSkeletonRoot,
                            humanoidRetargetAnimators,
                            allowPartialJoints,
                            driveFootIkTargets,
                            leftFootIkTargetName,
                            rightFootIkTargetName,
                            out error,
                            verboseLogging,
                            debugState))
                    {
                        return;
                    }

                    startedSegment = next;
                }
            }

            public void Stop()
            {
                StopActiveMotion();
                DisposeRetargetCache();
            }

            public bool TryApplyMarkerSample(
                KimodoMarkerSampleResult sample,
                string modelName,
                IReadOnlyList<Animator> humanoidAnimators,
                bool driveFootIkTargets,
                string leftFootIkTargetName,
                string rightFootIkTargetName,
                out string error)
            {
                error = string.Empty;
                if (sample == null)
                {
                    error = "Marker sample is null.";
                    return false;
                }

                if (!TrySyncTargetStates(
                        humanoidAnimators,
                        driveFootIkTargets,
                        leftFootIkTargetName,
                        rightFootIkTargetName,
                        out bool hasTargets,
                        out error))
                {
                    return false;
                }

                if (!hasTargets)
                {
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
                            "KimodoInfiniteMotionDemo_SourceRetarget",
                            out sourceCache,
                            out error))
                    {
                        return false;
                    }

                    sourceCacheModelName = modelName;
                }

                if (!KimodoRetargetAvatarUtility.TryApplyMarkerSampleToTransformMap(
                        sample,
                        modelName,
                        sourceCache.skeletonRoot,
                        sourceCache.uniqueNameMap,
                        out error))
                {
                    return false;
                }

                ResetTargetFootIkBaselines();
                return TryApplyHumanoidPose(out error);
            }

            private bool Play(
                GeneratedSegment segment,
                string modelName,
                Transform profileSkeletonRoot,
                IReadOnlyList<Animator> humanoidRetargetAnimators,
                bool allowPartialJoints,
                bool driveFootIkTargets,
                string leftFootIkTargetName,
                string rightFootIkTargetName,
                out string error,
                bool verboseLogging,
                QueueDebugState debugState)
            {
                StopActiveMotion();
                if (debugState != null)
                {
                    debugState.LastPlayAttemptSegmentIndex = segment != null ? segment.Index : -1;
                }

                if (!KimodoRawMotionUtility.TryCreatePlaybackBinding(
                        segment.Motion,
                        modelName,
                        profileSkeletonRoot,
                        out profileBinding,
                        out error,
                        allowPartialJoints))
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning(
                            $"[KimodoInfiniteMotionDemo] Play segment {segment?.Index ?? -1} failed while creating profile binding: {error}");
                    }

                    return false;
                }

                profileRootJoint = profileBinding.joints != null && profileBinding.joints.Length > 0
                    ? profileBinding.joints[0]
                    : null;
                if (!TryCreateDirectRetargetBinding(
                        segment.Motion,
                        modelName,
                        humanoidRetargetAnimators,
                        allowPartialJoints,
                        driveFootIkTargets,
                        leftFootIkTargetName,
                        rightFootIkTargetName,
                        out error))
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning(
                            $"[KimodoInfiniteMotionDemo] Play segment {segment?.Index ?? -1} failed while creating retarget binding: {error}");
                    }

                    StopActiveMotion();
                    return false;
                }

                currentSegment = segment;
                currentSegment.WorldAccumulatedOffset = ResolveNextWorldOffset(segment.FirstRootPosition);
                currentSegmentRootBaseline = segment.FirstRootPosition;
                ResetTargetFootIkBaselines();
                timeSeconds = 0f;
                playing = true;
                if (!TryApplyFrame(0, out error))
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning(
                            $"[KimodoInfiniteMotionDemo] Play segment {segment?.Index ?? -1} failed while applying frame 0: {error}");
                    }

                    return false;
                }

                if (debugState != null)
                {
                    debugState.LastPlayStartedSegmentIndex = segment.Index;
                }

                if (verboseLogging)
                {
                    Debug.Log(
                        $"[KimodoInfiniteMotionDemo] Play segment {segment.Index} started. worldOffset={currentSegment.WorldAccumulatedOffset}");
                }

                return true;
            }

            private void AdvanceCurrentMotion(float deltaTime, out string error)
            {
                error = string.Empty;
                if (!playing || profileBinding == null)
                {
                    return;
                }

                timeSeconds += Mathf.Max(0f, deltaTime);
                bool reachedEnd = false;
                float segmentEndTime = currentSegment != null
                    ? Mathf.Max(0f, currentSegment.EffectiveLastFrameTimeSeconds)
                    : (profileBinding.Motion != null ? profileBinding.Motion.LastFrameTimeSeconds : 0f);
                if (profileBinding.Motion != null && timeSeconds >= segmentEndTime)
                {
                    timeSeconds = segmentEndTime;
                    reachedEnd = true;
                }

                if (!TryApplyTime(timeSeconds, out error))
                {
                    StopActiveMotion();
                    return;
                }

                if (reachedEnd)
                {
                    MarkCurrentSegmentCompleted();
                    StopActiveMotion();
                }
            }

            private bool TryDequeue(out GeneratedSegment segment)
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

            private void MarkCurrentSegmentCompleted()
            {
                if (currentSegment != null && currentSegment.Index > LastCompletedSegmentIndex)
                {
                    LastCompletedSegmentIndex = currentSegment.Index;
                    Vector3 completedDelta = currentSegment.LastRootPosition - currentSegment.FirstRootPosition;
                    lastCompletedWorldOffset = currentSegment.WorldAccumulatedOffset + new Vector3(
                        completedDelta.x,
                        0f,
                        completedDelta.z);
                }
            }

            private void StopActiveMotion()
            {
                profileBinding = null;
                sourceBinding = null;
                profileRootJoint = null;
                currentSegment = null;
                currentSegmentRootBaseline = Vector3.zero;
                timeSeconds = 0f;
                playing = false;
            }

            private void DisposeRetargetCache()
            {
                DisposeSourceRetargetCache();
                DisposeTargetStates();
            }

            private void DisposeSourceRetargetCache()
            {
                sourceBinding = null;
                sourceCache?.Dispose();
                sourceCache = null;
                sourceCacheModelName = null;
            }

            private Vector3 ResolveNextWorldOffset(Vector3 nextSegmentFirstRootPosition)
            {
                return lastCompletedWorldOffset;
            }

            private bool TryCreateDirectRetargetBinding(
                KimodoRawMotionData motion,
                string modelName,
                IReadOnlyList<Animator> humanoidAnimators,
                bool allowPartialJoints,
                bool driveFootIkTargets,
                string leftFootIkTargetName,
                string rightFootIkTargetName,
                out string error)
            {
                error = string.Empty;
                if (!TrySyncTargetStates(
                        humanoidAnimators,
                        driveFootIkTargets,
                        leftFootIkTargetName,
                        rightFootIkTargetName,
                        out bool hasTargets,
                        out error))
                {
                    return false;
                }

                if (!hasTargets)
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
                            "KimodoInfiniteMotionDemo_SourceRetarget",
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

                return true;
            }

            private bool TryApplyFrame(int frameIndex, out string error)
            {
                if (!KimodoRawMotionUtility.TryApplyFrame(profileBinding, frameIndex, out error, applyRootPosition: false))
                {
                    return false;
                }

                if (!TryApplyProfileDeltaRoot(frameIndex, out error))
                {
                    return false;
                }

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
                if (!KimodoRawMotionUtility.TryApplyTime(profileBinding, sampleTimeSeconds, out error, loop: false, applyRootPosition: false))
                {
                    return false;
                }

                if (!TryApplyProfileDeltaRoot(sampleTimeSeconds, out error))
                {
                    return false;
                }

                if (sourceBinding != null && !KimodoRawMotionUtility.TryApplyTime(sourceBinding, sampleTimeSeconds, out error, loop: false, applyRootPosition: false))
                {
                    return false;
                }

                if (!TryApplySourceDeltaRoot(sampleTimeSeconds, out error))
                {
                    return false;
                }

                return TryApplyHumanoidPose(out error);
            }

            private bool TryApplyProfileDeltaRoot(int frameIndex, out string error)
            {
                error = string.Empty;
                if (profileRootJoint == null || currentSegment == null)
                {
                    return true;
                }

                if (!currentSegment.Motion.TryReadUnityRootPosition(frameIndex, out Vector3 rootPosition))
                {
                    error = $"Failed to read profile root position for frame {frameIndex}.";
                    return false;
                }

                Vector3 delta = rootPosition - currentSegmentRootBaseline;
                profileRootJoint.localPosition = new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
                return true;
            }

            private bool TryApplyProfileDeltaRoot(float sampleTimeSeconds, out string error)
            {
                error = string.Empty;
                if (profileRootJoint == null || currentSegment == null)
                {
                    return true;
                }

                if (!KimodoRawMotionUtility.ResolveInterpolatedRootPosition(currentSegment.Motion, sampleTimeSeconds, false, out Vector3 rootPosition))
                {
                    error = $"Failed to sample profile root position at time {sampleTimeSeconds:0.###}.";
                    return false;
                }

                Vector3 delta = rootPosition - currentSegmentRootBaseline;
                profileRootJoint.localPosition = new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
                return true;
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
                sourceBinding.joints[0].localPosition = new Vector3(
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
                sourceBinding.joints[0].localPosition = new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
                return true;
            }

            private bool TryApplyHumanoidPose(out string error)
            {
                error = string.Empty;
                if (sourceCache == null || targetStates.Count == 0)
                {
                    return true;
                }

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(sourceCache, out MuscleSample sample, out error))
                {
                    return false;
                }

                HumanPose pose = sample.pose;
                BuildFootWorldPose(
                    sample,
                    out Vector3 leftFootWorldPosition,
                    out Quaternion leftFootWorldRotation,
                    out Vector3 rightFootWorldPosition,
                    out Quaternion rightFootWorldRotation);
                for (int i = 0; i < targetStates.Count; i++)
                {
                    TargetRetargetState state = targetStates[i];
                    HumanPoseHandler poseHandler = state.PoseHandler;
                    if (poseHandler == null)
                    {
                        continue;
                    }

                    poseHandler.SetHumanPose(ref pose);
                    ApplyFootIkTargets(
                        state,
                        leftFootWorldPosition,
                        leftFootWorldRotation,
                        rightFootWorldPosition,
                        rightFootWorldRotation);
                }

                return true;
            }

            private bool TrySyncTargetStates(
                IReadOnlyList<Animator> humanoidAnimators,
                bool driveFootIkTargets,
                string leftFootIkTargetName,
                string rightFootIkTargetName,
                out bool hasTargets,
                out string error)
            {
                error = string.Empty;
                hasTargets = false;

                var desiredAnimators = new HashSet<Animator>();
                if (humanoidAnimators != null)
                {
                    for (int i = 0; i < humanoidAnimators.Count; i++)
                    {
                        Animator animator = humanoidAnimators[i];
                        if (animator == null || !desiredAnimators.Add(animator))
                        {
                            continue;
                        }

                        Avatar avatar = animator.avatar;
                        if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
                        {
                            error = $"Humanoid retarget animator at index {i} has a null, invalid, or non-humanoid avatar.";
                            return false;
                        }

                        hasTargets = true;
                    }
                }

                for (int i = targetStates.Count - 1; i >= 0; i--)
                {
                    TargetRetargetState state = targetStates[i];
                    if (state == null || state.Animator == null || !desiredAnimators.Contains(state.Animator))
                    {
                        state?.Dispose();
                        targetStates.RemoveAt(i);
                    }
                }

                foreach (Animator animator in desiredAnimators)
                {
                    if (!TryEnsureTargetState(
                            animator,
                            driveFootIkTargets,
                            leftFootIkTargetName,
                            rightFootIkTargetName,
                            out error))
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool TryEnsureTargetState(
                Animator animator,
                bool driveFootIkTargets,
                string leftFootIkTargetName,
                string rightFootIkTargetName,
                out string error)
            {
                error = string.Empty;
                if (animator == null)
                {
                    return true;
                }

                Avatar avatar = animator.avatar;
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
                {
                    error = "Humanoid retarget animator avatar is null, invalid, or not humanoid.";
                    return false;
                }

                TargetRetargetState state = null;
                for (int i = 0; i < targetStates.Count; i++)
                {
                    if (ReferenceEquals(targetStates[i].Animator, animator))
                    {
                        state = targetStates[i];
                        break;
                    }
                }

                bool needsNewState = state == null;
                bool needsNewPoseHandler = state == null || state.PoseHandler == null || !ReferenceEquals(state.Avatar, avatar);
                if (needsNewState)
                {
                    state = new TargetRetargetState
                    {
                        Animator = animator
                    };
                    targetStates.Add(state);
                }

                if (needsNewPoseHandler)
                {
                    state.Avatar = avatar;
                    state.PoseHandler = new HumanPoseHandler(avatar, animator.transform);
                }

                state.LeftFootBone = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                state.RightFootBone = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                state.LeftFootIkTarget = driveFootIkTargets
                    ? FindChildByNameRecursive(animator.transform, leftFootIkTargetName)
                    : null;
                state.RightFootIkTarget = driveFootIkTargets
                    ? FindChildByNameRecursive(animator.transform, rightFootIkTargetName)
                    : null;

                if (!state.AnimatorDisabledForRetarget)
                {
                    state.AnimatorWasEnabled = animator.enabled;
                    state.AnimatorDisabledForRetarget = true;
                }

                animator.enabled = false;
                return true;
            }

            private void DisposeTargetStates()
            {
                for (int i = targetStates.Count - 1; i >= 0; i--)
                {
                    targetStates[i]?.Dispose();
                }

                targetStates.Clear();
            }

            private void ResetTargetFootIkBaselines()
            {
                for (int i = 0; i < targetStates.Count; i++)
                {
                    TargetRetargetState state = targetStates[i];
                    if (state == null)
                    {
                        continue;
                    }

                    state.LeftFootIkInitialized = false;
                    state.RightFootIkInitialized = false;
                }
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

            private static void ApplyFootIkTargets(
                TargetRetargetState state,
                Vector3 leftFootWorldPosition,
                Quaternion leftFootWorldRotation,
                Vector3 rightFootWorldPosition,
                Quaternion rightFootWorldRotation)
            {
                if (state == null)
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
}
