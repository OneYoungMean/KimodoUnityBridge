using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEngine;
using UnityEngine.Serialization;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeConstraintBuffer
    {
        private readonly List<KimodoMarkerSampleResult> overlapPoses = new List<KimodoMarkerSampleResult>();
        private readonly List<KimodoMarkerSampleResult> stagedSamples = new List<KimodoMarkerSampleResult>();
        private readonly List<KimodoMarkerSampleResult> pendingSamples = new List<KimodoMarkerSampleResult>();

        internal int StagedCount => stagedSamples.Count;
        internal int PendingCount => pendingSamples.Count;
        internal int OverlapCount => overlapPoses.Count;

        internal void Stage(KimodoMarkerSampleResult sample, double absoluteTimeOffset = 0.0)
        {
            if (sample == null)
            {
                return;
            }

            sample.sampleTime += absoluteTimeOffset;
            UpsertByType(stagedSamples, sample);
        }

        internal bool CommitStaged()
        {
            if (stagedSamples.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < stagedSamples.Count; i++)
            {
                UpsertByType(pendingSamples, stagedSamples[i]);
            }

            stagedSamples.Clear();
            return true;
        }

        internal void ClearUserConstraints()
        {
            stagedSamples.Clear();
            pendingSamples.Clear();
        }

        internal void ClearAll()
        {
            ClearUserConstraints();
            overlapPoses.Clear();
        }

        internal void SetOverlapPoses(IReadOnlyList<KimodoMarkerSampleResult> poses)
        {
            overlapPoses.Clear();
            if (poses == null)
            {
                return;
            }

            for (int i = 0; i < poses.Count; i++)
            {
                if (poses[i] != null)
                {
                    overlapPoses.Add(poses[i]);
                }
            }
        }

        internal void ClearOverlapPoses()
        {
            overlapPoses.Clear();
        }

        internal List<KimodoMarkerSampleResult> BuildActive(
            bool isArdy,
            double ardyApplyTime,
            bool includeOverlap,
            float maxConstraintTime,
            string fullBodyConstraintType,
            string root2DTargetConstraintType)
        {
            if (!isArdy)
            {
                RemoveByType(pendingSamples, root2DTargetConstraintType);
            }

            var samples = new List<KimodoMarkerSampleResult>();
            if (includeOverlap)
            {
                KimodoMarkerSampleResult terminalPose = null;
                for (int i = 0; i < overlapPoses.Count; i++)
                {
                    KimodoMarkerSampleResult candidate = overlapPoses[i];
                    if (candidate != null &&
                        (terminalPose == null || candidate.sampleTime < terminalPose.sampleTime))
                    {
                        terminalPose = candidate;
                    }
                }

                if (terminalPose != null)
                {
                    KimodoMarkerSampleResult sample = terminalPose.Clone();
                    sample.constraintType = fullBodyConstraintType;
                    sample.sampleTime = 0.0;
                    sample.kimodoRootPosition = new Vector3(0f, sample.kimodoRootPosition.y, 0f);
                    sample.unityRootPos = sample.kimodoRootPosition;
                    samples.Add(sample);
                }
            }

            for (int i = 0; i < pendingSamples.Count; i++)
            {
                KimodoMarkerSampleResult pending = pendingSamples[i];
                if (pending == null)
                {
                    continue;
                }

                KimodoMarkerSampleResult clone = pending.Clone();
                clone.sampleTime = isArdy
                    ? Math.Max(0.0, clone.sampleTime - ardyApplyTime)
                    : Mathf.Clamp((float)clone.sampleTime, 0f, maxConstraintTime);
                samples.Add(clone);
            }

            samples.Sort((a, b) => a.sampleTime.CompareTo(b.sampleTime));
            return samples;
        }

        internal void CompleteGeneration(bool isArdy)
        {
            if (!isArdy)
            {
                pendingSamples.Clear();
            }
        }

        private static void UpsertByType(
            List<KimodoMarkerSampleResult> samples,
            KimodoMarkerSampleResult sample)
        {
            RemoveByType(samples, sample?.constraintType);
            if (sample != null)
            {
                samples.Add(sample);
            }
        }

        private static void RemoveByType(List<KimodoMarkerSampleResult> samples, string constraintType)
        {
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                KimodoMarkerSampleResult existing = samples[i];
                if (existing == null ||
                    string.Equals(existing.constraintType, constraintType, StringComparison.OrdinalIgnoreCase))
                {
                    samples.RemoveAt(i);
                }
            }
        }
    }

    internal sealed class KimodoRuntimeGenerationSession : IDisposable
    {
        internal CancellationTokenSource LifetimeCts;
        internal CancellationTokenSource ActiveGenerationCts;
        internal Task SchedulerTask;
        internal bool Running;
        internal bool StartRequested;
        internal bool GenerationInFlight;
        internal int SegmentIndex;
        internal int LastGenerationWaitStatusSegment = -1;
        internal int GenerationRequestVersion;
        internal int? ArdyStreamResolvedSeed;
        internal bool ArdySessionStarted;
        internal bool ArdyPromptDirty = true;
        internal bool ArdyConstraintsDirty = true;
        internal bool ArdySettingsDirty = true;
        internal bool ArdyRefreshPending;
        internal float ArdyEffectivePlaybackReserveSeconds = 1f;
        internal bool GenerationBlocked;
        internal bool AppliedRuntimeSettingsInitialized;
        internal int AppliedTargetSignature;
        internal string AppliedModelsRoot = string.Empty;
        internal string AppliedModelName = string.Empty;
        internal KimodoTextEncoderMode AppliedTextEncoderMode;
        internal bool AppliedForceCpu;
        internal bool AppliedRandomSeed;
        internal int AppliedFixedSeed;

        internal void ResetArdy(float playbackReserveSeconds)
        {
            ArdyStreamResolvedSeed = null;
            ArdySessionStarted = false;
            ArdyPromptDirty = true;
            ArdyConstraintsDirty = true;
            ArdySettingsDirty = true;
            ArdyRefreshPending = false;
            ArdyEffectivePlaybackReserveSeconds = Mathf.Max(0.2f, playbackReserveSeconds);
        }

        public void Dispose()
        {
            try
            {
                LifetimeCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                ActiveGenerationCts?.Cancel();
            }
            catch
            {
            }

            LifetimeCts?.Dispose();
            ActiveGenerationCts?.Dispose();
            LifetimeCts = null;
            ActiveGenerationCts = null;
            SchedulerTask = null;
            Running = false;
            StartRequested = false;
            GenerationInFlight = false;
        }
    }

    [AddComponentMenu("Kimodo/Runtime Motion Driver")]
    public sealed class KimodoRuntimeMotionDriver : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField, HideInInspector]
        private Animator targetHumanoidAnimator;
        [SerializeField] private List<Animator> targetHumanoidAnimators = new List<Animator>();

        [Header("Bridge Runtime")]
        [SerializeField] private string modelsRoot = string.Empty;
        [SerializeField] private string modelName = "Kimodo-SOMA-RP-v1";
        [FormerlySerializedAs("highVram")]
        [SerializeField] private KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPerformance;
        [SerializeField] private bool forceCpu;
        [SerializeField][Min(1f)] private float startupTimeoutMinutes = 30f;

        [Header("Generation")]
        [SerializeField] private string defaultPrompt = IdlePrompt;
        [SerializeField][Min(1)] private int generationFrames = 150;
        [SerializeField][Min(1)] private int diffusionSteps = 100;
        [SerializeField, Range(0f, 4f)] private float textWeight = 1f;
        [SerializeField] private bool randomSeed = true;
        [SerializeField] private int fixedSeed = 42;
        [SerializeField][Min(0.1f)] private float segmentIntervalSeconds = 5f;
        [FormerlySerializedAs("ardyPlaybackDelaySeconds")]
        [FormerlySerializedAs("ardySafeIntervalSeconds")]
        [SerializeField][Min(0.2f), Tooltip("Request more ARDY motion when this much playable animation remains.")]
        private float ardyPlaybackReserveSeconds = 1f;
        [SerializeField, Tooltip("Let the ARDY backend adapt the playback reserve from measured response time.")]
        private bool ardyAdaptivePlaybackReserve = true;
        [SerializeField][Min(0f), Tooltip("0 selects adaptive ARDY History Crop.")]
        private float ardyHistoryCropSeconds;
        [SerializeField][Min(0f), Tooltip("0 uses the selected ARDY profile maximum.")]
        private float ardyFutureCropSeconds;
        [SerializeField, Tooltip("Expand ARDY Root2D waypoints into the official dense per-frame root path.")]
        private bool ardyDenseRootPath;
        [SerializeField] private bool loopHint = true;
        [SerializeField] private KimodoSegmentOverlapHeadSettings segmentOverlapHeadSettings = new KimodoSegmentOverlapHeadSettings();
        [SerializeField] private bool allowPartialJoints;
        [SerializeField] private KimodoSegmentTrimTrailSettings segmentTrimTrailSettings = new KimodoSegmentTrimTrailSettings();

        [Header("Foot IK Targets")]
        [SerializeField] private bool driveFootIkTargets = true;
        [SerializeField] private string leftFootIkTargetName = "LeftFootIK";
        [SerializeField] private string rightFootIkTargetName = "RightFootIK";

        [Header("Debug")]
        [SerializeField, Tooltip("Editor only. Show the model's profile-skeleton FBX driven by the current source pose.")]
        private bool drawDebugSkeleton;
        [SerializeField] private bool verboseLogging = true;

        private const string FullBodyConstraintType = "fullbody";
        private const string LeftHandConstraintType = "left-hand";
        private const string RightHandConstraintType = "right-hand";
        private const string LeftFootConstraintType = "left-foot";
        private const string RightFootConstraintType = "right-foot";
        private const string Root2DConstraintType = "root2d";
        private const string Root2DTargetConstraintType = "root2d_target";
        private const string Root2DTargetArdyOnlyMessage =
            "Root2D Target is an automatic ARDY-only navigation constraint. Use SetRoot2D for a single endpoint constraint.";
        private const string IdlePrompt = "idle";
        private const string KimodoFolderName = "NvlabKimodoQuickServer~";
        private const float MinGenerationDurationSeconds = 1f;
        private const float MaxGenerationDurationSeconds = 10f;

        private readonly KimodoRuntimeGenerationSession generationSession =
            new KimodoRuntimeGenerationSession();
        private string promptDraft;
        private string statusMessage = "Idle.";
        private readonly KimodoRuntimeConstraintBuffer constraintBuffer = new KimodoRuntimeConstraintBuffer();
        private readonly List<Animator> resolvedTargetAnimatorBuffer = new List<Animator>();
        private KimodoBridgeService bridgeService;
        private KimodoRuntimeMotionPlayer motionPlayer;

        public string StatusMessage => statusMessage;
        public bool IsRunning => generationSession.Running;
        public KimodoSegmentTrimTrailSettings SegmentTrimTrailSettings => segmentTrimTrailSettings;
        public KimodoSegmentOverlapHeadSettings SegmentOverlapHeadSettings => segmentOverlapHeadSettings;
        public event Action<KimodoRuntimeSegmentReport> SegmentReady;
        public event Action<KimodoRuntimeSegmentReport> SegmentStarted;
        public event Action<KimodoRuntimeSegmentReport> SegmentCompleted;
        public bool FootIkEnabled
        {
            get => driveFootIkTargets;
            set => driveFootIkTargets = value;
        }
        public bool DrawDebugSkeleton
        {
            get => drawDebugSkeleton;
            set => drawDebugSkeleton = value;
        }
        internal string DebugModelName => modelName;
        internal Transform DebugProfileSkeletonRoot => motionPlayer?.DebugProfileSkeletonRoot;

        private void Reset()
        {
            Animator animator = GetComponent<Animator>();
            if (animator != null && targetHumanoidAnimators.Count == 0)
            {
                targetHumanoidAnimators.Add(animator);
            }
        }

        private void Awake()
        {
            bridgeService = KimodoBridgeService.CreateOwned();
            motionPlayer = new KimodoRuntimeMotionPlayer();
            promptDraft = ResolveInitialPrompt();
            SyncGenerationDurationFromCurrentSettings();
            CaptureAppliedRuntimeSettings();
        }

        private void OnEnable()
        {
            EnsurePromptDraftInitialized();
            _ = StartRuntimeAsync();
        }

        private void OnDisable()
        {
            _ = StopRuntimeAsync();
        }

        private void OnDestroy()
        {
            motionPlayer?.Stop();
            generationSession.Dispose();
            bridgeService?.Dispose();
            bridgeService = null;
        }

        private void Update()
        {
            if (motionPlayer == null)
            {
                return;
            }

            motionPlayer.Update(
                Time.deltaTime,
                modelName,
                ResolveTargetAnimators(),
                allowPartialJoints,
                driveFootIkTargets,
                leftFootIkTargetName,
                rightFootIkTargetName,
                verboseLogging,
                out KimodoRuntimeGeneratedSegment startedSegment,
                out KimodoRuntimeGeneratedSegment completedSegment,
                out string playbackError);

            if (!string.IsNullOrWhiteSpace(playbackError))
            {
                UpdateStatus($"Playback failed: {playbackError}");
            }

            if (startedSegment == null)
            {
                if (completedSegment != null)
                {
                    SegmentCompleted?.Invoke(CreateSegmentReport(completedSegment));
                }

                return;
            }

            if (loopHint && !KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                constraintBuffer.SetOverlapPoses(startedSegment.ConstraintOverlapPoses);
            }
            else
            {
                constraintBuffer.ClearOverlapPoses();
            }

            UpdateStatus($"Playing segment {startedSegment.Index}.");
            SegmentStarted?.Invoke(CreateSegmentReport(startedSegment));

            if (completedSegment != null)
            {
                SegmentCompleted?.Invoke(CreateSegmentReport(completedSegment));
            }

        }

        private void LateUpdate()
        {
            motionPlayer?.ApplyLateRetargetCorrection(driveFootIkTargets);
        }

        public void SetPrompt(string prompt)
        {
            SetPromptInternal(prompt);
        }

        public void SetAnimationPrompt(string prompt)
        {
            SetPromptInternal(prompt);
        }

        public string GetAnimationPrompt(out bool isIdle)
        {
            return GetCurrentPromptInternal(out isIdle);
        }

        public string GetCurrentPrompt(out bool isIdle)
        {
            return GetCurrentPromptInternal(out isIdle);
        }

        public void SetAnimationDurationSeconds(float seconds)
        {
            ApplyGenerationDurationSeconds(seconds);
        }

        public void ApplyGenerationSettings()
        {
            _ = ApplyGenerationSettingsAsync();
        }

        private async Task ApplyGenerationSettingsAsync()
        {
            EnsurePromptDraftInitialized();
            promptDraft = string.IsNullOrWhiteSpace(defaultPrompt) ? IdlePrompt : defaultPrompt.Trim();
            if (!KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                ApplyGenerationDurationSeconds(generationFrames / KimodoPlayableClip.FIXED_FRAME_RATE);
            }
            else
            {
                generationSession.ArdyPromptDirty = true;
                generationSession.ArdySettingsDirty = true;
            }

            if (RequiresRuntimeSessionRestart() &&
                generationSession.Running &&
                generationSession.LifetimeCts != null &&
                !generationSession.LifetimeCts.IsCancellationRequested)
            {
                UpdateStatus("Runtime settings changed. Restarting generation session.");
                await ResetMotionAsync();
                return;
            }

            CaptureAppliedRuntimeSettings();
            await RefreshUpcomingGenerationAsync(
                "Generation settings applied.",
                "Generation settings applied. Waiting for current generation to finish.",
                "Generation settings applied. Generating fresh segment.");
        }

        public float GetAnimationDurationSeconds()
        {
            return ResolveGenerationDurationSeconds();
        }

        public void SetLeftHandConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            StageEndEffectorConstraintInternal(
                "LeftHand constraint",
                LeftHandConstraintType,
                "LeftHand",
                worldX,
                worldY,
                worldZ,
                duration);
        }

        public void SetRightHandConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            StageEndEffectorConstraintInternal(
                "RightHand constraint",
                RightHandConstraintType,
                "RightHand",
                worldX,
                worldY,
                worldZ,
                duration);
        }

        public void SetLeftFootConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            StageEndEffectorConstraintInternal(
                "LeftFoot constraint",
                LeftFootConstraintType,
                "LeftFoot",
                worldX,
                worldY,
                worldZ,
                duration);
        }

        public void SetRightFootConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            StageEndEffectorConstraintInternal(
                "RightFoot constraint",
                RightFootConstraintType,
                "RightFoot",
                worldX,
                worldY,
                worldZ,
                duration);
        }

        /// <summary>Stages an absolute Unity world-space Root2D target.</summary>
        public void SetRoot2D(float worldX, float worldZ, float duration = 1f)
        {
            StageRoot2DWorldConstraintInternal(worldX, worldZ, duration, null);
        }

        /// <summary>Stages an absolute Unity world-space Root2D target and heading.</summary>
        public void SetRoot2D(
            float worldX,
            float worldZ,
            float worldHeadingX,
            float worldHeadingZ,
            float duration = 1f)
        {
            StageRoot2DWorldConstraintInternal(
                worldX,
                worldZ,
                duration,
                NormalizeHeading(new Vector2(worldHeadingX, worldHeadingZ)));
        }

        public void SetRoot2DTarget(
            float worldX,
            float worldZ,
            float maxSpeedMetersPerSecond = 1.25f,
            float maxAccelerationMetersPerSecond2 = 1.5f,
            float arrivalThresholdMeters = 0.1f,
            bool includeHeading = true)
        {
            if (!KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                UpdateStatus(Root2DTargetArdyOnlyMessage);
                return;
            }

            if (!TryCreateRoot2DWorldConstraintSample(
                    worldX,
                    worldZ,
                    0f,
                    null,
                    out KimodoMarkerSampleResult sample,
                    out string error))
            {
                UpdateStatus(error);
                return;
            }

            sample.constraintType = Root2DTargetConstraintType;
            sample.rootTargetMaxSpeed = Mathf.Max(0.01f, maxSpeedMetersPerSecond);
            sample.rootTargetMaxAcceleration = Mathf.Max(0.01f, maxAccelerationMetersPerSecond2);
            sample.rootTargetArrivalThreshold = Mathf.Max(0f, arrivalThresholdMeters);
            sample.rootTargetIncludeHeading = includeHeading;
            StageConstraintSample(sample);
            UpdateStatus($"Root2D world target staged at ({worldX:0.###}, {worldZ:0.###}).");
        }

        public string QueuePromptedRoot2D(
            string prompt,
            float worldX,
            float worldZ,
            float generationDurationSeconds)
        {
            ApplyGenerationDurationSeconds(generationDurationSeconds);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                promptDraft = prompt.Trim();
            }

            string stageResult = StageRoot2DWorldConstraintInternal(
                worldX,
                worldZ,
                generationDurationSeconds,
                null);
            if (stageResult.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) ||
                stageResult.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            {
                return stageResult;
            }

            ApplyStagedConstraints();
            return stageResult;
        }

        public void ApplyStagedConstraints()
        {
            ApplyStagedConstraintsInternal(
                "Constraints queued.",
                "Constraints queued. Waiting for current generation to finish.",
                "Constraints queued. Generating constrained segment.");
        }

        public void ClearConstraints()
        {
            constraintBuffer.ClearUserConstraints();
            generationSession.ArdyConstraintsDirty = true;
            _ = RefreshUpcomingGenerationAsync(
                "Constraints cleared.",
                "Constraints cleared. Waiting for current generation to finish.",
                "Constraints cleared. Regenerating future motion.");
        }

        public Vector3 GetPosition()
        {
            return GetCurrentPositionInternal();
        }

        public async Task ResetMotionAsync()
        {
            promptDraft = ResolveInitialPrompt();
            constraintBuffer.ClearAll();
            generationSession.SegmentIndex = 0;
            generationSession.GenerationRequestVersion++;
            generationSession.LastGenerationWaitStatusSegment = -1;
            generationSession.GenerationBlocked = true;

            if (!generationSession.Running || generationSession.LifetimeCts == null || generationSession.LifetimeCts.IsCancellationRequested)
            {
                generationSession.GenerationBlocked = false;
                UpdateStatus("Prompt reset.");
                return;
            }

            if (generationSession.GenerationInFlight)
            {
                UpdateStatus("Prompt reset. Waiting for current generation to finish.");
                TryCancelActiveGeneration();
                await WaitForGenerationSlotAsync(generationSession.LifetimeCts.Token);
            }

            motionPlayer.Stop();
            motionPlayer.ResetCompletionState();
            motionPlayer.ClearQueue();
            if (bridgeService != null && !bridgeService.IsDisposed)
            {
                await bridgeService.StopAsync(CancellationToken.None);
                bridgeService.Dispose();
            }
            bridgeService = KimodoBridgeService.CreateOwned();
            ResetArdySessionState();
            CaptureAppliedRuntimeSettings();
            generationSession.GenerationBlocked = false;
            UpdateStatus("Prompt reset. Generating fresh segment.");
            await GenerateNextSegmentAsync(generationSession.LifetimeCts.Token);
        }

        private async Task StartRuntimeAsync()
        {
            if (generationSession.Running || generationSession.StartRequested)
            {
                return;
            }

            generationSession.StartRequested = true;
            try
            {
                if (!ValidateConfiguration(out string error))
                {
                    UpdateStatus(error);
                    Debug.LogError($"[KimodoRuntimeMotionDriver] {error}", this);
                    return;
                }

                generationSession.LifetimeCts?.Cancel();
                generationSession.LifetimeCts?.Dispose();
                generationSession.LifetimeCts = new CancellationTokenSource();
                if (bridgeService == null || bridgeService.IsDisposed)
                {
                    bridgeService = KimodoBridgeService.CreateOwned();
                }

                generationSession.SegmentIndex = 0;
                generationSession.GenerationInFlight = false;
                generationSession.GenerationRequestVersion = 0;
                generationSession.GenerationBlocked = false;
                generationSession.LastGenerationWaitStatusSegment = -1;
                constraintBuffer.ClearAll();
                motionPlayer.Stop();
                motionPlayer.ResetCompletionState();
                motionPlayer.ClearQueue();
                ResetArdySessionState();
                CaptureAppliedRuntimeSettings();

                generationSession.Running = true;
                generationSession.SchedulerTask = RunSchedulerLoopAsync(generationSession.LifetimeCts.Token);
                UpdateStatus("Generator active.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
                UpdateStatus($"Start failed: {ex.Message}");
                await StopRuntimeAsync();
            }
            finally
            {
                generationSession.StartRequested = false;
            }
        }

        private async Task StopRuntimeAsync()
        {
            generationSession.Running = false;

            CancellationTokenSource cts = generationSession.LifetimeCts;
            generationSession.LifetimeCts = null;
            CancellationTokenSource generationCts = generationSession.ActiveGenerationCts;
            generationSession.ActiveGenerationCts = null;
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                }
            }

            if (generationCts != null)
            {
                try
                {
                    generationCts.Cancel();
                }
                catch
                {
                }
            }

            Task task = generationSession.SchedulerTask;
            generationSession.SchedulerTask = null;
            if (task != null)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[KimodoRuntimeMotionDriver] Scheduler stop observed exception: {ex.Message}", this);
                }
            }

            cts?.Dispose();
            generationCts?.Dispose();
            generationSession.GenerationInFlight = false;
            generationSession.LastGenerationWaitStatusSegment = -1;
            constraintBuffer.ClearAll();
            motionPlayer.Stop();
            motionPlayer.ResetCompletionState();
            motionPlayer.ClearQueue();
            ResetArdySessionState();
            if (bridgeService != null && !bridgeService.IsDisposed)
            {
                await bridgeService.StopAsync(CancellationToken.None);
            }
            UpdateStatus("Stopped.");
        }

        private async Task RunSchedulerLoopAsync(CancellationToken token)
        {
            try
            {
                await GenerateNextSegmentAsync(token);

                while (!token.IsCancellationRequested)
                {
                    MaybeQueueNextGeneration(token);
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
                UpdateStatus($"Scheduler failed: {ex.Message}");
                generationSession.Running = false;
            }
        }

        private void MaybeQueueNextGeneration(CancellationToken token)
        {
            if (!generationSession.Running || generationSession.GenerationInFlight || generationSession.GenerationBlocked)
            {
                return;
            }

            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (isArdy && !ShouldRequestArdyGeneration(
                    motionPlayer.BufferedDurationSeconds,
                    generationSession.ArdyEffectivePlaybackReserveSeconds,
                    generationSession.ArdyRefreshPending))
            {
                return;
            }

            if (!isArdy && motionPlayer.QueuedSegmentCount > 0)
            {
                return;
            }

            if (isArdy)
            {
                _ = GenerateNextSegmentAsync(token);
                return;
            }

            if (!CanStartGenerationForCurrentSegment(out int waitingForSegment))
            {
                if (generationSession.LastGenerationWaitStatusSegment != generationSession.SegmentIndex)
                {
                    UpdateStatus($"Waiting for segment {waitingForSegment} to finish before generating segment {generationSession.SegmentIndex}.");
                    generationSession.LastGenerationWaitStatusSegment = generationSession.SegmentIndex;
                }

                return;
            }

            generationSession.LastGenerationWaitStatusSegment = -1;
            _ = GenerateNextSegmentAsync(token);
        }

        private bool CanStartGenerationForCurrentSegment(out int waitingForSegment)
        {
            int requiredCompletedSegment = generationSession.SegmentIndex - 2;
            waitingForSegment = requiredCompletedSegment;
            if (requiredCompletedSegment < 0)
            {
                return true;
            }

            return motionPlayer.LastCompletedSegmentIndex >= requiredCompletedSegment;
        }

        private async Task GenerateNextSegmentAsync(CancellationToken token)
        {
            if (generationSession.GenerationInFlight)
            {
                return;
            }

            generationSession.GenerationInFlight = true;
            int requestVersion = generationSession.GenerationRequestVersion;
            int requestSegmentIndex = generationSession.SegmentIndex;
            CancellationTokenSource generationCts = null;
            try
            {
                generationCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                generationSession.ActiveGenerationCts = generationCts;
                CancellationToken generationToken = generationCts.Token;

                string prompt = ResolvePrompt();
                string constraintsJson = BuildNextConstraintsJson();
                bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile ardyProfile);
                bool sendPrompt = !isArdy || !generationSession.ArdySessionStarted || generationSession.ArdyPromptDirty;
                bool sendConstraints = !isArdy || !generationSession.ArdySessionStarted || generationSession.ArdyConstraintsDirty;
                bool sendSettings = isArdy && (!generationSession.ArdySessionStarted || generationSession.ArdySettingsDirty);
                if (isArdy)
                {
                    generationSession.ArdyRefreshPending = false;
                }
                int resolvedRequestSeed = isArdy && generationSession.ArdyStreamResolvedSeed.HasValue
                    ? generationSession.ArdyStreamResolvedSeed.Value
                    : (randomSeed ? (Guid.NewGuid().GetHashCode() & int.MaxValue) : fixedSeed);
                if (isArdy)
                {
                    generationSession.ArdyStreamResolvedSeed = resolvedRequestSeed;
                }
                var request = new KimodoGenerationRequestDto
                {
                    ardy_session_update_only = isArdy && generationSession.ArdySessionStarted && !sendSettings,
                    prompt = sendPrompt ? prompt : null,
                    duration = isArdy ? (float?)null : ResolveGenerationDurationSeconds(),
                    seed = resolvedRequestSeed,
                    steps = Mathf.Clamp(diffusionSteps, 1, isArdy ? ardyProfile.MaxDiffusionSteps : 1000),
                    text_weight = Mathf.Clamp(textWeight, 0f, 4f),
                    constraints_json = sendConstraints
                        ? (isArdy && string.IsNullOrWhiteSpace(constraintsJson) ? "[]" : constraintsJson)
                        : null,
                    transition_duration = 0f,
                    model = modelName,
                    text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    simulate_free_vram_gb = forceCpu ? 0 : (int?)null,
                    models_root = string.IsNullOrWhiteSpace(modelsRoot) ? string.Empty : Path.GetFullPath(modelsRoot),
                    force_hf_download = false,
                    owner_pid = System.Diagnostics.Process.GetCurrentProcess().Id
                };
                if (isArdy)
                {
                    request.time_as_double = motionPlayer.PlaybackTimeAsDouble;
                    if (sendSettings)
                    {
                        request.ardy_history_crop_seconds = ardyHistoryCropSeconds;
                        request.ardy_future_crop_seconds = ardyFutureCropSeconds > 0f
                            ? ardyFutureCropSeconds
                            : (double?)null;
                        request.ardy_playback_reserve_seconds = Mathf.Max(0.2f, ardyPlaybackReserveSeconds);
                        request.ardy_adaptive_playback_reserve = ardyAdaptivePlaybackReserve;
                    }
                }

                OnProgress($"Generating segment {requestSegmentIndex}...");
                KimodoBridgeGenerationResult bridgeResult =
                    await bridgeService.GenerateAsync(request, OnProgress, generationToken);
                bool staleRequest = requestVersion != generationSession.GenerationRequestVersion || generationToken.IsCancellationRequested;
                if (ShouldDiscardCompletedGenerationResult(isArdy, staleRequest, token.IsCancellationRequested))
                {
                    if (verboseLogging)
                    {
                        Debug.Log($"[KimodoRuntimeMotionDriver] Discard stale segment {requestSegmentIndex} generation result.", this);
                    }

                    return;
                }
                if (staleRequest && verboseLogging)
                {
                    Debug.Log(
                        $"[KimodoRuntimeMotionDriver] Append committed ARDY segment {requestSegmentIndex} before applying the pending stream update.",
                        this);
                }
                if (isArdy)
                {
                    ValidateArdyResult(bridgeResult, ardyProfile, resolvedRequestSeed);
                    if (bridgeResult.ArdyPlaybackReserveSeconds.HasValue)
                    {
                        generationSession.ArdyEffectivePlaybackReserveSeconds = Mathf.Max(
                            0.2f,
                            (float)bridgeResult.ArdyPlaybackReserveSeconds.Value);
                    }
                    if (bridgeResult.MotionData == null)
                    {
                        generationSession.ArdySessionStarted = true;
                        if (!staleRequest)
                        {
                            if (sendPrompt) generationSession.ArdyPromptDirty = false;
                            if (sendConstraints) generationSession.ArdyConstraintsDirty = false;
                            if (sendSettings) generationSession.ArdySettingsDirty = false;
                        }
                        UpdateStatus("ARDY cursor synchronized; no new KMB frames were required.");
                        return;
                    }
                }

                KimodoRawMotionMetadata metadata;
                if (isArdy)
                {
                    if (!bridgeResult.MotionData.TryReadUnityRootPosition(0, out Vector3 firstRootPosition) ||
                        !bridgeResult.MotionData.TryReadUnityRootPosition(
                            bridgeResult.MotionData.FrameCount - 1,
                            out Vector3 lastRootPosition))
                    {
                        throw new InvalidOperationException("Failed to read ARDY KMB root positions.");
                    }
                    metadata = new KimodoRawMotionMetadata(
                        bridgeResult.MotionData,
                        firstRootPosition,
                        lastRootPosition,
                        null);
                }
                else
                {
                    metadata = await Task.Run(() =>
                    {
                        var generationResult = new KimodoGenerationResultDto
                        {
                            motionJsonCompact = bridgeResult?.MotionJsonCompact,
                            motionData = bridgeResult?.MotionData,
                            motionFormat = bridgeResult?.MotionFormat,
                            rawStatus = bridgeResult?.RawStatus,
                            message = bridgeResult?.Message
                        };

                        if (!KimodoRawMotionUtility.TryAnalyzeGenerationResult(
                                generationResult,
                                modelName,
                                out KimodoRawMotionMetadata parsedMetadata,
                                out string parseError,
                                FullBodyConstraintType,
                                0.0,
                                allowPartialJoints))
                        {
                            throw new InvalidOperationException(parseError);
                        }

                        return parsedMetadata;
                    }, generationToken);
                }

                int effectiveLastFrameIndex = isArdy
                    ? metadata.Motion.FrameCount - 1
                    : KimodoRuntimeSegmentAnalysisUtility.ResolveEffectiveLastFrameIndex(
                        metadata.Motion,
                        segmentTrimTrailSettings);
                if (!metadata.Motion.TryReadUnityRootPosition(effectiveLastFrameIndex, out Vector3 effectiveLastRootPosition))
                {
                    throw new InvalidOperationException(
                        $"Failed to read effective tail root position for frame {effectiveLastFrameIndex}.");
                }

                KimodoMarkerSampleResult effectiveTailPose = null;
                if (!isArdy && !KimodoRawMotionUtility.TryExtractMarkerSample(
                    metadata.Motion,
                    modelName,
                    effectiveLastFrameIndex,
                    out effectiveTailPose,
                    out string tailError,
                    FullBodyConstraintType,
                    0.0,
                    allowPartialJoints))
                {
                    throw new InvalidOperationException(tailError);
                }

                List<KimodoMarkerSampleResult> constraintOverlapPoses = isArdy
                    ? new List<KimodoMarkerSampleResult>()
                    : KimodoRuntimeSegmentAnalysisUtility.BuildConstraintOverlapPoses(
                        metadata.Motion,
                        modelName,
                        effectiveLastFrameIndex,
                        segmentOverlapHeadSettings,
                        allowPartialJoints);
                if (!isArdy && constraintOverlapPoses.Count == 0)
                {
                    KimodoMarkerSampleResult fallbackPose = effectiveTailPose.Clone();
                    fallbackPose.sampleTime = 0.0;
                    constraintOverlapPoses.Add(fallbackPose);
                }

                var generatedSegment = new KimodoRuntimeGeneratedSegment
                {
                    Index = requestSegmentIndex,
                    PromptText = prompt,
                    Motion = metadata.Motion,
                    ConstraintOverlapPoses = constraintOverlapPoses,
                    FirstRootPosition = metadata.FirstRootPosition,
                    LastRootPosition = effectiveLastRootPosition,
                    WorldAccumulatedOffset = Vector3.zero,
                    EffectiveLastFrameIndex = effectiveLastFrameIndex,
                    EffectiveLastFrameTimeSeconds = metadata.Motion.FrameRate > 0f
                        ? (isArdy ? metadata.Motion.FrameCount : effectiveLastFrameIndex) / metadata.Motion.FrameRate
                        : metadata.Motion.LastFrameTimeSeconds,
                    MotionBytes = bridgeResult?.MotionBytes,
                    MotionRepFingerprint = bridgeResult?.MotionRepFingerprint ?? string.Empty,
                    ResolvedSeed = bridgeResult?.ResolvedSeed,
                    UseRawRootPosition = isArdy
                };
                if (isArdy)
                {
                    if (!motionPlayer.ReplaceArdy(
                            generatedSegment,
                            bridgeResult.StartFrame,
                            verboseLogging,
                            out string appendError))
                    {
                        throw new InvalidOperationException(appendError);
                    }
                    generationSession.ArdySessionStarted = true;
                    if (!staleRequest)
                    {
                        if (sendPrompt) generationSession.ArdyPromptDirty = false;
                        if (sendConstraints) generationSession.ArdyConstraintsDirty = false;
                        if (sendSettings) generationSession.ArdySettingsDirty = false;
                    }
                }
                else
                {
                    motionPlayer.Enqueue(generatedSegment, verboseLogging);
                }
                SegmentReady?.Invoke(CreateSegmentReport(new KimodoRuntimeGeneratedSegment
                {
                    Index = requestSegmentIndex,
                    PromptText = prompt,
                    Motion = metadata.Motion,
                    FirstRootPosition = metadata.FirstRootPosition,
                    LastRootPosition = effectiveLastRootPosition,
                    EffectiveLastFrameIndex = effectiveLastFrameIndex,
                    EffectiveLastFrameTimeSeconds = metadata.Motion.FrameRate > 0f
                        ? (isArdy ? metadata.Motion.FrameCount : effectiveLastFrameIndex) / metadata.Motion.FrameRate
                        : metadata.Motion.LastFrameTimeSeconds
                }));

                constraintBuffer.CompleteGeneration(isArdy);
                generationSession.SegmentIndex = requestSegmentIndex + 1;
                UpdateStatus($"Segment {requestSegmentIndex} ready.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
                UpdateStatus($"Generate failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(generationSession.ActiveGenerationCts, generationCts))
                {
                    generationSession.ActiveGenerationCts = null;
                }

                generationCts?.Dispose();
                generationSession.GenerationInFlight = false;
                if (generationSession.Running && !generationSession.GenerationBlocked && generationSession.ArdyRefreshPending &&
                    generationSession.LifetimeCts != null && !generationSession.LifetimeCts.IsCancellationRequested)
                {
                    _ = GenerateNextSegmentAsync(generationSession.LifetimeCts.Token);
                }
            }
        }

        private List<KimodoMarkerSampleResult> BuildActiveGenerationConstraints()
        {
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            return constraintBuffer.BuildActive(
                isArdy,
                isArdy ? motionPlayer.PlaybackTimeAsDouble : 0.0,
                includeOverlap: loopHint && !isArdy,
                ResolveGenerationDurationSeconds(),
                FullBodyConstraintType,
                Root2DTargetConstraintType);
        }

        private string BuildNextConstraintsJson()
        {
            List<KimodoMarkerSampleResult> activeConstraints = BuildActiveGenerationConstraints();
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile);
            if (activeConstraints.Count == 0)
            {
                return string.Empty;
            }

            string futureConstraints = KimodoConstraintJsonExporter.ToConstraintsJson(
                activeConstraints,
                0.0,
                ResolveGenerationDurationSeconds(),
                isArdy ? profile.SourceFps : KimodoPlayableClip.FIXED_FRAME_RATE,
                denseRootPath: isArdy && ardyDenseRootPath);
            return futureConstraints;
        }

        private async Task RefreshUpcomingGenerationAsync(
            string inactiveStatus,
            string waitingStatus,
            string generatingStatus)
        {
            generationSession.LastGenerationWaitStatusSegment = -1;
            generationSession.GenerationRequestVersion++;
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (isArdy)
            {
                generationSession.ArdyRefreshPending = true;
            }
            if (!isArdy)
            {
                int clearedQueuedSegmentCount = motionPlayer.QueuedSegmentCount;
                motionPlayer.ClearQueue();
                RewindSegmentIndexAfterQueueInvalidation(clearedQueuedSegmentCount);
            }

            if (!generationSession.Running || generationSession.LifetimeCts == null || generationSession.LifetimeCts.IsCancellationRequested)
            {
                UpdateStatus(inactiveStatus);
                return;
            }

            if (generationSession.GenerationInFlight)
            {
                UpdateStatus(waitingStatus);
                if (isArdy)
                {
                    return;
                }
                if (ShouldCancelActiveGenerationForRefresh(isArdy))
                {
                    TryCancelActiveGeneration();
                }
                await WaitForGenerationSlotAsync(generationSession.LifetimeCts.Token);
                if (!generationSession.Running || generationSession.LifetimeCts == null || generationSession.LifetimeCts.IsCancellationRequested)
                {
                    return;
                }
            }

            UpdateStatus(generatingStatus);
            await GenerateNextSegmentAsync(generationSession.LifetimeCts.Token);
        }

        internal static bool ShouldCancelActiveGenerationForRefresh(bool isArdy)
        {
            return !isArdy;
        }

        private void TryCancelActiveGeneration()
        {
            CancellationTokenSource generationCts = generationSession.ActiveGenerationCts;
            if (generationCts == null)
            {
                return;
            }

            try
            {
                generationCts.Cancel();
            }
            catch
            {
            }
        }

        private async Task WaitForGenerationSlotAsync(CancellationToken token)
        {
            while (generationSession.GenerationInFlight && !token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
            }
        }

        private void RewindSegmentIndexAfterQueueInvalidation(int clearedQueuedSegmentCount)
        {
            if (clearedQueuedSegmentCount <= 0)
            {
                return;
            }

            int minSegmentIndex = Mathf.Max(0, motionPlayer.LastCompletedSegmentIndex + 1);
            generationSession.SegmentIndex = Mathf.Max(minSegmentIndex, generationSession.SegmentIndex - clearedQueuedSegmentCount);
        }

        private void ResetArdySessionState()
        {
            generationSession.ResetArdy(ardyPlaybackReserveSeconds);
        }

        private bool RequiresRuntimeSessionRestart()
        {
            if (!generationSession.AppliedRuntimeSettingsInitialized)
            {
                return false;
            }

            string currentModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            string currentModelsRoot = (modelsRoot ?? string.Empty).Trim();
            bool targetChanged = generationSession.AppliedTargetSignature != ComputeTargetSignature();
            bool runtimeSignatureChanged =
                !string.Equals(generationSession.AppliedModelName, currentModelName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(generationSession.AppliedModelsRoot, currentModelsRoot, StringComparison.Ordinal) ||
                generationSession.AppliedTextEncoderMode != textEncoderMode ||
                generationSession.AppliedForceCpu != forceCpu;
            return RequiresNewGenerationSession(
                targetChanged,
                runtimeSignatureChanged,
                KimodoMotionModelProfiles.TryGetArdy(currentModelName, out _),
                generationSession.AppliedRandomSeed != randomSeed,
                !randomSeed && generationSession.AppliedFixedSeed != fixedSeed);
        }

        internal static bool RequiresNewGenerationSession(
            bool targetChanged,
            bool runtimeSignatureChanged,
            bool isArdy,
            bool randomSeedModeChanged,
            bool deterministicSeedChanged)
        {
            return targetChanged ||
                runtimeSignatureChanged ||
                (isArdy && (randomSeedModeChanged || deterministicSeedChanged));
        }

        private void CaptureAppliedRuntimeSettings()
        {
            generationSession.AppliedTargetSignature = ComputeTargetSignature();
            generationSession.AppliedModelsRoot = (modelsRoot ?? string.Empty).Trim();
            generationSession.AppliedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            generationSession.AppliedTextEncoderMode = textEncoderMode;
            generationSession.AppliedForceCpu = forceCpu;
            generationSession.AppliedRandomSeed = randomSeed;
            generationSession.AppliedFixedSeed = fixedSeed;
            generationSession.AppliedRuntimeSettingsInitialized = true;
        }

        internal static bool ShouldRequestArdyGeneration(
            float bufferedDurationSeconds,
            float playbackReserveSeconds,
            bool refreshPending)
        {
            return refreshPending || bufferedDurationSeconds <= Mathf.Max(0.2f, playbackReserveSeconds);
        }

        internal static void ValidateArdyResult(
            KimodoBridgeGenerationResult result,
            KimodoMotionModelProfile profile,
            int requestedSeed)
        {
            if (result == null ||
                !string.Equals(result.MotionFormat, "kmb_v1", StringComparison.OrdinalIgnoreCase) ||
                result.EndFrameExclusive < result.StartFrame)
            {
                throw new InvalidOperationException("ARDY result metadata is invalid.");
            }
            int expectedFrames = result.EndFrameExclusive - result.StartFrame;
            if (expectedFrames == 0)
            {
                if (result.MotionData != null || result.MotionBytes == null || result.MotionBytes.Length != 0)
                {
                    throw new InvalidOperationException("Empty ARDY result contains unexpected KMB data.");
                }
            }
            else if (result.MotionData == null ||
                result.MotionBytes == null ||
                result.MotionBytes.Length == 0 ||
                result.MotionData.FrameCount != expectedFrames ||
                result.MotionData.JointCount != profile.JointCount ||
                Mathf.Abs(result.MotionData.FrameRate - profile.SourceFps) > 1e-4f)
            {
                throw new InvalidOperationException("ARDY KMB frame count, FPS, or rig metadata does not match its response.");
            }
            if (!string.Equals(
                    result.MotionRepFingerprint,
                    profile.MotionRepFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ARDY result motion representation fingerprint mismatch.");
            }
            if (!result.ResolvedSeed.HasValue || result.ResolvedSeed.Value != requestedSeed)
            {
                throw new InvalidOperationException("ARDY result resolved_seed does not match the requested seed.");
            }
        }

        internal static bool ShouldDiscardCompletedGenerationResult(
            bool isArdy,
            bool staleRequest,
            bool lifetimeCancelled)
        {
            return lifetimeCancelled || (!isArdy && staleRequest);
        }

        private string SetPromptInternal(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return GetCurrentPromptInternal(out bool _);
            }

            promptDraft = prompt.Trim();
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                generationSession.ArdyPromptDirty = true;
            }
            _ = RefreshUpcomingGenerationAsync(
                $"Prompt updated: {promptDraft}",
                $"Prompt updated: {promptDraft}. Waiting for current generation to finish.",
                $"Prompt updated: {promptDraft}. Generating fresh segment.");
            return promptDraft;
        }

        private string StageEndEffectorConstraintInternal(
            string label,
            string constraintType,
            string jointName,
            float x,
            float y,
            float z,
            float durationSeconds)
        {
            if (!TryCreateShiftedConstraintSample(
                    constraintType,
                    jointName,
                    new Vector3(x, y, z),
                    durationSeconds,
                    out KimodoMarkerSampleResult sample,
                    out string error))
            {
                UpdateStatus(error);
                return error;
            }

            StageConstraintSample(sample);
            string result = $"{label} staged at {FormatVector3(new Vector3(x, y, z))}.";
            UpdateStatus(result);
            return result;
        }

        private string StageRoot2DWorldConstraintInternal(
            float worldX,
            float worldZ,
            float durationSeconds,
            Vector2? worldHeading)
        {
            if (!TryCreateRoot2DWorldConstraintSample(
                    worldX,
                    worldZ,
                    durationSeconds,
                    worldHeading,
                    out KimodoMarkerSampleResult sample,
                    out string error))
            {
                UpdateStatus(error);
                return error;
            }

            StageConstraintSample(sample);
            string result = $"Root2D world target staged at ({worldX:0.###}, {worldZ:0.###}).";
            UpdateStatus(result);
            return result;
        }

        private void ApplyStagedConstraintsInternal(
            string inactiveStatus,
            string waitingStatus,
            string generatingStatus)
        {
            if (!constraintBuffer.CommitStaged())
            {
                return;
            }

            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                generationSession.ArdyConstraintsDirty = true;
            }
            _ = RefreshUpcomingGenerationAsync(inactiveStatus, waitingStatus, generatingStatus);
        }

        private void StageConstraintSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null)
            {
                return;
            }

            constraintBuffer.Stage(
                sample,
                KimodoMotionModelProfiles.TryGetArdy(modelName, out _)
                    ? motionPlayer.PlaybackTimeAsDouble
                    : 0.0);
        }

        private string GetCurrentPromptInternal(out bool isIdle)
        {
            string currentPrompt = motionPlayer.CurrentPromptText;
            string resolved = string.IsNullOrWhiteSpace(currentPrompt)
                ? ResolvePrompt()
                : currentPrompt.Trim();
            isIdle = string.Equals(resolved, ResolveInitialPrompt(), StringComparison.OrdinalIgnoreCase);
            return resolved;
        }

        private Vector3 GetCurrentPositionInternal()
        {
            Animator primaryTarget = ResolvePrimaryTargetAnimator();
            Transform hips = primaryTarget != null
                ? primaryTarget.GetBoneTransform(HumanBodyBones.Hips)
                : null;
            if (hips != null)
            {
                return hips.position;
            }

            if (motionPlayer.HasCurrentSegment)
            {
                return motionPlayer.CurrentRootPosition;
            }

            return primaryTarget != null ? primaryTarget.transform.position : transform.position;
        }

        private float ClampConstraintTime(float durationSeconds)
        {
            return Mathf.Clamp(durationSeconds, 0f, ResolveGenerationDurationSeconds());
        }

        private bool TryCreateShiftedConstraintSample(
            string constraintType,
            string jointName,
            Vector3 targetWorldPosition,
            float durationSeconds,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            if (!TryCaptureCurrentPoseConstraint(constraintType, durationSeconds, out sample, out error))
            {
                return false;
            }

            Transform constraintRoot = motionPlayer.ConstraintSkeletonRoot;
            Transform targetJoint = KimodoRetargetAvatarUtility.FindTransformByName(constraintRoot, jointName);
            if (targetJoint == null)
            {
                error = $"Cannot find joint '{jointName}' under constraint skeleton root.";
                sample = null;
                return false;
            }

            Vector3 offset = targetWorldPosition - targetJoint.position;
            sample.kimodoRootPosition += offset;
            sample.unityRootPos += offset;
            sample.constraintType = constraintType;
            return true;
        }

        private bool TryCreateRoot2DWorldConstraintSample(
            float worldX,
            float worldZ,
            float durationSeconds,
            Vector2? worldHeading,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            if (!TryCaptureCurrentPoseConstraint(Root2DConstraintType, durationSeconds, out sample, out error))
            {
                return false;
            }

            Vector3 currentWorldPosition = GetCurrentPositionInternal();
            Quaternion modelToWorldRotation = ResolveModelToWorldRotation();
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            Vector3 constraintModelOrigin = isArdy
                ? Vector3.zero
                : motionPlayer.NextSegmentRootOrigin;
            Vector2 modelTarget = ResolveModelRoot2DTarget(
                sample.kimodoRootPosition,
                constraintModelOrigin,
                currentWorldPosition,
                modelToWorldRotation,
                new Vector3(worldX, currentWorldPosition.y, worldZ),
                motionPlayer.SourceHumanScale,
                ResolveTargetHumanScale());
            sample.kimodoRootPosition = new Vector3(
                modelTarget.x,
                sample.kimodoRootPosition.y,
                modelTarget.y);
            sample.unityRootPos = new Vector3(worldX, sample.unityRootPos.y, worldZ);
            sample.constraintType = Root2DConstraintType;
            sample.localAxisAngles = new List<Vector3>();
            sample.sampledJointIndices = new List<int>();
            sample.hasRootHeading = false;
            if (worldHeading.HasValue)
            {
                sample.hasRootHeading = true;
                sample.rootHeading = ResolveModelRoot2DHeading(modelToWorldRotation, worldHeading.Value);
            }

            return true;
        }

        private bool TryCaptureCurrentPoseConstraint(
            string constraintType,
            float durationSeconds,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            if (!motionPlayer.EnsureConstraintSkeletonReady(modelName, out error))
            {
                sample = null;
                return false;
            }

            return KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                null,
                motionPlayer.ConstraintSkeletonRoot,
                modelName,
                ClampConstraintTime(durationSeconds),
                constraintType,
                null,
                null,
                null,
                out sample,
                out error);
        }

        private static Vector2 NormalizeHeading(Vector2 heading)
        {
            if (heading.sqrMagnitude <= 1e-8f)
            {
                return Vector2.right;
            }

            heading.Normalize();
            return heading;
        }

        internal static Vector2 ResolveModelRoot2DOffset(
            Vector3 currentWorldPosition,
            Quaternion worldRotation,
            Vector3 targetWorldPosition)
        {
            Vector3 worldDelta = targetWorldPosition - currentWorldPosition;
            worldDelta.y = 0f;
            Vector3 localDelta = Quaternion.Inverse(worldRotation) * worldDelta;
            return new Vector2(localDelta.x, localDelta.z);
        }

        internal static Vector2 ResolveModelRoot2DTarget(
            Vector3 currentModelRootPosition,
            Vector3 constraintModelOrigin,
            Vector3 currentWorldPosition,
            Quaternion modelToWorldRotation,
            Vector3 targetWorldPosition,
            float sourceHumanScale = 1f,
            float targetHumanScale = 1f)
        {
            Vector2 offset = ResolveModelRoot2DOffset(
                currentWorldPosition,
                modelToWorldRotation,
                targetWorldPosition);
            offset *= Mathf.Max(1e-6f, sourceHumanScale) / Mathf.Max(1e-6f, targetHumanScale);
            return new Vector2(
                currentModelRootPosition.x + offset.x - constraintModelOrigin.x,
                currentModelRootPosition.z + offset.y - constraintModelOrigin.z);
        }

        internal static Vector2 ResolveModelRoot2DHeading(
            Quaternion modelToWorldRotation,
            Vector2 worldHeading)
        {
            Vector2 normalizedWorldHeading = NormalizeHeading(worldHeading);
            Vector3 modelHeading = Quaternion.Inverse(modelToWorldRotation) *
                new Vector3(normalizedWorldHeading.x, 0f, normalizedWorldHeading.y);
            return NormalizeHeading(new Vector2(modelHeading.x, modelHeading.z));
        }

        private Quaternion ResolveModelToWorldRotation()
        {
            Animator primaryTarget = ResolvePrimaryTargetAnimator();
            Transform modelRoot = primaryTarget != null
                ? primaryTarget.transform
                : transform;
            Vector3 forward = Vector3.ProjectOnPlane(modelRoot.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private float ResolveTargetHumanScale()
        {
            Animator primaryTarget = ResolvePrimaryTargetAnimator();
            return primaryTarget != null &&
                KimodoRetargetCoreUtility.IsValidHumanoid(primaryTarget.avatar)
                ? Mathf.Max(1e-6f, primaryTarget.humanScale)
                : 1f;
        }

        private void OnProgress(string message)
        {
            if (verboseLogging && !string.IsNullOrWhiteSpace(message))
            {
                Debug.Log($"[KimodoRuntimeMotionDriver] {message}", this);
            }

            UpdateStatus(message);
        }

        private void UpdateStatus(string message)
        {
            statusMessage = string.IsNullOrWhiteSpace(message) ? " " : message;
        }

        private string ResolvePrompt()
        {
            string prompt = promptDraft;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = defaultPrompt;
            }

            return string.IsNullOrWhiteSpace(prompt) ? IdlePrompt : prompt.Trim();
        }

        private string ResolveInitialPrompt()
        {
            string prompt = defaultPrompt;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = IdlePrompt;
            }

            return string.IsNullOrWhiteSpace(prompt) ? IdlePrompt : prompt.Trim();
        }

        private void EnsurePromptDraftInitialized()
        {
            if (string.IsNullOrWhiteSpace(promptDraft))
            {
                promptDraft = ResolveInitialPrompt();
            }
        }

        private void SyncGenerationDurationFromCurrentSettings()
        {
            ApplyGenerationDurationSeconds(ResolveGenerationDurationSeconds());
        }

        private float ResolveGenerationDurationSeconds()
        {
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile))
            {
                return (profile.MaxContextFrames - profile.HorizonFrames) / profile.SourceFps;
            }

            float frameDuration = generationFrames / KimodoPlayableClip.FIXED_FRAME_RATE;
            return Mathf.Clamp(
                Mathf.Max(segmentIntervalSeconds, frameDuration),
                MinGenerationDurationSeconds,
                MaxGenerationDurationSeconds);
        }

        private void ApplyGenerationDurationSeconds(float durationSeconds)
        {
            float clamped = Mathf.Clamp(durationSeconds, MinGenerationDurationSeconds, MaxGenerationDurationSeconds);
            segmentIntervalSeconds = clamped;
            generationFrames = Mathf.Max(
                1,
                KimodoFrameTimeUtility.SecondsToFrameCount(clamped, KimodoPlayableClip.FIXED_FRAME_RATE));
        }

        private bool ValidateConfiguration(out string error)
        {
            IReadOnlyList<Animator> targets = ResolveTargetAnimators();
            if (targets.Count == 0)
            {
                error = "At least one target humanoid Animator is required.";
                return false;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                Animator target = targets[i];
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(target.avatar))
                {
                    error = $"Target Animator '{target.name}' avatar is null, invalid, or not humanoid.";
                    return false;
                }
            }

            string resolvedRuntimeRoot = EnsureRuntimeRootReady();
            if (string.IsNullOrWhiteSpace(resolvedRuntimeRoot))
            {
                error = "Runtime root is empty.";
                return false;
            }

            if (!Directory.Exists(resolvedRuntimeRoot))
            {
                error = $"Runtime root does not exist: {resolvedRuntimeRoot}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private IReadOnlyList<Animator> ResolveTargetAnimators()
        {
            resolvedTargetAnimatorBuffer.Clear();
            var seen = new HashSet<Animator>();
            if (targetHumanoidAnimators != null)
            {
                for (int i = 0; i < targetHumanoidAnimators.Count; i++)
                {
                    Animator animator = targetHumanoidAnimators[i];
                    if (animator != null && seen.Add(animator))
                    {
                        resolvedTargetAnimatorBuffer.Add(animator);
                    }
                }
            }

            // Preserve scenes serialized before multi-target support.
            if (resolvedTargetAnimatorBuffer.Count == 0 && targetHumanoidAnimator != null)
            {
                resolvedTargetAnimatorBuffer.Add(targetHumanoidAnimator);
            }
            return resolvedTargetAnimatorBuffer;
        }

        private Animator ResolvePrimaryTargetAnimator()
        {
            IReadOnlyList<Animator> targets = ResolveTargetAnimators();
            return targets.Count > 0 ? targets[0] : null;
        }

        private int ComputeTargetSignature()
        {
            unchecked
            {
                int hash = 17;
                IReadOnlyList<Animator> targets = ResolveTargetAnimators();
                for (int i = 0; i < targets.Count; i++)
                {
                    Animator animator = targets[i];
                    hash = hash * 31 + animator.GetInstanceID();
                    hash = hash * 31 + (animator.avatar != null ? animator.avatar.GetInstanceID() : 0);
                }
                return hash;
            }
        }

        private string ResolveRuntimeRoot()
        {
            if (Application.isEditor)
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", KimodoFolderName));
            }

            return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, KimodoFolderName));
        }

        private string EnsureRuntimeRootReady()
        {
            return KimodoRuntimeBootstrapUtility.EnsureRuntimeRootForCurrentMode(ResolveRuntimeRoot());
        }

        private static string FormatVector3(Vector3 value)
        {
            return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
        }

        private static KimodoRuntimeSegmentReport CreateSegmentReport(KimodoRuntimeGeneratedSegment segment)
        {
            if (segment == null)
            {
                return null;
            }

            return new KimodoRuntimeSegmentReport
            {
                Index = segment.Index,
                PromptText = segment.PromptText,
                FirstRootPosition = segment.FirstRootPosition,
                EffectiveLastRootPosition = segment.LastRootPosition,
                EffectiveLastFrameIndex = segment.EffectiveLastFrameIndex,
                EffectiveLastFrameTimeSeconds = segment.EffectiveLastFrameTimeSeconds,
                MotionDurationSeconds = segment.Motion != null ? segment.Motion.LastFrameTimeSeconds : 0f
            };
        }

    }
}
