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
        [SerializeField, HideInInspector]
        private Animator targetHumanoidAnimator;
        [SerializeField] private List<Animator> targetHumanoidAnimators = new List<Animator>();

        [Header("Bridge Runtime")]
        [SerializeField] private string modelsRoot = string.Empty;
        [SerializeField] private string modelName = "Kimodo-SOMA-RP-v1";
        [FormerlySerializedAs("highVram")]
        [SerializeField] private KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPerformance;
        [SerializeField] private bool forceCpu;

        [Header("Generation")]
        [SerializeField] private string defaultPrompt = IdlePrompt;
        [SerializeField][Min(1)] private int generationFrames = 150;
        [SerializeField][Min(1)] private int diffusionSteps = 100;
        [SerializeField] private bool randomSeed = true;
        [SerializeField] private int fixedSeed = 42;
        [SerializeField][Min(0.1f)] private float segmentIntervalSeconds = 5f;
        [FormerlySerializedAs("ardyPlaybackDelaySeconds")]
        [FormerlySerializedAs("ardySafeIntervalSeconds")]
        [SerializeField][Min(0.2f), Tooltip("Request more ARDY motion when this much playable animation remains.")]
        private float ardyPlaybackReserveSeconds = 1f;
        [SerializeField, Tooltip("Adapt the ARDY history window from upcoming motion constraints.")]
        private bool ardyAutoHistory = true;
        [SerializeField, Range(0f, 1f), Tooltip("0 uses one motion token of history; 1 uses the largest history window allowed by the model context.")]
        private float ardyHistoryWeight = 1f;
        [SerializeField, Min(0.01f), Tooltip("ARDY Root2D planning speed limit in meters per second.")]
        private float ardyMaxSpeed = 1.25f;
        [SerializeField, Min(0.01f), Tooltip("ARDY Root2D planning acceleration limit in meters per second squared.")]
        private float ardyMaxAcceleration = 1.5f;
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

        private const string IdlePrompt = "idle";
        private const string KimodoFolderName = "NvlabKimodoQuickServer~";
        private const float MinGenerationDurationSeconds = 1f;
        private const float MaxGenerationDurationSeconds = 10f;

        private readonly KimodoRuntimeGenerationSession generationSession =
            new KimodoRuntimeGenerationSession();
        private string promptDraft;
        private string statusMessage = "Idle.";
        private readonly KimodoRuntimeConstraints constraints = new KimodoRuntimeConstraints();
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

            MaybeQueueNextGeneration(generationSession.LifetimeToken);

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
                constraints.SetOverlap(startedSegment.ConstraintOverlapPoses);
            }
            else
            {
                constraints.ClearOverlap();
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

        public void SetAnimationPrompt(string prompt)
        {
            SetPromptInternal(prompt);
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
                ApplyGenerationDurationSeconds(generationFrames / KimodoMotionModelProfiles.DefaultFrameRate);
            }
            else
            {
                generationSession.MarkArdyPromptDirty();
                generationSession.MarkArdySettingsDirty();
            }

            if (RequiresRuntimeSessionRestart() &&
                generationSession.IsActive)
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
                KimodoRuntimeConstraints.LeftHandType,
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
                KimodoRuntimeConstraints.RightHandType,
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
                KimodoRuntimeConstraints.LeftFootType,
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
                KimodoRuntimeConstraints.RightFootType,
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
                KimodoRoot2DPlanner.NormalizeHeading(new Vector2(worldHeadingX, worldHeadingZ)));
        }

        public void SetRoot2DTarget(
            float worldX,
            float worldZ,
            float maxSpeedMetersPerSecond = 1.25f,
            float maxAccelerationMetersPerSecond2 = 1.5f,
            float arrivalThresholdMeters = 0.1f,
            bool includeHeading = true,
            Vector2? worldHeading = null)
        {
            float maxSpeed = Mathf.Max(0.01f, maxSpeedMetersPerSecond);
            float maxAcceleration = Mathf.Max(0.01f, maxAccelerationMetersPerSecond2);
            ardyMaxSpeed = maxSpeed;
            ardyMaxAcceleration = maxAcceleration;
            Vector3 current = GetCurrentPositionInternal();
            var target = new Vector2(worldX, worldZ);
            if (KimodoRoot2DPlanner.HasArrived(current, target, arrivalThresholdMeters))
            {
                UpdateStatus($"Root2D target already within {Mathf.Max(0f, arrivalThresholdMeters):0.###} m.");
                return;
            }

            float distance = Vector2.Distance(new Vector2(current.x, current.z), target);
            float duration = KimodoRoot2DPlanner.EstimateDuration(
                distance,
                maxSpeed,
                maxAcceleration,
                MinGenerationDurationSeconds,
                MaxGenerationDurationSeconds);
            StageRoot2DWorldConstraintInternal(
                worldX,
                worldZ,
                duration,
                includeHeading && worldHeading.HasValue
                    ? KimodoRoot2DPlanner.NormalizeHeading(worldHeading.Value)
                    : (Vector2?)null);
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
            constraints.ClearUser();
            generationSession.MarkArdyConstraintsDirty();
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
            constraints.Clear();
            generationSession.BeginMotionReset();

            if (!generationSession.IsActive)
            {
                generationSession.EndMotionReset();
                UpdateStatus("Prompt reset.");
                return;
            }

            if (generationSession.GenerationInFlight)
            {
                UpdateStatus("Prompt reset. Waiting for current generation to finish.");
                generationSession.CancelGeneration();
                await WaitForGenerationSlotAsync(generationSession.LifetimeToken);
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
            generationSession.EndMotionReset();
            UpdateStatus("Prompt reset. Generating fresh segment.");
            await GenerateNextSegmentAsync(generationSession.LifetimeToken);
        }

        private async Task StartRuntimeAsync()
        {
            if (!generationSession.TryBeginStart())
            {
                return;
            }

            try
            {
                if (!ValidateConfiguration(out string error))
                {
                    UpdateStatus(error);
                    Debug.LogError($"[KimodoRuntimeMotionDriver] {error}", this);
                    return;
                }

                if (bridgeService == null || bridgeService.IsDisposed)
                {
                    bridgeService = KimodoBridgeService.CreateOwned();
                }

                constraints.Clear();
                motionPlayer.Stop();
                motionPlayer.ResetCompletionState();
                motionPlayer.ClearQueue();
                ResetArdySessionState();
                CaptureAppliedRuntimeSettings();

                generationSession.Start();
                UpdateStatus("Generator active.");
                _ = GenerateNextSegmentAsync(generationSession.LifetimeToken);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
                UpdateStatus($"Start failed: {ex.Message}");
                await StopRuntimeAsync();
            }
            finally
            {
                generationSession.EndStart();
            }
        }

        private async Task StopRuntimeAsync()
        {
            generationSession.Stop();

            constraints.Clear();
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

        private void MaybeQueueNextGeneration(CancellationToken token)
        {
            if (!generationSession.Running || generationSession.GenerationInFlight || generationSession.GenerationBlocked)
            {
                return;
            }

            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (isArdy && !KimodoRuntimeGenerationSession.ShouldRequestArdyGeneration(
                    motionPlayer.BufferedDurationSeconds,
                    generationSession.ArdyPlaybackReserveSeconds,
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
                if (generationSession.ShouldReportWait())
                {
                    UpdateStatus($"Waiting for segment {waitingForSegment} to finish before generating segment {generationSession.SegmentIndex}.");
                }

                return;
            }

            generationSession.ClearWaitStatus();
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
            if (!generationSession.TryBeginGeneration(
                    token,
                    out CancellationTokenSource generationCts,
                    out int requestVersion,
                    out int requestSegmentIndex))
            {
                return;
            }

            try
            {
                CancellationToken generationToken = generationCts.Token;

                string prompt = ResolvePrompt();
                bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile ardyProfile);
                bool sendPrompt = !isArdy || !generationSession.ArdyStarted || generationSession.ArdyPromptDirty;
                bool sendConstraints = !isArdy || !generationSession.ArdyStarted || generationSession.ArdyConstraintsDirty;
                bool sendSettings = isArdy && (!generationSession.ArdyStarted || generationSession.ArdySettingsDirty);
                int consumedPendingRevision = constraints.PendingRevision;
                float generationDuration = ResolveGenerationDurationSeconds();
                string constraintsJson = sendConstraints
                    ? BuildNextConstraintsJson(isArdy, ardyProfile, generationDuration)
                    : string.Empty;
                if (isArdy)
                {
                    generationSession.BeginArdyRequest();
                }
                int resolvedRequestSeed = generationSession.ResolveRequestSeed(isArdy, randomSeed, fixedSeed);
                bool sessionUpdateOnly = isArdy && generationSession.ArdyStarted && !sendSettings;
                string resolvedModelName = isArdy
                    ? ardyProfile.ModelName
                    : KimodoMotionModelProfiles.NormalizeName(modelName);
                var request = new KimodoGenerationRequestDto
                {
                    ardy_session_update_only = sessionUpdateOnly,
                    prompt = sendPrompt ? prompt : null,
                    duration = isArdy ? (float?)null : generationDuration,
                    seed = resolvedRequestSeed,
                    steps = isArdy
                        ? KimodoMotionModelProfiles.ResolveArdyProtocolSteps(diffusionSteps, ardyProfile)
                        : KimodoMotionModelProfiles.ClampDiffusionSteps(resolvedModelName, diffusionSteps),
                    constraints = new KimodoConstraintPayload
                    {
                        json = sendConstraints
                            ? (isArdy && string.IsNullOrWhiteSpace(constraintsJson) ? "[]" : constraintsJson)
                            : string.Empty
                    },
                    model = resolvedModelName,
                    text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    simulate_free_vram_gb = forceCpu ? 0 : (int?)null,
                    models_root = string.IsNullOrWhiteSpace(modelsRoot) ? string.Empty : Path.GetFullPath(modelsRoot.Trim())
                };
                if (isArdy)
                {
                    request.time_as_double = motionPlayer.PlaybackTimeAsDouble;
                    if (sendSettings)
                    {
                        if (!ardyAutoHistory)
                        {
                            request.ardy_history_weight = Mathf.Clamp01(ardyHistoryWeight);
                        }
                        request.ardy_playback_reserve_seconds = Mathf.Max(0.2f, ardyPlaybackReserveSeconds);
                        request.ardy_max_speed = Mathf.Max(0.01f, ardyMaxSpeed);
                        request.ardy_max_acceleration = Mathf.Max(0.01f, ardyMaxAcceleration);
                    }
                }

                OnProgress($"Generating segment {requestSegmentIndex}...");
                KimodoBridgeGenerationResult bridgeResult =
                    await bridgeService.GenerateAsync(request, OnProgress, generationToken);
                bool staleRequest = requestVersion != generationSession.RequestVersion || generationToken.IsCancellationRequested;
                if (KimodoRuntimeGenerationSession.ShouldDiscardResult(
                        isArdy,
                        staleRequest,
                        token.IsCancellationRequested))
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
                    KimodoRuntimeSegmentBuilder.ValidateArdyResult(
                        bridgeResult,
                        ardyProfile,
                        resolvedRequestSeed);
                    if (bridgeResult.ArdyPlaybackReserveSeconds.HasValue)
                    {
                        generationSession.SetArdyPlaybackReserve(
                            (float)bridgeResult.ArdyPlaybackReserveSeconds.Value);
                    }
                    if (bridgeResult.MotionData == null)
                    {
                        generationSession.CompleteArdyRequest(
                            sendPrompt,
                            sendConstraints,
                            sendSettings,
                            staleRequest);
                        UpdateStatus("ARDY cursor synchronized; no new KMB frames were required.");
                        return;
                    }
                }

                KimodoRuntimeGeneratedSegment generatedSegment =
                    await KimodoRuntimeSegmentBuilder.BuildAsync(
                        bridgeResult,
                        modelName,
                        prompt,
                        requestSegmentIndex,
                        isArdy,
                        segmentTrimTrailSettings,
                        segmentOverlapHeadSettings,
                        allowPartialJoints,
                        generationToken);
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
                    generationSession.CompleteArdyRequest(
                        sendPrompt,
                        sendConstraints,
                        sendSettings,
                        staleRequest);
                }
                else
                {
                    motionPlayer.Enqueue(generatedSegment, verboseLogging);
                }
                SegmentReady?.Invoke(CreateSegmentReport(generatedSegment));

                constraints.CompleteGeneration(isArdy, consumedPendingRevision);
                generationSession.AdvanceSegment(requestSegmentIndex);
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
                generationSession.EndGeneration(generationCts);
                if (generationSession.ShouldRunPendingRefresh)
                {
                    _ = GenerateNextSegmentAsync(generationSession.LifetimeToken);
                }
            }
        }

        private string BuildNextConstraintsJson(
            bool isArdy,
            KimodoMotionModelProfile ardyProfile,
            float generationDuration)
        {
            List<KimodoMarkerSampleResult> activeConstraints = constraints.BuildForGeneration(
                isArdy,
                isArdy ? motionPlayer.PlaybackTimeAsDouble : 0.0,
                includeOverlap: loopHint && !isArdy,
                generationDuration);
            if (activeConstraints.Count == 0)
            {
                return string.Empty;
            }

            string futureConstraints = KimodoConstraintJsonExporter.ToConstraintsJson(
                activeConstraints,
                new KimodoConstraintExportContext(motionPlayer != null ? motionPlayer.SourceHumanScale : 1f),
                0.0,
                generationDuration,
                isArdy ? ardyProfile.SourceFps : KimodoMotionModelProfiles.DefaultFrameRate);
            return futureConstraints;
        }

        private async Task RefreshUpcomingGenerationAsync(
            string inactiveStatus,
            string waitingStatus,
            string generatingStatus)
        {
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            generationSession.RequestRefresh(isArdy);

            if (!generationSession.IsActive)
            {
                UpdateStatus(inactiveStatus);
                return;
            }

            if (generationSession.GenerationInFlight)
            {
                UpdateStatus(waitingStatus);
                return;
            }

            if (!isArdy && motionPlayer.QueuedSegmentCount > 0)
            {
                UpdateStatus(waitingStatus);
                return;
            }

            UpdateStatus(generatingStatus);
            await GenerateNextSegmentAsync(generationSession.LifetimeToken);
        }

        private async Task WaitForGenerationSlotAsync(CancellationToken token)
        {
            while (generationSession.GenerationInFlight && !token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
            }
        }

        private void ResetArdySessionState()
        {
            generationSession.ResetArdy(ardyPlaybackReserveSeconds);
        }

        private bool RequiresRuntimeSessionRestart()
        {
            KimodoRuntimeSessionSignature current = CreateRuntimeSessionSignature();
            if (!generationSession.TryGetAppliedSignature(out KimodoRuntimeSessionSignature applied))
            {
                return false;
            }

            bool targetChanged = applied.Target != current.Target;
            bool runtimeSignatureChanged =
                !string.Equals(applied.ModelName, current.ModelName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(applied.ModelsRoot, current.ModelsRoot, StringComparison.Ordinal) ||
                applied.TextEncoderMode != current.TextEncoderMode ||
                applied.ForceCpu != current.ForceCpu;
            return RequiresNewGenerationSession(
                targetChanged,
                runtimeSignatureChanged,
                KimodoMotionModelProfiles.TryGetArdy(current.ModelName, out _),
                applied.RandomSeed != current.RandomSeed,
                !current.RandomSeed && applied.FixedSeed != current.FixedSeed);
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
            generationSession.Capture(CreateRuntimeSessionSignature());
        }

        private KimodoRuntimeSessionSignature CreateRuntimeSessionSignature() =>
            new KimodoRuntimeSessionSignature(
                ComputeTargetSignature(),
                modelsRoot,
                modelName,
                textEncoderMode,
                forceCpu,
                randomSeed,
                fixedSeed);

        private string SetPromptInternal(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return GetCurrentPromptInternal(out bool _);
            }

            promptDraft = prompt.Trim();
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                generationSession.MarkArdyPromptDirty();
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
            if (!KimodoRuntimeConstraintSampler.TryCreateEndEffector(
                    motionPlayer,
                    modelName,
                    constraintType,
                    jointName,
                    new Vector3(x, y, z),
                    GetCurrentPositionInternal(),
                    ResolveModelToWorldRotation(),
                    ClampConstraintTime(durationSeconds),
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
            if (!KimodoRuntimeConstraintSampler.TryCreateRoot2D(
                    motionPlayer,
                    modelName,
                    new Vector2(worldX, worldZ),
                    worldHeading,
                    GetCurrentPositionInternal(),
                    ResolveModelToWorldRotation(),
                    ResolveTargetHumanScale(),
                    ClampConstraintTime(durationSeconds),
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
            if (!constraints.Commit())
            {
                return;
            }

            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                generationSession.MarkArdyConstraintsDirty();
            }
            _ = RefreshUpcomingGenerationAsync(inactiveStatus, waitingStatus, generatingStatus);
        }

        private void StageConstraintSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null)
            {
                return;
            }

            constraints.Stage(
                sample,
                KimodoMotionModelProfiles.TryGetArdy(modelName, out _)
                    ? motionPlayer.PlaybackTimeAsDouble
                    : 0.0);
        }

        private string GetCurrentPromptInternal(out bool isIdle)
        {
            string currentPrompt = motionPlayer != null ? motionPlayer.CurrentPromptText : null;
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

            if (motionPlayer != null && motionPlayer.HasCurrentSegment)
            {
                return motionPlayer.CurrentRootPosition;
            }

            return primaryTarget != null ? primaryTarget.transform.position : transform.position;
        }

        private float ClampConstraintTime(float durationSeconds)
        {
            return Mathf.Clamp(durationSeconds, 0f, ResolveGenerationDurationSeconds());
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

            float frameDuration = generationFrames / KimodoMotionModelProfiles.DefaultFrameRate;
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
                KimodoFrameTimeUtility.SecondsToFrameCount(clamped, KimodoMotionModelProfiles.DefaultFrameRate));
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
                    hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(animator);
                    hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(animator.avatar);
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
