using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using KimodoUnityBridge;
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
        [SerializeField, Tooltip("Adapt ARDY history from previous root speed: 0-1 m/s = 0.225; 1-10 m/s grows exponentially to 1; above 10 m/s = 1.")]
        private bool ardyAutoHistory = true;
        [SerializeField, Range(0f, 1f), Tooltip("0 uses one motion token of history; 1 uses the largest history window allowed by the model context.")]
        private float ardyHistoryWeight = 1f;
        [SerializeField, Min(0.01f), Tooltip("ARDY Root2D planning speed limit in meters per second.")]
        private float ardyMaxSpeed = 1.25f;
        [SerializeField, Min(0.01f), Tooltip("ARDY Root2D planning acceleration limit in meters per second squared.")]
        private float ardyMaxAcceleration = 1.5f;
        [SerializeField] private bool allowPartialJoints;
        [SerializeField] private KimodoSegmentTrimTrailSettings segmentTrimTrailSettings = new KimodoSegmentTrimTrailSettings();

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
        private KimodoRuntimeInterruptionPlan interruptionPlan;

        private sealed class KimodoRuntimeInterruptionPlan
        {
            public float SwitchTimeSeconds;
            public KimodoConstraintInternalData FirstFrameConstraint;
        }

        public string StatusMessage => statusMessage;
        public bool IsRunning => generationSession.Running;
        public KimodoSegmentTrimTrailSettings SegmentTrimTrailSettings => segmentTrimTrailSettings;
        public bool DrawDebugSkeleton
        {
            get => drawDebugSkeleton;
            set => drawDebugSkeleton = value;
        }
        internal string DebugModelName => modelName;
        internal Transform DebugProfileSkeletonRoot => motionPlayer?.DebugProfileSkeletonRoot;
        public event Action<KimodoRuntimeSegmentReport> SegmentReady;
        public event Action<KimodoRuntimeSegmentReport> SegmentStarted;
        public event Action<KimodoRuntimeSegmentReport> SegmentCompleted;
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
                verboseLogging,
                out KimodoRuntimeGeneratedSegment startedSegment,
                out KimodoRuntimeGeneratedSegment completedSegment,
                out string playbackError);

            if (!string.IsNullOrWhiteSpace(playbackError))
            {
                UpdateStatus($"Playback failed: {playbackError}");
            }

            ExpireMissedInterruptionPlan();

            if (startedSegment == null)
            {
                MaybeStartNextGeneration(generationSession.LifetimeToken);

                if (completedSegment != null)
                {
                    SegmentCompleted?.Invoke(CreateSegmentReport(completedSegment));
                }

                return;
            }

            if (!KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                constraints.SetTerminal(startedSegment.TerminalConstraint);
            }
            else
            {
                constraints.ClearTerminal();
            }

            // Publish this segment's terminal pose before starting the next
            // generation request; the request snapshots constraints immediately.
            MaybeStartNextGeneration(generationSession.LifetimeToken);

            UpdateStatus($"Playing segment {startedSegment.Index}.");
            SegmentStarted?.Invoke(CreateSegmentReport(startedSegment));

            if (completedSegment != null)
            {
                SegmentCompleted?.Invoke(CreateSegmentReport(completedSegment));
            }

        }

        private void LateUpdate()
        {
            motionPlayer?.ApplyLateRetargetCorrection();
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
                "Generation settings applied. Cancelling current generation.",
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
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (isArdy)
            {
                if (!TryCreateRuntimeRoot2DTarget(
                        new Vector2(worldX, worldZ),
                        includeHeading && worldHeading.HasValue
                            ? KimodoRoot2DPlanner.NormalizeHeading(worldHeading.Value)
                            : (Vector2?)null,
                        includeHeading,
                        maxSpeed,
                        maxAcceleration,
                        Mathf.Max(0f, arrivalThresholdMeters),
                        out KimodoRuntimeRoot2DTarget target,
                        out string targetError))
                {
                    UpdateStatus(targetError);
                    return;
                }

                constraints.StageRoot2DTarget(target);
                UpdateStatus($"Root2D target staged at ({worldX:0.###}, {worldZ:0.###}).");
                return;
            }

            Vector3 current = GetCurrentPositionInternal();
            var targetPosition = new Vector2(worldX, worldZ);
            if (KimodoRoot2DPlanner.HasArrived(current, targetPosition, arrivalThresholdMeters))
            {
                UpdateStatus($"Root2D target already within {Mathf.Max(0f, arrivalThresholdMeters):0.###} m.");
                return;
            }

            float distance = Vector2.Distance(new Vector2(current.x, current.z), targetPosition);
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

            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                if (!TryCreateRuntimeRoot2DTarget(
                        new Vector2(worldX, worldZ),
                        null,
                        false,
                        Mathf.Max(0.01f, ardyMaxSpeed),
                        Mathf.Max(0.01f, ardyMaxAcceleration),
                        0.1f,
                        out KimodoRuntimeRoot2DTarget target,
                        out string targetError))
                {
                    UpdateStatus(targetError);
                    return targetError;
                }

                constraints.StageRoot2DTarget(target);
                ApplyStagedConstraints();
                string targetResult = $"Root2D target staged at ({worldX:0.###}, {worldZ:0.###}).";
                UpdateStatus(targetResult);
                return targetResult;
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
                "Constraints queued. Cancelling current generation.",
                "Constraints queued. Generating constrained segment.");
        }

        public void ClearConstraints()
        {
            constraints.ClearUser();
            generationSession.MarkArdyConstraintsDirty();
            _ = RefreshUpcomingGenerationAsync(
                "Constraints cleared.",
                "Constraints cleared. Cancelling current generation.",
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
            motionPlayer.ClearNextSegment();
            interruptionPlan = null;
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
                motionPlayer.ClearNextSegment();
                interruptionPlan = null;
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
            motionPlayer.ClearNextSegment();
            interruptionPlan = null;
            ResetArdySessionState();
            if (bridgeService != null && !bridgeService.IsDisposed)
            {
                await bridgeService.StopAsync(CancellationToken.None);
            }
            UpdateStatus("Stopped.");
        }

        private void MaybeStartNextGeneration(CancellationToken token)
        {
            if (!generationSession.Running || generationSession.GenerationInFlight || generationSession.GenerationBlocked)
            {
                return;
            }

            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (isArdy && !KimodoRuntimeGenerationSession.ShouldRequestArdyGeneration(
                    motionPlayer.BufferedDurationSeconds,
                    generationSession.ArdyPlaybackReserveSeconds,
                    generationSession.RefreshPending))
            {
                return;
            }

            if (generationSession.RefreshPending || (!isArdy && motionPlayer.HasNextSegment))
            {
                return;
            }

            _ = GenerateNextSegmentAsync(token);
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
                float generationStartedAt = Time.realtimeSinceStartup;

                string prompt = ResolvePrompt();
                bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile ardyProfile);
                KimodoRuntimeInterruptionPlan requestInterruptionPlan = isArdy
                    ? null
                    : interruptionPlan;
                bool sendPrompt = !isArdy || !generationSession.ArdyStarted || generationSession.ArdyPromptDirty;
                bool sendConstraints = !isArdy || !generationSession.ArdyStarted || generationSession.ArdyConstraintsDirty;
                bool sendSettings = isArdy && (!generationSession.ArdyStarted || generationSession.ArdySettingsDirty);
                int consumedPendingRevision = constraints.PendingRevision;
                float generationDuration = ResolveGenerationDurationSeconds();
                string constraintsJson = sendConstraints
                    ? BuildNextConstraintsJson(
                        isArdy,
                        ardyProfile,
                        generationDuration,
                        requestInterruptionPlan?.FirstFrameConstraint)
                    : string.Empty;
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
                        allowPartialJoints,
                        generationToken);
                staleRequest = requestVersion != generationSession.RequestVersion || generationToken.IsCancellationRequested;
                if (KimodoRuntimeGenerationSession.ShouldDiscardResult(
                        isArdy,
                        staleRequest,
                        token.IsCancellationRequested))
                {
                    if (verboseLogging)
                    {
                        Debug.Log($"[KimodoRuntimeMotionDriver] Discard stale segment {requestSegmentIndex} after build.", this);
                    }

                    return;
                }
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
                    float? switchTimeSeconds = requestInterruptionPlan != null &&
                        ReferenceEquals(requestInterruptionPlan, interruptionPlan)
                        ? requestInterruptionPlan.SwitchTimeSeconds
                        : (float?)null;
                    if (!motionPlayer.TrySetNextSegment(generatedSegment, switchTimeSeconds, verboseLogging))
                    {
                        if (verboseLogging)
                        {
                            Debug.Log(
                                $"[KimodoRuntimeMotionDriver] Discard segment {generatedSegment.Index}: next segment already exists.",
                                this);
                        }

                        return;
                    }
                    interruptionPlan = null;
                    generationSession.RecordKimodoGenerationDuration(
                        Mathf.Max(0f, Time.realtimeSinceStartup - generationStartedAt));
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
            float generationDuration,
            KimodoConstraintInternalData firstFrameOverride)
        {
            List<KimodoRuntimeRoot2DConstraint> activeConstraints = constraints.BuildRoot2DForGeneration(
                isArdy,
                isArdy ? motionPlayer.PlaybackTimeAsDouble : 0.0,
                generationDuration);
            // The interruption pose is request-local. Do not assign it back to
            // RuntimeConstraints: that object owns the preceding segment's
            // tail pose for ordinary first-frame continuity.
            KimodoConstraintInternalData terminal = firstFrameOverride ??
                constraints.BuildTerminalForGeneration(isArdy);
            List<KimodoRuntimeRoot2DTarget> activeTargets = constraints.BuildRoot2DTargetsForGeneration(isArdy);
            if (activeConstraints.Count == 0 && activeTargets.Count == 0 && terminal == null)
            {
                return string.Empty;
            }

            float exportFps = isArdy ? ardyProfile.SourceFps : KimodoMotionModelProfiles.DefaultFrameRate;
            string futureConstraints = BuildRuntimeRoot2DConstraintsJson(
                activeConstraints,
                generationDuration,
                exportFps);
            string targetConstraints = BuildRuntimeRoot2DTargetsJson(activeTargets);
            if (!string.IsNullOrWhiteSpace(targetConstraints))
            {
                futureConstraints = targetConstraints;
            }
            if (terminal == null)
            {
                return futureConstraints;
            }

            JArray combined = string.IsNullOrWhiteSpace(futureConstraints)
                ? new JArray()
                : JArray.Parse(futureConstraints);
            JArray internalConstraints = KimodoRawMotionConstraintBuilder.BuildFullBodyConstraints(
                new[] { terminal },
                isArdy ? ardyProfile.SourceFps : KimodoMotionModelProfiles.DefaultFrameRate,
                generationDuration);
            foreach (JToken constraint in internalConstraints)
            {
                combined.Add(constraint);
            }
            return combined.ToString(Formatting.Indented);
        }

        private static string BuildRuntimeRoot2DTargetsJson(
            IReadOnlyList<KimodoRuntimeRoot2DTarget> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return string.Empty;
            }

            var result = new JArray();
            for (int i = 0; i < targets.Count; i++)
            {
                KimodoRuntimeRoot2DTarget target = targets[i];
                if (target == null)
                {
                    continue;
                }

                var item = new JObject
                {
                    ["type"] = KimodoRuntimeConstraints.Root2DTargetType,
                    ["target_root_2d"] = new JArray(target.protocolRoot.x, target.protocolRoot.y),
                    ["max_speed"] = target.maxSpeed,
                    ["max_acceleration"] = target.maxAcceleration,
                    ["arrival_threshold"] = target.arrivalThreshold,
                    ["include_heading"] = target.includeHeading
                };
                if (target.includeHeading && target.hasHeading)
                {
                    item["heading"] = new JArray(target.protocolHeading.x, target.protocolHeading.y);
                }
                result.Add(item);
            }
            return result.Count == 0 ? string.Empty : result.ToString(Formatting.Indented);
        }

        private static string BuildRuntimeRoot2DConstraintsJson(
            IReadOnlyList<KimodoRuntimeRoot2DConstraint> constraints,
            float durationSeconds,
            float exportFps)
        {
            if (constraints == null || constraints.Count == 0)
            {
                return string.Empty;
            }

            var frameIndices = new JArray();
            var roots = new JArray();
            var headings = new JArray();
            bool hasCompleteHeading = true;
            float fps = exportFps > 0f ? exportFps : KimodoMotionModelProfiles.DefaultFrameRate;
            int maxFrame = Mathf.Max(0, KimodoFrameTimeUtility.SecondsToFrameCount(durationSeconds, fps) - 1);
            for (int i = 0; i < constraints.Count; i++)
            {
                KimodoRuntimeRoot2DConstraint constraint = constraints[i];
                if (constraint == null)
                {
                    continue;
                }

                int frame = Mathf.Clamp(
                    KimodoFrameTimeUtility.SecondsToFrameIndex(constraint.sampleTime, fps),
                    0,
                    maxFrame);
                frameIndices.Add(frame);
                roots.Add(new JArray(constraint.protocolRoot.x, constraint.protocolRoot.y));
                if (constraint.hasHeading)
                {
                    headings.Add(new JArray(constraint.protocolHeading.x, constraint.protocolHeading.y));
                }
                else
                {
                    hasCompleteHeading = false;
                }
            }

            if (frameIndices.Count == 0)
            {
                return string.Empty;
            }

            var root2d = new JObject
            {
                ["type"] = KimodoRuntimeConstraints.Root2DType,
                ["frame_indices"] = frameIndices,
                ["smooth_root_2d"] = roots
            };
            if (hasCompleteHeading)
            {
                root2d["global_root_heading"] = headings;
            }

            return new JArray(root2d).ToString(Formatting.Indented);
        }

        private async Task RefreshUpcomingGenerationAsync(
            string inactiveStatus,
            string waitingStatus,
            string generatingStatus)
        {
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (!isArdy)
            {
                interruptionPlan = TryCreateInterruptionPlan();
            }
            generationSession.RequestRefresh();

            if (!isArdy)
            {
                motionPlayer.ClearNextSegment();
            }

            if (!generationSession.IsActive)
            {
                UpdateStatus(inactiveStatus);
                return;
            }

            if (generationSession.GenerationInFlight)
            {
                generationSession.CancelGeneration();
                UpdateStatus(waitingStatus);
                return;
            }

            if (!isArdy && motionPlayer.HasNextSegment)
            {
                UpdateStatus(waitingStatus);
                return;
            }

            UpdateStatus(generatingStatus);
            await GenerateNextSegmentAsync(generationSession.LifetimeToken);
        }

        private KimodoRuntimeInterruptionPlan TryCreateInterruptionPlan()
        {
            if (!generationSession.TryGetKimodoGenerationEstimate(out float estimatedSeconds) ||
                motionPlayer == null ||
                !motionPlayer.TryBuildInterruptionConstraint(
                    motionPlayer.CurrentSegmentTimeSeconds + estimatedSeconds,
                    modelName,
                    out KimodoConstraintInternalData firstFrameConstraint,
                    out float switchTimeSeconds))
            {
                return null;
            }

            return new KimodoRuntimeInterruptionPlan
            {
                SwitchTimeSeconds = switchTimeSeconds,
                FirstFrameConstraint = firstFrameConstraint
            };
        }

        private void ExpireMissedInterruptionPlan()
        {
            if (interruptionPlan == null || motionPlayer == null || motionPlayer.HasNextSegment ||
                motionPlayer.CurrentSegmentTimeSeconds < interruptionPlan.SwitchTimeSeconds)
            {
                return;
            }

            interruptionPlan = null;
            generationSession.RequestRefresh();
            generationSession.CancelGeneration();
            UpdateStatus("Predicted interruption point passed. Regenerating from the next segment start.");
            if (!generationSession.GenerationInFlight)
            {
                _ = GenerateNextSegmentAsync(generationSession.LifetimeToken);
            }
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
                $"Prompt updated: {promptDraft}. Cancelling current generation.",
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
            string result = "Runtime hand/feet effectors are disabled; use KMB FullBody sampling.";
            UpdateStatus(result);
            return result;
        }

        private string StageRoot2DWorldConstraintInternal(
            float worldX,
            float worldZ,
            float durationSeconds,
            Vector2? worldHeading)
        {
            if (!TryCreateRuntimeRoot2DConstraint(
                    new Vector2(worldX, worldZ),
                    worldHeading,
                    ClampConstraintTime(durationSeconds),
                    out KimodoRuntimeRoot2DConstraint sample,
                    out string error))
            {
                UpdateStatus(error);
                return error;
            }

            constraints.StageRoot2D(
                sample,
                KimodoMotionModelProfiles.TryGetArdy(modelName, out _)
                    ? motionPlayer.PlaybackTimeAsDouble
                    : 0.0);
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

        private bool TryCreateRuntimeRoot2DConstraint(
            Vector2 targetWorldPosition,
            Vector2? worldHeading,
            float sampleTime,
            out KimodoRuntimeRoot2DConstraint constraint,
            out string error)
        {
            constraint = null;
            error = string.Empty;
            if (motionPlayer == null ||
                !motionPlayer.EnsureConstraintSkeletonReady(modelName, out error) ||
                motionPlayer.ConstraintRetargetSkeleton == null)
            {
                return false;
            }

            RetargetSkeleton sourceSkeleton = motionPlayer.ConstraintRetargetSkeleton;
            if (!sourceSkeleton.GetBonePose(
                    HumanBodyBones.Hips,
                    out Vector3 sourceRoot,
                    out Quaternion sourceRotation))
            {
                error = "Runtime Root2D requires a sampled KMB hips pose.";
                return false;
            }

            Vector3 currentWorldPosition = GetCurrentPositionInternal();
            Quaternion modelToWorldRotation = ResolveModelToWorldRotation();
            float scale = Mathf.Max(1e-6f, sourceSkeleton.humanScale) /
                Mathf.Max(1e-6f, ResolveTargetHumanScale());
            Vector2 localDelta = KimodoRoot2DPlanner.ToModelOffset(
                currentWorldPosition,
                modelToWorldRotation,
                new Vector3(targetWorldPosition.x, currentWorldPosition.y, targetWorldPosition.y));
            Vector3 neutralRoot = sourceRoot + new Vector3(localDelta.x * scale, 0f, localDelta.y * scale);
            constraint = new KimodoRuntimeRoot2DConstraint
            {
                sampleTime = sampleTime,
                protocolRoot = new Vector2(-neutralRoot.x, neutralRoot.z),
                hasHeading = worldHeading.HasValue
            };

            if (worldHeading.HasValue)
            {
                Vector2 modelHeading = KimodoRoot2DPlanner.ToModelHeading(
                    modelToWorldRotation,
                    worldHeading.Value);
                Quaternion desiredYaw = Quaternion.LookRotation(
                    new Vector3(modelHeading.x, 0f, modelHeading.y),
                    Vector3.up);
                Vector3 forward = KimodoMotionMath.ResolvePlanarHeading(
                    KimodoMotionMath.ApplyPlanarHeading(sourceRotation, desiredYaw)) * Vector3.forward;
                constraint.protocolHeading = new Vector2(forward.z, -forward.x);
            }

            return true;
        }

        private bool TryCreateRuntimeRoot2DTarget(
            Vector2 targetWorldPosition,
            Vector2? worldHeading,
            bool includeHeading,
            float maxSpeed,
            float maxAcceleration,
            float arrivalThreshold,
            out KimodoRuntimeRoot2DTarget target,
            out string error)
        {
            target = null;
            error = string.Empty;
            if (motionPlayer == null ||
                !motionPlayer.EnsureConstraintSkeletonReady(modelName, out error) ||
                motionPlayer.ConstraintRetargetSkeleton == null)
            {
                return false;
            }

            RetargetSkeleton sourceSkeleton = motionPlayer.ConstraintRetargetSkeleton;
            if (!sourceSkeleton.GetBonePose(HumanBodyBones.Hips, out Vector3 sourceRoot, out Quaternion sourceRotation))
            {
                error = "Runtime Root2D requires a sampled KMB hips pose.";
                return false;
            }

            Vector3 currentWorldPosition = GetCurrentPositionInternal();
            Quaternion modelToWorldRotation = ResolveModelToWorldRotation();
            float scale = Mathf.Max(1e-6f, sourceSkeleton.humanScale) /
                Mathf.Max(1e-6f, ResolveTargetHumanScale());
            Vector2 localDelta = KimodoRoot2DPlanner.ToModelOffset(
                currentWorldPosition,
                modelToWorldRotation,
                new Vector3(targetWorldPosition.x, currentWorldPosition.y, targetWorldPosition.y));
            Vector3 neutralRoot = sourceRoot + new Vector3(localDelta.x * scale, 0f, localDelta.y * scale);
            target = new KimodoRuntimeRoot2DTarget
            {
                protocolRoot = new Vector2(-neutralRoot.x, neutralRoot.z),
                maxSpeed = maxSpeed,
                maxAcceleration = maxAcceleration,
                arrivalThreshold = arrivalThreshold,
                includeHeading = includeHeading,
                hasHeading = worldHeading.HasValue
            };

            if (worldHeading.HasValue)
            {
                Vector2 modelHeading = KimodoRoot2DPlanner.ToModelHeading(modelToWorldRotation, worldHeading.Value);
                Quaternion desiredYaw = Quaternion.LookRotation(new Vector3(modelHeading.x, 0f, modelHeading.y), Vector3.up);
                Vector3 forward = KimodoMotionMath.ResolvePlanarHeading(
                    KimodoMotionMath.ApplyPlanarHeading(sourceRotation, desiredYaw)) * Vector3.forward;
                target.protocolHeading = new Vector2(forward.z, -forward.x);
            }
            return true;
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
            Transform modelRoot = primaryTarget != null ? primaryTarget.transform : transform;
            return KimodoMotionMath.ResolvePlanarHeading(modelRoot.rotation);
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
