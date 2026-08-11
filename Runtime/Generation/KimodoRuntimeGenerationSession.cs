using System;
using System.Threading;
using System.Threading.Tasks;
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
        private Task schedulerTask;
        private KimodoRuntimeSessionSignature appliedSignature;
        private bool hasAppliedSignature;

        internal bool Running { get; private set; }
        internal bool StartRequested { get; private set; }
        internal bool GenerationInFlight { get; private set; }
        internal bool GenerationBlocked { get; private set; }
        internal int SegmentIndex { get; private set; }
        internal int RequestVersion { get; private set; }
        internal int LastWaitStatusSegment { get; private set; } = -1;

        internal int? ArdyResolvedSeed { get; private set; }
        internal bool ArdyStarted { get; private set; }
        internal bool ArdyPromptDirty { get; private set; } = true;
        internal bool ArdyConstraintsDirty { get; private set; } = true;
        internal bool ArdySettingsDirty { get; private set; } = true;
        internal bool ArdyRefreshPending { get; private set; }
        internal float ArdyPlaybackReserveSeconds { get; private set; } = 1f;

        internal bool IsActive =>
            Running && lifetimeCts != null && !lifetimeCts.IsCancellationRequested;
        internal CancellationToken LifetimeToken => lifetimeCts?.Token ?? CancellationToken.None;
        internal bool ShouldRunPendingRefresh => IsActive && !GenerationBlocked && ArdyRefreshPending;

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

        internal void Start(Func<CancellationToken, Task> scheduler)
        {
            CancelAndDispose(ref lifetimeCts);
            lifetimeCts = new CancellationTokenSource();
            SegmentIndex = 0;
            RequestVersion = 0;
            GenerationInFlight = false;
            GenerationBlocked = false;
            LastWaitStatusSegment = -1;
            Running = true;
            schedulerTask = scheduler(lifetimeCts.Token);
        }

        internal async Task StopAsync()
        {
            Running = false;
            CancellationTokenSource lifetime = lifetimeCts;
            CancellationTokenSource generation = activeGenerationCts;
            Task scheduler = schedulerTask;
            lifetimeCts = null;
            activeGenerationCts = null;
            schedulerTask = null;
            TryCancel(lifetime);
            TryCancel(generation);

            try
            {
                if (scheduler != null)
                {
                    await scheduler;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lifetime?.Dispose();
                generation?.Dispose();
                GenerationInFlight = false;
                GenerationBlocked = false;
                LastWaitStatusSegment = -1;
            }
        }

        internal void BeginMotionReset()
        {
            SegmentIndex = 0;
            RequestVersion++;
            LastWaitStatusSegment = -1;
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

        internal void Fail() => Running = false;

        internal void AdvanceSegment(int completedSegment) => SegmentIndex = completedSegment + 1;

        internal bool ShouldReportWait()
        {
            if (LastWaitStatusSegment == SegmentIndex)
            {
                return false;
            }

            LastWaitStatusSegment = SegmentIndex;
            return true;
        }

        internal void ClearWaitStatus() => LastWaitStatusSegment = -1;

        internal void RequestRefresh(bool isArdy)
        {
            LastWaitStatusSegment = -1;
            if (!isArdy)
            {
                return;
            }

            RequestVersion++;
            ArdyRefreshPending = true;
        }

        internal void BeginArdyRequest() => ArdyRefreshPending = false;

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
            ArdyRefreshPending = false;
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
            TryCancel(lifetimeCts);
            TryCancel(activeGenerationCts);
            lifetimeCts?.Dispose();
            activeGenerationCts?.Dispose();
            lifetimeCts = null;
            activeGenerationCts = null;
            schedulerTask = null;
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
