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
    [AddComponentMenu("Kimodo/Runtime Motion Driver")]
    public sealed class KimodoRuntimeMotionDriver : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Animator targetHumanoidAnimator;

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
        [SerializeField][Min(0f), Tooltip("0 uses the selected ARDY profile maximum.")]
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
        [SerializeField, Tooltip("Debug only. Draw the internal source skeleton in the scene using Debug.DrawLine.")]
        private bool drawDebugSkeleton;
        [SerializeField] private Color debugSkeletonBoneColor = new Color(0.2f, 0.95f, 1f, 1f);
        [SerializeField] private Color debugSkeletonJointColor = new Color(1f, 0.7f, 0.2f, 1f);
        [SerializeField][Min(0.001f)] private float debugJointMarkerSize = 0.025f;
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

        private CancellationTokenSource lifetimeCts;
        private CancellationTokenSource activeGenerationCts;
        private Task schedulerTask;
        private bool running;
        private bool startRequested;
        private bool generationInFlight;
        private int segmentIndex;
        private int lastGenerationWaitStatusSegment = -1;
        private int generationRequestVersion;
        private string promptDraft;
        private string statusMessage = "Idle.";
        private readonly List<KimodoMarkerSampleResult> nextConstraintPoses = new List<KimodoMarkerSampleResult>();
        private readonly List<KimodoMarkerSampleResult> stagedConstraintSamples = new List<KimodoMarkerSampleResult>();
        private readonly List<KimodoMarkerSampleResult> pendingConstraintSamples = new List<KimodoMarkerSampleResult>();
        private readonly List<KimodoMarkerSampleResult> constraintJsonScratch = new List<KimodoMarkerSampleResult>();
        private KimodoBridgeService bridgeService;
        private int? ardyStreamResolvedSeed;
        private bool ardySessionStarted;
        private bool ardyPromptDirty = true;
        private bool ardyConstraintsDirty = true;
        private bool ardySettingsDirty = true;
        private bool ardyRefreshPending;
        private float ardyEffectivePlaybackReserveSeconds = 1f;
        private KimodoRuntimeMotionPlayer motionPlayer;
        private bool generationBlocked;
        private bool appliedRuntimeSettingsInitialized;
        private Animator appliedTargetHumanoidAnimator;
        private string appliedModelsRoot = string.Empty;
        private string appliedModelName = string.Empty;
        private KimodoTextEncoderMode appliedTextEncoderMode;
        private bool appliedForceCpu;
        private bool appliedRandomSeed;
        private int appliedFixedSeed;

        public string StatusMessage => statusMessage;
        public bool IsRunning => running;
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

        private void Reset()
        {
            if (targetHumanoidAnimator == null)
            {
                targetHumanoidAnimator = GetComponent<Animator>();
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
                targetHumanoidAnimator,
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

                if (drawDebugSkeleton)
                {
                    motionPlayer.DrawDebugSkeleton(debugSkeletonBoneColor, debugSkeletonJointColor, debugJointMarkerSize);
                }

                return;
            }

            if (loopHint && !KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                SetNextConstraintPoses(startedSegment.ConstraintOverlapPoses);
            }
            else
            {
                ClearNextConstraintPoses();
            }

            UpdateStatus($"Playing segment {startedSegment.Index}.");
            SegmentStarted?.Invoke(CreateSegmentReport(startedSegment));

            if (completedSegment != null)
            {
                SegmentCompleted?.Invoke(CreateSegmentReport(completedSegment));
            }

            if (drawDebugSkeleton)
            {
                motionPlayer.DrawDebugSkeleton(debugSkeletonBoneColor, debugSkeletonJointColor, debugJointMarkerSize);
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
                ardyPromptDirty = true;
                ardySettingsDirty = true;
            }

            if (RequiresRuntimeSessionRestart() &&
                running &&
                lifetimeCts != null &&
                !lifetimeCts.IsCancellationRequested)
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

        public void SetLeftHandConstraint(float x, float y, float z, float duration = 1f)
        {
            StageEndEffectorConstraintInternal("LeftHand constraint", LeftHandConstraintType, "LeftHand", x, y, z, duration);
        }

        public void SetRightHandConstraint(float x, float y, float z, float duration = 1f)
        {
            StageEndEffectorConstraintInternal("RightHand constraint", RightHandConstraintType, "RightHand", x, y, z, duration);
        }

        public void SetLeftFootConstraint(float x, float y, float z, float duration = 1f)
        {
            StageEndEffectorConstraintInternal("LeftFoot constraint", LeftFootConstraintType, "LeftFoot", x, y, z, duration);
        }

        public void SetRightFootConstraint(float x, float y, float z, float duration = 1f)
        {
            StageEndEffectorConstraintInternal("RightFoot constraint", RightFootConstraintType, "RightFoot", x, y, z, duration);
        }

        /// <summary>Stages an absolute Root2D position in generated-motion coordinates.</summary>
        public void SetRoot2D(float x, float z, float duration = 1f)
        {
            StageRoot2DConstraintInternal(x, z, duration, null);
        }

        public void SetRoot2D(float x, float z, float headingX, float headingZ, float duration = 1f)
        {
            StageRoot2DConstraintInternal(x, z, duration, NormalizeHeading(new Vector2(headingX, headingZ)));
        }

        /// <summary>Stages a Unity world-space target as a local Root2D displacement.</summary>
        public void SetRoot2DWorld(float worldX, float worldZ, float duration = 1f)
        {
            Vector3 currentWorldPosition = GetCurrentPositionInternal();
            Vector2 localOffset = ResolveLocalRoot2DOffset(
                currentWorldPosition,
                transform.rotation,
                new Vector3(worldX, currentWorldPosition.y, worldZ));
            StageRoot2DLocalConstraintInternal(localOffset.x, localOffset.y, duration, null);
        }

        public void SetRoot2DTarget(
            float x,
            float z,
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

            StageConstraintSample(new KimodoMarkerSampleResult
            {
                constraintType = Root2DTargetConstraintType,
                kimodoRootPosition = new Vector3(x, 0f, z),
                unityRootPos = new Vector3(x, 0f, z),
                hasRootHeading = false,
                rootTargetMaxSpeed = Mathf.Max(0.01f, maxSpeedMetersPerSecond),
                rootTargetMaxAcceleration = Mathf.Max(0.01f, maxAccelerationMetersPerSecond2),
                rootTargetArrivalThreshold = Mathf.Max(0f, arrivalThresholdMeters),
                rootTargetIncludeHeading = includeHeading
            });
            UpdateStatus($"Root2D target staged at ({x:0.###}, {z:0.###}).");
        }

        public string QueuePromptedRoot2DLocal(string prompt, float x, float z, float generationDurationSeconds)
        {
            ApplyGenerationDurationSeconds(generationDurationSeconds);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                promptDraft = prompt.Trim();
            }

            string stageResult = StageRoot2DLocalConstraintInternal(x, z, generationDurationSeconds, null);
            if (stageResult.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) ||
                stageResult.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            {
                return stageResult;
            }

            ApplyStagedConstraints();
            return stageResult;
        }

        public void SetRoot2DLocal(float x, float z, float duration = 1f)
        {
            StageRoot2DLocalConstraintInternal(x, z, duration, null);
        }

        public void SetRoot2DLocal(float x, float z, float headingX, float headingZ, float duration = 1f)
        {
            StageRoot2DLocalConstraintInternal(x, z, duration, new Vector2(headingX, headingZ));
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
            stagedConstraintSamples.Clear();
            pendingConstraintSamples.Clear();
            ardyConstraintsDirty = true;
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
            stagedConstraintSamples.Clear();
            pendingConstraintSamples.Clear();
            ClearNextConstraintPoses();
            segmentIndex = 0;
            generationRequestVersion++;
            lastGenerationWaitStatusSegment = -1;
            generationBlocked = true;

            if (!running || lifetimeCts == null || lifetimeCts.IsCancellationRequested)
            {
                generationBlocked = false;
                UpdateStatus("Prompt reset.");
                return;
            }

            if (generationInFlight)
            {
                UpdateStatus("Prompt reset. Waiting for current generation to finish.");
                TryCancelActiveGeneration();
                await WaitForGenerationSlotAsync(lifetimeCts.Token);
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
            generationBlocked = false;
            UpdateStatus("Prompt reset. Generating fresh segment.");
            await GenerateNextSegmentAsync(lifetimeCts.Token);
        }

        private async Task StartRuntimeAsync()
        {
            if (running || startRequested)
            {
                return;
            }

            startRequested = true;
            try
            {
                if (!ValidateConfiguration(out string error))
                {
                    UpdateStatus(error);
                    Debug.LogError($"[KimodoRuntimeMotionDriver] {error}", this);
                    return;
                }

                lifetimeCts?.Cancel();
                lifetimeCts?.Dispose();
                lifetimeCts = new CancellationTokenSource();
                if (bridgeService == null || bridgeService.IsDisposed)
                {
                    bridgeService = KimodoBridgeService.CreateOwned();
                }

                segmentIndex = 0;
                generationInFlight = false;
                generationRequestVersion = 0;
                generationBlocked = false;
                lastGenerationWaitStatusSegment = -1;
                stagedConstraintSamples.Clear();
                pendingConstraintSamples.Clear();
                ClearNextConstraintPoses();
                motionPlayer.Stop();
                motionPlayer.ResetCompletionState();
                motionPlayer.ClearQueue();
                ResetArdySessionState();
                CaptureAppliedRuntimeSettings();

                running = true;
                schedulerTask = RunSchedulerLoopAsync(lifetimeCts.Token);
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
                startRequested = false;
            }
        }

        private async Task StopRuntimeAsync()
        {
            running = false;

            CancellationTokenSource cts = lifetimeCts;
            lifetimeCts = null;
            CancellationTokenSource generationCts = activeGenerationCts;
            activeGenerationCts = null;
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

            Task task = schedulerTask;
            schedulerTask = null;
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
            generationInFlight = false;
            lastGenerationWaitStatusSegment = -1;
            stagedConstraintSamples.Clear();
            pendingConstraintSamples.Clear();
            ClearNextConstraintPoses();
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
                running = false;
            }
        }

        private void MaybeQueueNextGeneration(CancellationToken token)
        {
            if (!running || generationInFlight || generationBlocked)
            {
                return;
            }

            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (isArdy && !ShouldRequestArdyGeneration(
                    motionPlayer.BufferedDurationSeconds,
                    ardyEffectivePlaybackReserveSeconds,
                    ardyRefreshPending))
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
                if (lastGenerationWaitStatusSegment != segmentIndex)
                {
                    UpdateStatus($"Waiting for segment {waitingForSegment} to finish before generating segment {segmentIndex}.");
                    lastGenerationWaitStatusSegment = segmentIndex;
                }

                return;
            }

            lastGenerationWaitStatusSegment = -1;
            _ = GenerateNextSegmentAsync(token);
        }

        private bool CanStartGenerationForCurrentSegment(out int waitingForSegment)
        {
            int requiredCompletedSegment = segmentIndex - 2;
            waitingForSegment = requiredCompletedSegment;
            if (requiredCompletedSegment < 0)
            {
                return true;
            }

            return motionPlayer.LastCompletedSegmentIndex >= requiredCompletedSegment;
        }

        private async Task GenerateNextSegmentAsync(CancellationToken token)
        {
            if (generationInFlight)
            {
                return;
            }

            generationInFlight = true;
            int requestVersion = generationRequestVersion;
            int requestSegmentIndex = segmentIndex;
            CancellationTokenSource generationCts = null;
            try
            {
                generationCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                activeGenerationCts = generationCts;
                CancellationToken generationToken = generationCts.Token;

                string prompt = ResolvePrompt();
                string constraintsJson = BuildNextConstraintsJson();
                bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile ardyProfile);
                bool sendPrompt = !isArdy || !ardySessionStarted || ardyPromptDirty;
                bool sendConstraints = !isArdy || !ardySessionStarted || ardyConstraintsDirty;
                bool sendSettings = isArdy && (!ardySessionStarted || ardySettingsDirty);
                if (isArdy)
                {
                    ardyRefreshPending = false;
                }
                int resolvedRequestSeed = isArdy && ardyStreamResolvedSeed.HasValue
                    ? ardyStreamResolvedSeed.Value
                    : (randomSeed ? (Guid.NewGuid().GetHashCode() & int.MaxValue) : fixedSeed);
                if (isArdy)
                {
                    ardyStreamResolvedSeed = resolvedRequestSeed;
                }
                var request = new KimodoGenerationRequestDto
                {
                    ardy_session_update_only = isArdy && ardySessionStarted && !sendSettings,
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
                        request.ardy_history_crop_seconds = ardyHistoryCropSeconds > 0f
                            ? ardyHistoryCropSeconds
                            : (double?)null;
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
                bool staleRequest = requestVersion != generationRequestVersion || generationToken.IsCancellationRequested;
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
                        ardyEffectivePlaybackReserveSeconds = Mathf.Max(
                            0.2f,
                            (float)bridgeResult.ArdyPlaybackReserveSeconds.Value);
                    }
                    if (bridgeResult.MotionData == null)
                    {
                        ardySessionStarted = true;
                        if (!staleRequest)
                        {
                            if (sendPrompt) ardyPromptDirty = false;
                            if (sendConstraints) ardyConstraintsDirty = false;
                            if (sendSettings) ardySettingsDirty = false;
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
                    ardySessionStarted = true;
                    if (!staleRequest)
                    {
                        if (sendPrompt) ardyPromptDirty = false;
                        if (sendConstraints) ardyConstraintsDirty = false;
                        if (sendSettings) ardySettingsDirty = false;
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

                if (!isArdy)
                {
                    pendingConstraintSamples.Clear();
                }
                segmentIndex = requestSegmentIndex + 1;
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
                if (ReferenceEquals(activeGenerationCts, generationCts))
                {
                    activeGenerationCts = null;
                }

                generationCts?.Dispose();
                generationInFlight = false;
                if (running && !generationBlocked && ardyRefreshPending &&
                    lifetimeCts != null && !lifetimeCts.IsCancellationRequested)
                {
                    _ = GenerateNextSegmentAsync(lifetimeCts.Token);
                }
            }
        }

        private List<KimodoMarkerSampleResult> BuildActiveGenerationConstraints()
        {
            var samples = new List<KimodoMarkerSampleResult>();
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            double ardyApplyTime = isArdy ? motionPlayer.PlaybackTimeAsDouble : 0.0;

            if (!isArdy)
            {
                pendingConstraintSamples.RemoveAll(sample =>
                    sample != null &&
                    string.Equals(sample.constraintType, Root2DTargetConstraintType, StringComparison.OrdinalIgnoreCase));
            }

            if (loopHint &&
                !KimodoMotionModelProfiles.TryGetArdy(modelName, out _) &&
                nextConstraintPoses.Count > 0)
            {
                for (int i = 0; i < nextConstraintPoses.Count; i++)
                {
                    KimodoMarkerSampleResult source = nextConstraintPoses[i];
                    if (source == null)
                    {
                        continue;
                    }

                    KimodoMarkerSampleResult sample = source.Clone();
                    sample.constraintType = FullBodyConstraintType;
                    sample.sampleTime = source.sampleTime;
                    sample.kimodoRootPosition = new Vector3(0f, sample.kimodoRootPosition.y, 0f);
                    sample.unityRootPos = sample.kimodoRootPosition;
                    samples.Add(sample);
                }
            }

            for (int i = 0; i < pendingConstraintSamples.Count; i++)
            {
                KimodoMarkerSampleResult pending = pendingConstraintSamples[i];
                if (pending == null)
                {
                    continue;
                }

                KimodoMarkerSampleResult clone = pending.Clone();
                clone.sampleTime = isArdy
                    ? Math.Max(0.0, clone.sampleTime - ardyApplyTime)
                    : ClampConstraintTime((float)clone.sampleTime);
                samples.Add(clone);
            }

            samples.Sort((a, b) => a.sampleTime.CompareTo(b.sampleTime));
            return samples;
        }

        private string BuildNextConstraintsJson()
        {
            List<KimodoMarkerSampleResult> activeConstraints = BuildActiveGenerationConstraints();
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile);
            if (activeConstraints.Count == 0)
            {
                return string.Empty;
            }

            constraintJsonScratch.Clear();
            constraintJsonScratch.AddRange(activeConstraints);
            string futureConstraints = KimodoConstraintJsonExporter.ToConstraintsJson(
                constraintJsonScratch,
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
            lastGenerationWaitStatusSegment = -1;
            generationRequestVersion++;
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (isArdy)
            {
                ardyRefreshPending = true;
            }
            if (!isArdy)
            {
                int clearedQueuedSegmentCount = motionPlayer.QueuedSegmentCount;
                motionPlayer.ClearQueue();
                RewindSegmentIndexAfterQueueInvalidation(clearedQueuedSegmentCount);
            }

            if (!running || lifetimeCts == null || lifetimeCts.IsCancellationRequested)
            {
                UpdateStatus(inactiveStatus);
                return;
            }

            if (generationInFlight)
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
                await WaitForGenerationSlotAsync(lifetimeCts.Token);
                if (!running || lifetimeCts == null || lifetimeCts.IsCancellationRequested)
                {
                    return;
                }
            }

            UpdateStatus(generatingStatus);
            await GenerateNextSegmentAsync(lifetimeCts.Token);
        }

        internal static bool ShouldCancelActiveGenerationForRefresh(bool isArdy)
        {
            return !isArdy;
        }

        private void TryCancelActiveGeneration()
        {
            CancellationTokenSource generationCts = activeGenerationCts;
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
            while (generationInFlight && !token.IsCancellationRequested)
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
            segmentIndex = Mathf.Max(minSegmentIndex, segmentIndex - clearedQueuedSegmentCount);
        }

        private void ResetArdySessionState()
        {
            ardyStreamResolvedSeed = null;
            ardySessionStarted = false;
            ardyPromptDirty = true;
            ardyConstraintsDirty = true;
            ardySettingsDirty = true;
            ardyRefreshPending = false;
            ardyEffectivePlaybackReserveSeconds = Mathf.Max(0.2f, ardyPlaybackReserveSeconds);
        }

        private bool RequiresRuntimeSessionRestart()
        {
            if (!appliedRuntimeSettingsInitialized)
            {
                return false;
            }

            string currentModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            string currentModelsRoot = (modelsRoot ?? string.Empty).Trim();
            bool targetChanged = !ReferenceEquals(appliedTargetHumanoidAnimator, targetHumanoidAnimator);
            bool runtimeSignatureChanged =
                !string.Equals(appliedModelName, currentModelName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(appliedModelsRoot, currentModelsRoot, StringComparison.Ordinal) ||
                appliedTextEncoderMode != textEncoderMode ||
                appliedForceCpu != forceCpu;
            return RequiresNewGenerationSession(
                targetChanged,
                runtimeSignatureChanged,
                KimodoMotionModelProfiles.TryGetArdy(currentModelName, out _),
                appliedRandomSeed != randomSeed,
                !randomSeed && appliedFixedSeed != fixedSeed);
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
            appliedTargetHumanoidAnimator = targetHumanoidAnimator;
            appliedModelsRoot = (modelsRoot ?? string.Empty).Trim();
            appliedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            appliedTextEncoderMode = textEncoderMode;
            appliedForceCpu = forceCpu;
            appliedRandomSeed = randomSeed;
            appliedFixedSeed = fixedSeed;
            appliedRuntimeSettingsInitialized = true;
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
                ardyPromptDirty = true;
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

        private string StageRoot2DConstraintInternal(float x, float z, float durationSeconds, Vector2? heading)
        {
            if (!TryCreateRoot2DConstraintSample(x, z, durationSeconds, heading, out KimodoMarkerSampleResult sample, out string error))
            {
                UpdateStatus(error);
                return error;
            }

            StageConstraintSample(sample);
            string result = $"Root2D staged at ({x:0.###}, {z:0.###}).";
            UpdateStatus(result);
            return result;
        }

        private string StageRoot2DLocalConstraintInternal(float x, float z, float durationSeconds, Vector2? heading)
        {
            if (!TryCreateRoot2DLocalConstraintSample(x, z, durationSeconds, heading, out KimodoMarkerSampleResult sample, out string error))
            {
                UpdateStatus(error);
                return error;
            }

            StageConstraintSample(sample);
            string result = $"Root2D local staged at ({x:0.###}, {z:0.###}).";
            UpdateStatus(result);
            return result;
        }

        private void ApplyStagedConstraintsInternal(
            string inactiveStatus,
            string waitingStatus,
            string generatingStatus)
        {
            if (stagedConstraintSamples.Count == 0)
            {
                return;
            }

            for (int i = 0; i < stagedConstraintSamples.Count; i++)
            {
                UpsertPendingConstraintSample(stagedConstraintSamples[i]);
            }

            stagedConstraintSamples.Clear();
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                ardyConstraintsDirty = true;
            }
            _ = RefreshUpcomingGenerationAsync(inactiveStatus, waitingStatus, generatingStatus);
        }

        private void StageConstraintSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null)
            {
                return;
            }

            for (int i = stagedConstraintSamples.Count - 1; i >= 0; i--)
            {
                KimodoMarkerSampleResult existing = stagedConstraintSamples[i];
                if (existing == null)
                {
                    stagedConstraintSamples.RemoveAt(i);
                    continue;
                }

                if (string.Equals(existing.constraintType, sample.constraintType, StringComparison.OrdinalIgnoreCase))
                {
                    stagedConstraintSamples.RemoveAt(i);
                }
            }

            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                sample.sampleTime += motionPlayer.PlaybackTimeAsDouble;
            }
            stagedConstraintSamples.Add(sample);
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
            Transform hips = targetHumanoidAnimator != null
                ? targetHumanoidAnimator.GetBoneTransform(HumanBodyBones.Hips)
                : null;
            if (hips != null)
            {
                return hips.position;
            }

            if (motionPlayer.HasCurrentSegment)
            {
                return motionPlayer.CurrentRootPosition;
            }

            return targetHumanoidAnimator != null ? targetHumanoidAnimator.transform.position : transform.position;
        }

        private void UpsertPendingConstraintSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null)
            {
                return;
            }

            for (int i = pendingConstraintSamples.Count - 1; i >= 0; i--)
            {
                KimodoMarkerSampleResult existing = pendingConstraintSamples[i];
                if (existing == null)
                {
                    pendingConstraintSamples.RemoveAt(i);
                    continue;
                }

                if (string.Equals(existing.constraintType, sample.constraintType, StringComparison.OrdinalIgnoreCase))
                {
                    pendingConstraintSamples.RemoveAt(i);
                }
            }

            pendingConstraintSamples.Add(sample);
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

        private bool TryCreateRoot2DConstraintSample(
            float x,
            float z,
            float durationSeconds,
            Vector2? heading,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            if (!TryCaptureCurrentPoseConstraint(Root2DConstraintType, durationSeconds, out sample, out error))
            {
                return false;
            }

            Vector3 offset = new Vector3(x - sample.kimodoRootPosition.x, 0f, z - sample.kimodoRootPosition.z);
            sample.kimodoRootPosition += offset;
            sample.unityRootPos += offset;
            sample.constraintType = Root2DConstraintType;
            sample.localAxisAngles = new List<Vector3>();
            sample.sampledJointIndices = new List<int>();
            sample.hasRootHeading = false;
            if (heading.HasValue)
            {
                sample.hasRootHeading = true;
                sample.rootHeading = NormalizeHeading(heading.Value);
            }

            return true;
        }

        private bool TryCreateRoot2DLocalConstraintSample(
            float localX,
            float localZ,
            float durationSeconds,
            Vector2? localHeading,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            if (!TryCaptureCurrentPoseConstraint(Root2DConstraintType, durationSeconds, out sample, out error))
            {
                return false;
            }

            Vector2 basisForward = sample.hasRootHeading
                ? NormalizeHeading(sample.rootHeading)
                : Vector2.up;
            Vector2 basisRight = new Vector2(basisForward.y, -basisForward.x);

            Vector2 worldOffset2D = basisRight * localX + basisForward * localZ;
            sample.kimodoRootPosition = new Vector3(
                worldOffset2D.x,
                sample.kimodoRootPosition.y,
                worldOffset2D.y);
            sample.unityRootPos = new Vector3(
                worldOffset2D.x,
                sample.unityRootPos.y,
                worldOffset2D.y);
            sample.constraintType = Root2DConstraintType;
            sample.localAxisAngles = new List<Vector3>();
            sample.sampledJointIndices = new List<int>();
            sample.hasRootHeading = false;
            if (localHeading.HasValue)
            {
                Vector2 normalizedLocalHeading = NormalizeHeading(localHeading.Value);
                Vector2 worldHeading = basisRight * normalizedLocalHeading.x + basisForward * normalizedLocalHeading.y;
                sample.hasRootHeading = true;
                sample.rootHeading = NormalizeHeading(worldHeading);
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

        internal static Vector2 ResolveLocalRoot2DOffset(
            Vector3 currentWorldPosition,
            Quaternion worldRotation,
            Vector3 targetWorldPosition)
        {
            Vector3 worldDelta = targetWorldPosition - currentWorldPosition;
            worldDelta.y = 0f;
            Vector3 localDelta = Quaternion.Inverse(worldRotation) * worldDelta;
            return new Vector2(localDelta.x, localDelta.z);
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

        private void ClearNextConstraintPoses()
        {
            nextConstraintPoses.Clear();
            constraintJsonScratch.Clear();
        }

        private void SetNextConstraintPoses(IReadOnlyList<KimodoMarkerSampleResult> poses)
        {
            nextConstraintPoses.Clear();
            if (poses == null)
            {
                return;
            }

            for (int i = 0; i < poses.Count; i++)
            {
                KimodoMarkerSampleResult pose = poses[i];
                if (pose != null)
                {
                    nextConstraintPoses.Add(pose);
                }
            }
        }

        private bool ValidateConfiguration(out string error)
        {
            if (targetHumanoidAnimator == null)
            {
                error = "Target humanoid animator is not assigned.";
                return false;
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
