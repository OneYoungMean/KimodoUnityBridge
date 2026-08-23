using System;
using System.Threading;
using UnityEngine;

namespace KimodoBridge
{
    internal readonly struct KimodoRuntimeSessionSignature
    {
        internal readonly int Target;
        internal readonly string ModelsRoot;
        internal readonly string ModelName;
        internal readonly KimodoTextEncoderMode TextEncoderMode;
        internal readonly bool ForceCpu;
        internal readonly bool RandomSeed;
        internal readonly int FixedSeed;

        internal KimodoRuntimeSessionSignature(
            int target,
            string modelsRoot,
            string modelName,
            KimodoTextEncoderMode textEncoderMode,
            bool forceCpu,
            bool randomSeed,
            int fixedSeed)
        {
            Target = target;
            ModelsRoot = (modelsRoot ?? string.Empty).Trim();
            ModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            TextEncoderMode = textEncoderMode;
            ForceCpu = forceCpu;
            RandomSeed = randomSeed;
            FixedSeed = fixedSeed;
        }
    }

    internal sealed class KimodoRuntimeGenerationSession : IDisposable
    {
        private CancellationTokenSource lifetimeCts;
        private CancellationTokenSource activeGenerationCts;
        private KimodoRuntimeSessionSignature appliedSignature;
        private bool hasAppliedSignature;

        internal bool Running { get; private set; }
        internal bool StartRequested { get; private set; }
        internal bool GenerationInFlight { get; private set; }
        internal bool GenerationBlocked { get; private set; }
        internal int SegmentIndex { get; private set; }
        internal int RequestVersion { get; private set; }

        internal int? ArdyResolvedSeed { get; private set; }
        internal bool ArdyStarted { get; private set; }
        internal bool ArdyPromptDirty { get; private set; } = true;
        internal bool ArdyConstraintsDirty { get; private set; } = true;
        internal bool ArdySettingsDirty { get; private set; } = true;
        internal bool RefreshPending { get; private set; }
        internal float ArdyPlaybackReserveSeconds { get; private set; } = 1f;
        internal int CompletedKimodoGenerationCount { get; private set; }
        internal float EstimatedKimodoGenerationSeconds { get; private set; }

        internal bool IsActive =>
            Running && lifetimeCts != null && !lifetimeCts.IsCancellationRequested;
        internal CancellationToken LifetimeToken => lifetimeCts?.Token ?? CancellationToken.None;
        internal bool ShouldRunPendingRefresh => IsActive && !GenerationBlocked && RefreshPending;

        internal bool TryBeginStart()
        {
            if (Running || StartRequested)
            {
                return false;
            }

            StartRequested = true;
            return true;
        }

        internal void EndStart() => StartRequested = false;

        internal void Start()
        {
            CancelAndDispose(ref lifetimeCts);
            lifetimeCts = new CancellationTokenSource();
            SegmentIndex = 0;
            RequestVersion = 0;
            GenerationInFlight = false;
            GenerationBlocked = false;
            RefreshPending = false;
            CompletedKimodoGenerationCount = 0;
            EstimatedKimodoGenerationSeconds = 0f;
            Running = true;
        }

        internal void Stop()
        {
            Running = false;
            CancellationTokenSource lifetime = lifetimeCts;
            CancellationTokenSource generation = activeGenerationCts;
            lifetimeCts = null;
            activeGenerationCts = null;
            TryCancel(lifetime);
            TryCancel(generation);
            lifetime?.Dispose();
            generation?.Dispose();
            GenerationInFlight = false;
            GenerationBlocked = false;
            RefreshPending = false;
        }

        internal void BeginMotionReset()
        {
            SegmentIndex = 0;
            RequestVersion++;
            GenerationBlocked = true;
        }

        internal void EndMotionReset() => GenerationBlocked = false;

        internal bool TryBeginGeneration(
            CancellationToken parentToken,
            out CancellationTokenSource generationCts,
            out int requestVersion,
            out int segmentIndex)
        {
            generationCts = null;
            requestVersion = RequestVersion;
            segmentIndex = SegmentIndex;
            if (GenerationInFlight)
            {
                return false;
            }

            generationCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            activeGenerationCts = generationCts;
            GenerationInFlight = true;
            RefreshPending = false;
            return true;
        }

