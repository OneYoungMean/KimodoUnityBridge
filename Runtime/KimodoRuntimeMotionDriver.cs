using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    [AddComponentMenu("Kimodo/Runtime Motion Driver")]
    public sealed class KimodoRuntimeMotionDriver : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private List<Animator> targetHumanoidAnimators = new List<Animator>();

        [Header("Bridge Runtime")]
        [SerializeField] private string modelsRoot = string.Empty;
        [SerializeField] private string modelName = KimodoMotionModelProfiles.DefaultModelName;
        [SerializeField] private KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPerformance;
        [SerializeField] private bool forceCpu;

        [Header("Generation")]
        [SerializeField] private string defaultPrompt = IdlePrompt;
        [SerializeField][Min(1)] private int generationFrames = 150;
        [SerializeField][Min(1)] private int diffusionSteps = 100;
        [SerializeField] private bool randomSeed = true;
        [SerializeField] private int fixedSeed = 42;
        [SerializeField][Min(0.1f)] private float segmentIntervalSeconds = 5f;
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
        private const float MinGenerationDurationSeconds = 1.5f;
        private const float MaxGenerationDurationSeconds = 10f;

        private readonly KimodoRuntimeGenerationSession generationSession =
            new KimodoRuntimeGenerationSession();
        private IKimodoRuntimeGenerationKernel generationKernel;
        private string promptDraft;
        private string statusMessage = "Idle.";
        private readonly KimodoRuntimeConstraints constraints = new KimodoRuntimeConstraints();
        private readonly KimodoRuntimeEffectorConstraintSolver effectorSolver =
            new KimodoRuntimeEffectorConstraintSolver();
        private readonly List<KimodoRuntimeEffectorRequest> pendingEffectorRequests =
            new List<KimodoRuntimeEffectorRequest>();
        private readonly List<Animator> resolvedTargetAnimatorBuffer = new List<Animator>();
        private KimodoBridgeService bridgeService;
        private KimodoRuntimeMotionPlayer motionPlayer;
        private float nextGenerationAllowedTime;
        private int generationFailureCount;

        private sealed class KimodoRuntimeEffectorRequest
        {
            public HumanBodyBones Bone;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool EnableRotation;
            public float RelativeTime;
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
            EnsureGenerationKernel();
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
            effectorSolver.Dispose();
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
            FlushPendingEffectorRequests();

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
            EnsureGenerationKernel();
            promptDraft = string.IsNullOrWhiteSpace(defaultPrompt) ? IdlePrompt : defaultPrompt.Trim();
            if (!KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                ApplyGenerationDurationSeconds(generationFrames / KimodoMotionModelProfiles.DefaultFrameRate);
            }
            else
            {
                generationKernel.PromptChanged();
                generationKernel.SettingsChanged();
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

        /// <summary>Queues a world-space left-hand target at the requested time offset.</summary>
        public void SetLeftHandConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            QueueEffectorConstraint(
                HumanBodyBones.LeftHand,
                new Vector3(worldX, worldY, worldZ),
                Quaternion.identity,
                false,
                duration);
        }

        /// <summary>Queues a world-space right-hand target at the requested time offset.</summary>
        public void SetRightHandConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            QueueEffectorConstraint(
                HumanBodyBones.RightHand,
                new Vector3(worldX, worldY, worldZ),
                Quaternion.identity,
                false,
                duration);
        }

        /// <summary>Queues a world-space left-foot target at the requested time offset.</summary>
        public void SetLeftFootConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            QueueEffectorConstraint(
                HumanBodyBones.LeftFoot,
                new Vector3(worldX, worldY, worldZ),
                Quaternion.identity,
                false,
                duration);
        }

        /// <summary>Queues a world-space right-foot target at the requested time offset.</summary>
        public void SetRightFootConstraint(float worldX, float worldY, float worldZ, float duration = 1f)
        {
            QueueEffectorConstraint(
                HumanBodyBones.RightFoot,
                new Vector3(worldX, worldY, worldZ),
                Quaternion.identity,
                false,
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

            float distance = (KimodoMotionMath.ToPlanar(current) - targetPosition).magnitude;
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
            pendingEffectorRequests.Clear();
            constraints.ClearUser();
            generationKernel?.ConstraintsChanged();
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
            EnsureGenerationKernel();
            pendingEffectorRequests.Clear();
            constraints.Clear();
            generationSession.BeginMotionReset();
            generationFailureCount = 0;
            nextGenerationAllowedTime = 0f;
            try
            {
                if (!generationSession.IsActive)
                {
                    UpdateStatus("Prompt reset.");
                    return;
                }

                if (generationSession.GenerationInFlight)
                {
                    UpdateStatus("Prompt reset. Waiting for current generation to finish.");
                    generationKernel?.CancelGeneration(generationSession);
                    await WaitForGenerationSlotAsync(generationSession.LifetimeToken);
                }

                motionPlayer.Stop();
                motionPlayer.ResetCompletionState();
                motionPlayer.ClearNextSegment();
                if (bridgeService != null && !bridgeService.IsDisposed)
                {
                    await bridgeService.StopAsync(CancellationToken.None);
                    bridgeService.Dispose();
                }
                bridgeService = KimodoBridgeService.CreateOwned();
                EnsureGenerationKernel();
                ResetGenerationKernel();
                CaptureAppliedRuntimeSettings();
                UpdateStatus("Prompt reset. Generating fresh segment.");
                await GenerateNextSegmentAsync(generationSession.LifetimeToken);
            }
            finally
            {
                generationSession.EndMotionReset();
            }
        }

        private async Task StartRuntimeAsync()
        {
            if (!generationSession.TryBeginStart())
            {
                return;
            }

            try
            {
                generationFailureCount = 0;
                nextGenerationAllowedTime = 0f;
                if (!ValidateConfiguration(out string error))
                {
                    UpdateStatus(error);
                    Debug.LogError($"[KimodoRuntimeMotionDriver] {error}", this);
                    return;
                }

                EnsureGenerationKernel();

                if (!effectorSolver.EnsureReady(
                        ResolvePrimaryTargetAnimator(),
                        modelName,
                        out error))
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
                ResetGenerationKernel();
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

            pendingEffectorRequests.Clear();
            constraints.Clear();
            motionPlayer.Stop();
            motionPlayer.ResetCompletionState();
            motionPlayer.ClearNextSegment();
            ResetGenerationKernel();
            if (bridgeService != null && !bridgeService.IsDisposed)
            {
                await bridgeService.StopAsync(CancellationToken.None);
            }
            UpdateStatus("Stopped.");
        }

        private void MaybeStartNextGeneration(CancellationToken token)
        {
            if (!generationSession.Running || generationSession.GenerationInFlight || generationSession.GenerationBlocked ||
                Time.realtimeSinceStartup < nextGenerationAllowedTime ||
                generationKernel == null || !generationKernel.ShouldRequest(generationSession, motionPlayer))
            {
                return;
            }

            _ = GenerateNextSegmentAsync(token);
        }

        private async Task GenerateNextSegmentAsync(CancellationToken token)
        {
            if (Time.realtimeSinceStartup < nextGenerationAllowedTime)
            {
                return;
            }

            IKimodoRuntimeGenerationKernel requestKernel = generationKernel;
            if (requestKernel == null)
            {
                return;
            }

            try
            {
                KimodoRuntimeKernelInput kernelInput = CreateKernelInput(
                    requestKernel,
                    generationSession.SegmentIndex);
                KimodoRuntimeGenerationOutcome outcome = await requestKernel.GenerateAsync(kernelInput, token);
                KimodoRuntimeGeneratedSegment generatedSegment = outcome?.Segment;
                if (generatedSegment == null)
                {
                    return;
                }

                int requestSegmentIndex = outcome.SegmentIndex;
                generationFailureCount = 0;
                nextGenerationAllowedTime = 0f;
                UpdateStatus($"Segment {requestSegmentIndex} ready.");
                try
                {
                    SegmentReady?.Invoke(CreateSegmentReport(generatedSegment));
                }
                catch (Exception subscriberException)
                {
                    Debug.LogException(subscriberException, this);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
                UpdateStatus($"Generate failed: {ex.Message}");
                generationFailureCount = Mathf.Min(generationFailureCount + 1, 6);
                nextGenerationAllowedTime = Time.realtimeSinceStartup + Mathf.Min(30f, Mathf.Pow(2f, generationFailureCount));
            }
            finally
            {
                if (generationSession.ShouldRunPendingRefresh)
                {
                    _ = GenerateNextSegmentAsync(generationSession.LifetimeToken);
                }
            }
        }

        private KimodoRuntimeKernelInput CreateKernelInput(
            IKimodoRuntimeGenerationKernel kernel,
            int segmentIndex)
        {
            KimodoMotionModelProfile ardyProfile = null;
            if (kernel.IsArdy &&
                !KimodoMotionModelProfiles.TryGetArdy(modelName, out ardyProfile))
            {
                throw new InvalidOperationException($"Model '{modelName}' has no ARDY runtime profile.");
            }

            float duration = ResolveGenerationDurationSeconds();
            return new KimodoRuntimeKernelInput
            {
                Session = generationSession,
                BridgeService = bridgeService,
                Constraints = constraints,
                MotionPlayer = motionPlayer,
                ModelName = modelName,
                Prompt = ResolvePrompt(),
                ModelsRoot = modelsRoot,
                TextEncoderMode = textEncoderMode,
                ForceCpu = forceCpu,
                RandomSeed = randomSeed,
                FixedSeed = fixedSeed,
                DiffusionSteps = diffusionSteps,
                GenerationDuration = duration,
                ArdyPlaybackReserveSetting = ardyPlaybackReserveSeconds,
                ArdyAutoHistory = ardyAutoHistory,
                ArdyHistoryWeight = ardyHistoryWeight,
                ArdyProfile = ardyProfile,
                TrimTrail = segmentTrimTrailSettings,
                AllowPartialJoints = allowPartialJoints,
                VerboseLogging = verboseLogging,
                SegmentIndex = segmentIndex,
                OnProgress = OnProgress,
                SetStatus = UpdateStatus
            };
        }

        private async Task RefreshUpcomingGenerationAsync(
            string inactiveStatus,
            string waitingStatus,
            string generatingStatus)
        {
            EnsureGenerationKernel();
            generationKernel.RequestRefresh(CreateKernelInput(generationKernel, generationSession.SegmentIndex), generationSession);

            if (!generationSession.IsActive)
            {
                UpdateStatus(inactiveStatus);
                return;
            }

            if (generationSession.GenerationInFlight)
            {
                generationKernel.CancelGeneration(generationSession);
                UpdateStatus(waitingStatus);
                return;
            }

            UpdateStatus(generatingStatus);
            await GenerateNextSegmentAsync(generationSession.LifetimeToken);
        }

        private void ExpireMissedInterruptionPlan()
        {
            if (generationKernel == null ||
                !generationKernel.ExpireInterruptionPlan(
                    CreateKernelInput(generationKernel, generationSession.SegmentIndex),
                    generationSession))
            {
                return;
            }

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

        private void ResetGenerationKernel()
        {
            generationKernel?.Reset(ardyPlaybackReserveSeconds);
        }

        private void EnsureGenerationKernel()
        {
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            if (generationKernel != null && generationKernel.IsArdy == isArdy)
            {
                return;
            }

            generationKernel = isArdy
                ? (IKimodoRuntimeGenerationKernel)new KimodoRuntimeArdyGenerationKernel()
                : new KimodoRuntimeGenerationKernel();
            generationKernel.Reset(ardyPlaybackReserveSeconds);
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
            generationKernel?.PromptChanged();
            _ = RefreshUpcomingGenerationAsync(
                $"Prompt updated: {promptDraft}",
                $"Prompt updated: {promptDraft}. Cancelling current generation.",
                $"Prompt updated: {promptDraft}. Generating fresh segment.");
            return promptDraft;
        }

        private void QueueEffectorConstraint(
            HumanBodyBones bone,
            Vector3 position,
            Quaternion rotation,
            bool enableRotation,
            float durationSeconds)
        {
            var request = new KimodoRuntimeEffectorRequest
            {
                Bone = bone,
                Position = position,
                Rotation = rotation,
                EnableRotation = enableRotation,
                RelativeTime = ClampConstraintTime(durationSeconds)
            };

            if (!motionPlayer.HasCurrentSegment)
            {
                pendingEffectorRequests.Add(request);
                UpdateStatus("Effector constraint queued until the first segment is playing.");
                return;
            }

            if (!TryBuildAndStageEffector(request, out string error))
            {
                UpdateStatus(error);
                return;
            }

            ApplyStagedConstraints();
        }

        private void FlushPendingEffectorRequests()
        {
            if (motionPlayer == null || !motionPlayer.HasCurrentSegment ||
                pendingEffectorRequests.Count == 0)
            {
                return;
            }

            bool stagedAny = false;
            string firstError = string.Empty;
            while (pendingEffectorRequests.Count > 0)
            {
                KimodoRuntimeEffectorRequest request = pendingEffectorRequests[0];
                pendingEffectorRequests.RemoveAt(0);
                if (TryBuildAndStageEffector(request, out string error))
                {
                    stagedAny = true;
                }
                else if (string.IsNullOrEmpty(firstError))
                {
                    firstError = error;
                }
            }

            if (stagedAny)
            {
                ApplyStagedConstraints();
            }
            if (!string.IsNullOrEmpty(firstError))
            {
                UpdateStatus(firstError);
            }
        }

        private bool TryBuildAndStageEffector(
            KimodoRuntimeEffectorRequest request,
            out string error)
        {
            error = string.Empty;
            if (request == null)
            {
                error = "Runtime effector request is null.";
                return false;
            }

            if (!effectorSolver.TryBuild(
                    ResolvePrimaryTargetAnimator(),
                    modelName,
                    request.Bone,
                    request.Position,
                    request.Rotation,
                    request.EnableRotation,
                    ResolveEffectorSampleTime(request.RelativeTime),
                    out KimodoRuntimeEffectorConstraint constraint,
                    out error))
            {
                return false;
            }

            constraints.StageEffector(constraint);
            return true;
        }

        private double ResolveEffectorSampleTime(float relativeTime)
        {
            if (motionPlayer != null &&
                motionPlayer.HasCurrentSegment &&
                KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                return motionPlayer.PlaybackTimeAsDouble + relativeTime;
            }

            return relativeTime;
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

            generationKernel?.ConstraintsChanged();
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
            ResolveRuntimeRoot2DProjection(
                targetWorldPosition,
                worldHeading,
                currentWorldPosition,
                sourceSkeleton,
                sourceRoot,
                sourceRotation,
                out Vector2 protocolRoot,
                out Vector2 protocolHeading);
            constraint = new KimodoRuntimeRoot2DConstraint
            {
                sampleTime = sampleTime,
                protocolRoot = protocolRoot,
                hasHeading = worldHeading.HasValue
            };

            if (worldHeading.HasValue)
            {
                constraint.protocolHeading = protocolHeading;
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
            ResolveRuntimeRoot2DProjection(
                targetWorldPosition,
                worldHeading,
                currentWorldPosition,
                sourceSkeleton,
                sourceRoot,
                sourceRotation,
                out Vector2 protocolRoot,
                out Vector2 protocolHeading);
            target = new KimodoRuntimeRoot2DTarget
            {
                protocolRoot = protocolRoot,
                maxSpeed = maxSpeed,
                maxAcceleration = maxAcceleration,
                arrivalThreshold = arrivalThreshold,
                includeHeading = includeHeading,
                hasHeading = worldHeading.HasValue
            };

            if (worldHeading.HasValue)
            {
                target.protocolHeading = protocolHeading;
            }
            return true;
        }

        private void ResolveRuntimeRoot2DProjection(
            Vector2 targetWorldPosition,
            Vector2? worldHeading,
            Vector3 currentWorldPosition,
            RetargetSkeleton sourceSkeleton,
            Vector3 sourceRoot,
            Quaternion sourceRotation,
            out Vector2 protocolRoot,
            out Vector2 protocolHeading)
        {
            Quaternion modelToWorldRotation = ResolveModelToWorldRotation();
            float scale = Mathf.Max(1e-6f, sourceSkeleton.humanScale) /
                Mathf.Max(1e-6f, ResolveTargetHumanScale());
            Vector2 localDelta = KimodoRoot2DPlanner.ToModelOffset(
                currentWorldPosition,
                modelToWorldRotation,
                new Vector3(targetWorldPosition.x, currentWorldPosition.y, targetWorldPosition.y));
            Vector3 neutralRoot = sourceRoot + new Vector3(localDelta.x * scale, 0f, localDelta.y * scale);
            protocolRoot = new Vector2(-neutralRoot.x, neutralRoot.z);
            protocolHeading = Vector2.zero;

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
                protocolHeading = new Vector2(forward.z, -forward.x);
            }
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
