using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeInterruptionPlan
    {
        internal float SwitchTimeSeconds;
        internal KimodoConstraintInternalData FirstFrameConstraint;
    }

    internal sealed class KimodoRuntimeKernelInput
    {
        internal KimodoRuntimeGenerationSession Session;
        internal KimodoBridgeService BridgeService;
        internal KimodoRuntimeConstraints Constraints;
        internal KimodoRuntimeMotionPlayer MotionPlayer;
        internal string ModelName;
        internal string Prompt;
        internal string ModelsRoot;
        internal KimodoTextEncoderMode TextEncoderMode;
        internal bool ForceCpu;
        internal bool RandomSeed;
        internal int FixedSeed;
        internal int DiffusionSteps;
        internal float GenerationDuration;
        internal KimodoRuntimeConstraintSnapshot ConstraintSnapshot;
        internal float ArdyPlaybackReserveSetting;
        internal bool ArdyAutoHistory;
        internal float ArdyHistoryWeight;
        internal KimodoMotionModelProfile ArdyProfile;
        internal KimodoSegmentTrimTrailSettings TrimTrail;
        internal bool AllowPartialJoints;
        internal bool VerboseLogging;
        internal int SegmentIndex;
        internal Action<string> OnProgress;
        internal Action<string> SetStatus;
    }

    internal sealed class KimodoRuntimeGenerationRequest
    {
        internal KimodoGenerationRequestDto Payload;
        internal bool SentPrompt;
        internal bool SentConstraints;
        internal bool SentSettings;
        internal int ResolvedSeed;
    }

    internal sealed class KimodoRuntimeGenerationOutcome
    {
        internal KimodoRuntimeGeneratedSegment Segment;
        internal int SegmentIndex;
    }

    internal interface IKimodoRuntimeGenerationKernel
    {
        bool IsArdy { get; }
        bool ShouldRequest(KimodoRuntimeGenerationSession session, KimodoRuntimeMotionPlayer player);
        void Reset(float playbackReserveSeconds);
        void PromptChanged();
        void ConstraintsChanged();
        void SettingsChanged();
        void RequestRefresh(KimodoRuntimeKernelInput input, KimodoRuntimeGenerationSession session);
        bool ExpireInterruptionPlan(KimodoRuntimeKernelInput input, KimodoRuntimeGenerationSession session);
        bool TryGetGenerationEstimate(out float seconds);
        void RecordGenerationDuration(float seconds);
        void SetPlaybackReserve(float seconds);
        void CancelGeneration(KimodoRuntimeGenerationSession session);
        Task<KimodoRuntimeGenerationOutcome> GenerateAsync(
            KimodoRuntimeKernelInput input,
            CancellationToken token);
        KimodoRuntimeGenerationRequest CreateRequest(
            KimodoRuntimeKernelInput input,
            KimodoRuntimeGenerationSession session);
        bool ShouldDiscardResult(bool staleRequest, bool lifetimeCancelled);
        void ValidateResult(
            KimodoGenerationResultDto result,
            KimodoRuntimeGenerationRequest request,
            KimodoRuntimeKernelInput input);
        bool HasNoMotionResult(KimodoGenerationResultDto result);
        Task<KimodoRuntimeGeneratedSegment> BuildSegmentAsync(
            KimodoGenerationResultDto result,
            KimodoRuntimeKernelInput input,
            CancellationToken token);
        bool TryCommitSegment(
            KimodoGenerationResultDto result,
            KimodoRuntimeGeneratedSegment segment,
            KimodoRuntimeKernelInput input,
            out string error);
        void CompleteRequest(KimodoRuntimeGenerationRequest request, bool staleRequest);
    }

    internal static class KimodoRuntimeGenerationPipeline
    {
        internal static async Task<KimodoRuntimeGenerationOutcome> GenerateAsync(
            IKimodoRuntimeGenerationKernel kernel,
            KimodoRuntimeKernelInput input,
            CancellationToken token)
        {
            KimodoRuntimeGenerationSession session = input?.Session;
            if (kernel == null || input == null || session == null || input.BridgeService == null ||
                input.Constraints == null ||
                !session.TryBeginGeneration(token, out CancellationTokenSource generationCts,
                    out int requestVersion, out int segmentIndex))
            {
                return null;
            }

            try
            {
                input.SegmentIndex = segmentIndex;
                input.ConstraintSnapshot = input.Constraints.CaptureSnapshot();
                int consumedPendingRevision = input.ConstraintSnapshot.PendingRevision;
                float generationStartedAt = Time.realtimeSinceStartup;
                CancellationToken generationToken = generationCts.Token;
                KimodoRuntimeGenerationRequest request = kernel.CreateRequest(input, session);
                input.OnProgress?.Invoke($"Generating segment {segmentIndex}...");
                KimodoGenerationResultDto bridgeResult = await new KimodoBridgeCommand(input.BridgeService)
                    .ExecuteAsync(request.Payload, (_, message) => input.OnProgress?.Invoke(message), generationToken);
                bool staleRequest = requestVersion != session.RequestVersion || generationToken.IsCancellationRequested;
                if (kernel.ShouldDiscardResult(staleRequest, token.IsCancellationRequested))
                {
                    if (input.VerboseLogging)
                    {
                        Debug.Log($"[KimodoRuntimeMotionDriver] Discard stale segment {segmentIndex} generation result.");
                    }
                    return null;
                }

                if (staleRequest && input.VerboseLogging)
                {
                    Debug.Log($"[KimodoRuntimeMotionDriver] Append committed ARDY segment {segmentIndex} before applying the pending stream update.");
                }

                kernel.ValidateResult(bridgeResult, request, input);
                if (kernel.IsArdy && bridgeResult.ardyPlaybackReserveSeconds.HasValue)
                {
                    kernel.SetPlaybackReserve((float)bridgeResult.ardyPlaybackReserveSeconds.Value);
                }
                if (kernel.HasNoMotionResult(bridgeResult))
                {
                    kernel.CompleteRequest(request, staleRequest);
                    input.SetStatus?.Invoke("ARDY cursor synchronized; no new KMB frames were required.");
                    return new KimodoRuntimeGenerationOutcome { SegmentIndex = segmentIndex };
                }

                KimodoRuntimeGeneratedSegment segment = await kernel.BuildSegmentAsync(
                    bridgeResult,
                    input,
                    generationToken);
                staleRequest = requestVersion != session.RequestVersion || generationToken.IsCancellationRequested;
                if (kernel.ShouldDiscardResult(staleRequest, token.IsCancellationRequested))
                {
                    if (input.VerboseLogging)
                    {
                        Debug.Log($"[KimodoRuntimeMotionDriver] Discard stale segment {segmentIndex} after build.");
                    }
                    return null;
                }

                if (!kernel.TryCommitSegment(bridgeResult, segment, input, out string commitError))
                {
                    if (kernel.IsArdy)
                    {
                        throw new InvalidOperationException(commitError);
                    }
                    if (input.VerboseLogging)
                    {
                        Debug.Log($"[KimodoRuntimeMotionDriver] Discard segment {segment?.Index ?? segmentIndex}: {commitError}");
                    }
                    return null;
                }

                kernel.CompleteRequest(request, staleRequest);
                kernel.RecordGenerationDuration(Mathf.Max(0f, Time.realtimeSinceStartup - generationStartedAt));
                input.Constraints.CompleteGeneration(consumedPendingRevision);
                session.AdvanceSegment(segmentIndex);
                return new KimodoRuntimeGenerationOutcome
                {
                    Segment = segment,
                    SegmentIndex = segmentIndex
                };
            }
            finally
            {
                session.EndGeneration(generationCts);
            }
        }
    }

    internal sealed class KimodoRuntimeGenerationKernel : IKimodoRuntimeGenerationKernel
    {
        private int completedGenerationCount;
        private float estimatedGenerationSeconds;
        private KimodoRuntimeInterruptionPlan interruptionPlan;

        public bool IsArdy => false;

        public bool ShouldRequest(KimodoRuntimeGenerationSession session, KimodoRuntimeMotionPlayer player)
        {
            return session != null && player != null && (session.RefreshPending || !player.HasNextSegment);
        }

        public void Reset(float playbackReserveSeconds)
        {
            completedGenerationCount = 0;
            estimatedGenerationSeconds = 0f;
            interruptionPlan = null;
        }

        public void PromptChanged() { }
        public void ConstraintsChanged() { }
        public void SettingsChanged() { }

        public void RequestRefresh(KimodoRuntimeKernelInput input, KimodoRuntimeGenerationSession session)
        {
            interruptionPlan = TryCreateInterruptionPlan(input);
            session?.RequestRefresh();
            input?.MotionPlayer?.ClearNextSegment();
        }

        public bool ExpireInterruptionPlan(
            KimodoRuntimeKernelInput input,
            KimodoRuntimeGenerationSession session)
        {
            if (interruptionPlan == null || input?.MotionPlayer == null ||
                input.MotionPlayer.HasNextSegment ||
                input.MotionPlayer.CurrentSegmentTimeSeconds < interruptionPlan.SwitchTimeSeconds)
            {
                return false;
            }

            interruptionPlan = null;
            session?.RequestRefresh();
            CancelGeneration(session);
            return true;
        }

        public bool TryGetGenerationEstimate(out float seconds)
        {
            seconds = estimatedGenerationSeconds;
            return completedGenerationCount > 1 && seconds > 0f;
        }

        public void RecordGenerationDuration(float seconds)
        {
            float clamped = Mathf.Max(0f, seconds);
            if (completedGenerationCount > 0)
            {
                estimatedGenerationSeconds = estimatedGenerationSeconds <= 0f
                    ? clamped
                    : Mathf.Lerp(estimatedGenerationSeconds, clamped, 0.5f);
            }

            completedGenerationCount++;
        }

        public void SetPlaybackReserve(float seconds) { }

        public void CancelGeneration(KimodoRuntimeGenerationSession session)
        {
            session?.CancelGeneration();
        }

        public Task<KimodoRuntimeGenerationOutcome> GenerateAsync(
            KimodoRuntimeKernelInput input,
            CancellationToken token) =>
            KimodoRuntimeGenerationPipeline.GenerateAsync(this, input, token);

        public KimodoRuntimeGenerationRequest CreateRequest(
            KimodoRuntimeKernelInput input,
            KimodoRuntimeGenerationSession session)
        {
            int seed = session.ResolveRequestSeed(input.RandomSeed, input.FixedSeed);
            KimodoConstraintInternalData firstFrame = interruptionPlan?.FirstFrameConstraint;
            KimodoRuntimeConstraintSnapshot snapshot = input.ConstraintSnapshot ??
                input.Constraints?.CaptureSnapshot();
            string constraints = KimodoRuntimeConstraintEncoder.BuildKimodo(
                snapshot,
                input.GenerationDuration,
                firstFrame);
            return new KimodoRuntimeGenerationRequest
            {
                SentPrompt = true,
                SentConstraints = true,
                ResolvedSeed = seed,
                Payload = CreatePayload(
                    input,
                    input.Prompt,
                    input.GenerationDuration,
                    constraints,
                    KimodoMotionModelProfiles.NormalizeName(input.ModelName),
                    KimodoMotionModelProfiles.ClampDiffusionSteps(input.ModelName, input.DiffusionSteps),
                    seed)
            };
        }

        public bool ShouldDiscardResult(bool staleRequest, bool lifetimeCancelled)
        {
            return lifetimeCancelled || staleRequest;
        }

        public void ValidateResult(
            KimodoGenerationResultDto result,
            KimodoRuntimeGenerationRequest request,
            KimodoRuntimeKernelInput input)
        {
        }

        public bool HasNoMotionResult(KimodoGenerationResultDto result) => false;

        public Task<KimodoRuntimeGeneratedSegment> BuildSegmentAsync(
            KimodoGenerationResultDto result,
            KimodoRuntimeKernelInput input,
            CancellationToken token)
        {
            return KimodoRuntimeSegmentBuilder.BuildAsync(
                result,
                input.ModelName,
                input.Prompt,
                input.SegmentIndex,
                false,
                input.TrimTrail,
                input.AllowPartialJoints,
                token);
        }

        public bool TryCommitSegment(
            KimodoGenerationResultDto result,
            KimodoRuntimeGeneratedSegment segment,
            KimodoRuntimeKernelInput input,
            out string error)
        {
            error = string.Empty;
            if (segment == null || input?.MotionPlayer == null)
            {
                error = "Kimodo generation produced an empty segment.";
                return false;
            }

            float? switchTime = interruptionPlan?.SwitchTimeSeconds;
            if (!input.MotionPlayer.TrySetNextSegment(segment, switchTime, input.VerboseLogging))
            {
                error = $"Kimodo segment {segment.Index} could not be queued.";
                return false;
            }

            interruptionPlan = null;
            return true;
        }

        public void CompleteRequest(KimodoRuntimeGenerationRequest request, bool staleRequest) { }

        private KimodoRuntimeInterruptionPlan TryCreateInterruptionPlan(KimodoRuntimeKernelInput input)
        {
            if (!TryGetGenerationEstimate(out float estimate) || input?.MotionPlayer == null ||
                !input.MotionPlayer.TryBuildInterruptionConstraint(
                    input.MotionPlayer.CurrentSegmentTimeSeconds + estimate,
                    input.ModelName,
                    out KimodoConstraintInternalData firstFrame,
                    out float switchTime))
            {
                return null;
            }

            return new KimodoRuntimeInterruptionPlan
            {
                SwitchTimeSeconds = switchTime,
                FirstFrameConstraint = firstFrame
            };
        }

        private static KimodoGenerationRequestDto CreatePayload(
            KimodoRuntimeKernelInput input,
            string prompt,
            float? duration,
            string constraintsJson,
            string modelName,
            int steps,
            int seed)
        {
            return new KimodoGenerationRequestDto
            {
                prompt = prompt,
                duration = duration,
                seed = seed,
                steps = steps,
                constraints = new KimodoConstraintPayload { json = constraintsJson },
                model = modelName,
                text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(input.TextEncoderMode),
                simulate_free_vram_gb = input.ForceCpu ? 0 : (int?)null,
                models_root = string.IsNullOrWhiteSpace(input.ModelsRoot)
                    ? string.Empty
                    : Path.GetFullPath(input.ModelsRoot.Trim())
            };
        }
    }

    internal sealed class KimodoRuntimeArdyGenerationKernel : IKimodoRuntimeGenerationKernel
    {
        private int? resolvedSeed;
        private bool started;
        private bool promptDirty = true;
        private bool constraintsDirty = true;
        private bool settingsDirty = true;
        private float playbackReserveSeconds = 1f;

        public bool IsArdy => true;

        public bool ShouldRequest(KimodoRuntimeGenerationSession session, KimodoRuntimeMotionPlayer player)
        {
            return session != null && player != null &&
                (session.RefreshPending ||
                    player.BufferedDurationSeconds <= Mathf.Max(0.2f, playbackReserveSeconds));
        }

        public void Reset(float playbackReserveSeconds)
        {
            resolvedSeed = null;
            started = false;
            promptDirty = true;
            constraintsDirty = true;
            settingsDirty = true;
            SetPlaybackReserve(playbackReserveSeconds);
        }

        public void PromptChanged() => promptDirty = true;
        public void ConstraintsChanged() => constraintsDirty = true;
        public void SettingsChanged() => settingsDirty = true;

        public void RequestRefresh(KimodoRuntimeKernelInput input, KimodoRuntimeGenerationSession session)
        {
            session?.RequestRefresh();
        }

        public bool ExpireInterruptionPlan(
            KimodoRuntimeKernelInput input,
            KimodoRuntimeGenerationSession session) => false;

        public bool TryGetGenerationEstimate(out float seconds)
        {
            seconds = 0f;
            return false;
        }

        public void RecordGenerationDuration(float seconds) { }

        public void SetPlaybackReserve(float seconds)
        {
            playbackReserveSeconds = Mathf.Max(0.2f, seconds);
        }

        public void CancelGeneration(KimodoRuntimeGenerationSession session)
        {
            session?.CancelGeneration();
        }

        public Task<KimodoRuntimeGenerationOutcome> GenerateAsync(
            KimodoRuntimeKernelInput input,
            CancellationToken token) =>
            KimodoRuntimeGenerationPipeline.GenerateAsync(this, input, token);

        public KimodoRuntimeGenerationRequest CreateRequest(
            KimodoRuntimeKernelInput input,
            KimodoRuntimeGenerationSession session)
        {
            bool sendPrompt = !started || promptDirty;
            bool sendConstraints = !started || constraintsDirty;
            bool sendSettings = !started || settingsDirty;
            if (!resolvedSeed.HasValue)
            {
                resolvedSeed = input.RandomSeed
                    ? (Guid.NewGuid().GetHashCode() & int.MaxValue)
                    : input.FixedSeed;
            }

            KimodoRuntimeConstraintSnapshot snapshot = input.ConstraintSnapshot ??
                input.Constraints?.CaptureSnapshot();
            string constraints = sendConstraints
                ? KimodoRuntimeConstraintEncoder.BuildArdy(
                    snapshot,
                    input.MotionPlayer.PlaybackTimeAsDouble,
                    input.GenerationDuration,
                    input.ArdyProfile.SourceFps)
                : string.Empty;
            var payload = new KimodoGenerationRequestDto
            {
                ardy_session_update_only = started && !sendSettings,
                prompt = sendPrompt ? input.Prompt : null,
                duration = null,
                time_as_double = input.MotionPlayer.PlaybackTimeAsDouble,
                seed = resolvedSeed.Value,
                steps = KimodoMotionModelProfiles.ResolveArdyProtocolSteps(
                    input.DiffusionSteps,
                    input.ArdyProfile),
                constraints = new KimodoConstraintPayload
                {
                    json = string.IsNullOrWhiteSpace(constraints) ? "[]" : constraints
                },
                model = input.ArdyProfile.ModelName,
                text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(input.TextEncoderMode),
                simulate_free_vram_gb = input.ForceCpu ? 0 : (int?)null,
                models_root = string.IsNullOrWhiteSpace(input.ModelsRoot)
                    ? string.Empty
                    : Path.GetFullPath(input.ModelsRoot.Trim()),
                ardy_history_weight = sendSettings && !input.ArdyAutoHistory
                    ? Mathf.Clamp01(input.ArdyHistoryWeight)
                    : (double?)null,
                ardy_playback_reserve_seconds = sendSettings
                    ? Mathf.Max(0.2f, input.ArdyPlaybackReserveSetting)
                    : (double?)null
            };
            return new KimodoRuntimeGenerationRequest
            {
                Payload = payload,
                SentPrompt = sendPrompt,
                SentConstraints = sendConstraints,
                SentSettings = sendSettings,
                ResolvedSeed = resolvedSeed.Value
            };
        }

        public bool ShouldDiscardResult(bool staleRequest, bool lifetimeCancelled)
        {
            return lifetimeCancelled;
        }

        public void ValidateResult(
            KimodoGenerationResultDto result,
            KimodoRuntimeGenerationRequest request,
            KimodoRuntimeKernelInput input)
        {
            KimodoRuntimeSegmentBuilder.ValidateArdyResult(
                result,
                input.ArdyProfile,
                request.ResolvedSeed);
        }

        public bool HasNoMotionResult(KimodoGenerationResultDto result) => result?.motionData == null;

        public Task<KimodoRuntimeGeneratedSegment> BuildSegmentAsync(
            KimodoGenerationResultDto result,
            KimodoRuntimeKernelInput input,
            CancellationToken token)
        {
            return KimodoRuntimeSegmentBuilder.BuildAsync(
                result,
                input.ModelName,
                input.Prompt,
                input.SegmentIndex,
                true,
                input.TrimTrail,
                input.AllowPartialJoints,
                token);
        }

        public bool TryCommitSegment(
            KimodoGenerationResultDto result,
            KimodoRuntimeGeneratedSegment segment,
            KimodoRuntimeKernelInput input,
            out string error)
        {
            if (segment == null || input?.MotionPlayer == null)
            {
                error = "ARDY generation produced an empty segment.";
                return false;
            }

            return input.MotionPlayer.ReplaceArdy(
                segment,
                result.startFrame,
                input.VerboseLogging,
                out error);
        }

        public void CompleteRequest(KimodoRuntimeGenerationRequest request, bool staleRequest)
        {
            started = true;
            if (staleRequest || request == null)
            {
                return;
            }

            if (request.SentPrompt) promptDirty = false;
            if (request.SentConstraints) constraintsDirty = false;
            if (request.SentSettings) settingsDirty = false;
        }
    }
}