        internal void EndGeneration(CancellationTokenSource generationCts)
        {
            if (ReferenceEquals(activeGenerationCts, generationCts))
            {
                activeGenerationCts = null;
                GenerationInFlight = false;
            }

            generationCts?.Dispose();
        }

        internal void CancelGeneration() => TryCancel(activeGenerationCts);

        internal void AdvanceSegment(int completedSegment) => SegmentIndex = completedSegment + 1;

        internal bool TryGetKimodoGenerationEstimate(out float seconds)
        {
            seconds = EstimatedKimodoGenerationSeconds;
            return CompletedKimodoGenerationCount > 1 && seconds > 0f;
        }

        internal void RecordKimodoGenerationDuration(float seconds)
        {
            float clamped = Mathf.Max(0f, seconds);
            if (CompletedKimodoGenerationCount > 0)
            {
                EstimatedKimodoGenerationSeconds = EstimatedKimodoGenerationSeconds <= 0f
                    ? clamped
                    : Mathf.Lerp(EstimatedKimodoGenerationSeconds, clamped, 0.5f);
            }

            CompletedKimodoGenerationCount++;
        }

        internal void RequestRefresh()
        {
            RequestVersion++;
            RefreshPending = true;
        }

        internal int ResolveRequestSeed(bool isArdy, bool randomSeed, int fixedSeed)
        {
            if (isArdy && ArdyResolvedSeed.HasValue)
            {
                return ArdyResolvedSeed.Value;
            }

            int resolved = randomSeed ? (Guid.NewGuid().GetHashCode() & int.MaxValue) : fixedSeed;
            if (isArdy)
            {
                ArdyResolvedSeed = resolved;
            }

            return resolved;
        }

        internal void CompleteArdyRequest(
            bool sentPrompt,
            bool sentConstraints,
            bool sentSettings,
            bool stale)
        {
            ArdyStarted = true;
            if (stale)
            {
                return;
            }

            if (sentPrompt) ArdyPromptDirty = false;
            if (sentConstraints) ArdyConstraintsDirty = false;
            if (sentSettings) ArdySettingsDirty = false;
        }

        internal void MarkArdyPromptDirty() => ArdyPromptDirty = true;
        internal void MarkArdyConstraintsDirty() => ArdyConstraintsDirty = true;
        internal void MarkArdySettingsDirty() => ArdySettingsDirty = true;

        internal void SetArdyPlaybackReserve(float seconds) =>
            ArdyPlaybackReserveSeconds = Mathf.Max(0.2f, seconds);

        internal void ResetArdy(float playbackReserveSeconds)
        {
            ArdyResolvedSeed = null;
            ArdyStarted = false;
            ArdyPromptDirty = true;
            ArdyConstraintsDirty = true;
            ArdySettingsDirty = true;
            RefreshPending = false;
            SetArdyPlaybackReserve(playbackReserveSeconds);
        }

        internal void Capture(KimodoRuntimeSessionSignature signature)
        {
            appliedSignature = signature;
            hasAppliedSignature = true;
        }

        internal bool TryGetAppliedSignature(out KimodoRuntimeSessionSignature signature)
        {
            signature = appliedSignature;
            return hasAppliedSignature;
        }

        internal static bool ShouldRequestArdyGeneration(
            float bufferedDurationSeconds,
            float playbackReserveSeconds,
            bool refreshPending) =>
            refreshPending || bufferedDurationSeconds <= Mathf.Max(0.2f, playbackReserveSeconds);

        internal static bool ShouldDiscardResult(
            bool isArdy,
            bool staleRequest,
            bool lifetimeCancelled) =>
            lifetimeCancelled || (!isArdy && staleRequest);

        public void Dispose()
        {
            Running = false;
            StartRequested = false;
            GenerationInFlight = false;
            RefreshPending = false;
            TryCancel(lifetimeCts);
            TryCancel(activeGenerationCts);
            lifetimeCts?.Dispose();
            activeGenerationCts?.Dispose();
            lifetimeCts = null;
            activeGenerationCts = null;
        }

        private static void TryCancel(CancellationTokenSource source)
        {
            try
            {
                source?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void CancelAndDispose(ref CancellationTokenSource source)
        {
            TryCancel(source);
            source?.Dispose();
            source = null;
        }
    }
}
