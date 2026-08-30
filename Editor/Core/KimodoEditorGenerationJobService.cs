using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal enum KimodoEditorGenerationJobStatus
    {
        None = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Canceled = 4
    }

    internal sealed class KimodoEditorGenerationJobSession
    {
        public Guid RequestId;
        public KimodoBridgeCommandStage Stage;
        public string Message = string.Empty;
        public string Error = string.Empty;
        public KimodoEditorGenerationJobStatus Status;
        public KimodoEditorGenerationResult Payload;
        public DateTime StartedAtUtc;
        public double? EstimatedSecondsRemaining;
        public string EstimatedCompletionUtc = string.Empty;
        public int ProgressCurrent;
        public int ProgressTotal;
        public double? ProgressRate;

        public bool IsRunning => Status == KimodoEditorGenerationJobStatus.Running;
    }

    [InitializeOnLoad]
    internal static class KimodoEditorGenerationJobService
    {
        private static readonly ConcurrentDictionary<Guid, RunningSessionState> SessionsByRequest =
            new ConcurrentDictionary<Guid, RunningSessionState>();

        static KimodoEditorGenerationJobService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => CancelAll("Generation canceled: assembly reload.");
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += () => CancelAll("Generation canceled: editor quitting.");
            EditorSceneManager.activeSceneChangedInEditMode += (_, _) => CancelAll("Generation canceled: active scene changed.");
        }

        internal static bool Start(
            UnityEngine.Object target,
            Func<KimodoEditorGenerationJobSession, CancellationToken, Task<KimodoEditorGenerationResult>> executeAsync,
            Action<KimodoEditorGenerationJobSession> statusChanged,
            out KimodoEditorGenerationJobSession session,
            out string error)
        {
            session = null;
            error = string.Empty;
            if (target == null)
            {
                error = "Generation target is null.";
                return false;
            }
            if (executeAsync == null)
            {
                error = "Generation callback is null.";
                return false;
            }

            var state = new RunningSessionState(target, statusChanged);
            if (!SessionsByRequest.TryAdd(state.Session.RequestId, state))
            {
                state.Dispose();
                error = "Could not register the generation request.";
                return false;
            }

            session = state.Session;
            _ = ExecuteAsync(state, executeAsync);
            return true;
        }

        internal static bool Cancel(UnityEngine.Object target, string reason = "Generation canceled.")
        {
            if (target == null) return false;
            RunningSessionState[] states = SnapshotForTarget(target)
                .Where(state => state?.Session != null && state.Session.IsRunning)
                .ToArray();
            for (int i = 0; i < states.Length; i++) CancelState(states[i], reason);
            return states.Length > 0;
        }

        internal static bool Cancel(Guid requestId, string reason = "Generation canceled.")
        {
            if (!SessionsByRequest.TryGetValue(requestId, out RunningSessionState state) ||
                state?.Session == null || !state.Session.IsRunning)
            {
                return false;
            }

            CancelState(state, reason);
            return true;
        }

        internal static void CancelAll(string reason = "Generation canceled.")
        {
            RunningSessionState[] snapshot = SessionsByRequest.Values
                .Where(state => state?.Session != null && state.Session.IsRunning)
                .ToArray();
            for (int i = 0; i < snapshot.Length; i++) CancelState(snapshot[i], reason);
        }

        internal static bool TryGet(UnityEngine.Object target, out KimodoEditorGenerationJobSession session)
        {
            session = null;
            if (target == null) return false;
            RunningSessionState[] states = SnapshotForTarget(target);
            RunningSessionState state = states
                .Where(item => item?.Session != null && item.Session.IsRunning)
                .OrderByDescending(item => item.Session.StartedAtUtc)
                .FirstOrDefault()
                ?? states
                    .Where(item => item?.Session != null)
                    .OrderByDescending(item => item.Session.StartedAtUtc)
                    .FirstOrDefault();
            session = state?.Session;
            return session != null;
        }

        internal static void Clear(UnityEngine.Object target)
        {
            if (target == null) return;
            RunningSessionState[] removed = SnapshotForTarget(target);
            foreach (RunningSessionState state in removed)
            {
                if (state?.Session != null && state.Session.IsRunning)
                {
                    state.Session.Status = KimodoEditorGenerationJobStatus.Canceled;
                    state.Session.Message = "Generation canceled.";
                    state.Session.Error = string.Empty;
                    state.RequestCancel();
                }
                if (state?.Session != null) SessionsByRequest.TryRemove(state.Session.RequestId, out _);
            }
            foreach (RunningSessionState state in removed) state?.Dispose();
        }

        internal static void UpdateProgress(
            UnityEngine.Object target,
            Guid requestId,
            KimodoBridgeCommandStage stage,
            string message)
        {
            Mutate(target, requestId, session =>
            {
                session.Status = KimodoEditorGenerationJobStatus.Running;
                session.Stage = stage;
                session.Message = message ?? string.Empty;
                session.Error = string.Empty;
                UpdateEstimate(session);
            });
        }

        private static void UpdateEstimate(KimodoEditorGenerationJobSession session)
        {
            string message = session.Message ?? string.Empty;
            Match eta = Regex.Match(message, @"ETA\s*(?:=|:)?\s*([0-9]+(?:\.[0-9]+)?)\s*s", RegexOptions.IgnoreCase);
            if (eta.Success && double.TryParse(eta.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            {
                session.EstimatedSecondsRemaining = Math.Max(0d, seconds);
                session.EstimatedCompletionUtc = DateTime.UtcNow.AddSeconds(seconds).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            }
            Match progress = Regex.Match(message, @"(?:Generation progress:\s*)?(\d+)\s*/\s*(\d+).*?(?:@\s*)?([0-9]+(?:\.[0-9]+)?)\s*(?:it/s|frames?/s)", RegexOptions.IgnoreCase);
            if (progress.Success && int.TryParse(progress.Groups[1].Value, out int current) &&
                int.TryParse(progress.Groups[2].Value, out int total) &&
                double.TryParse(progress.Groups[3].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double rate))
            {
                session.ProgressCurrent = current;
                session.ProgressTotal = total;
                session.ProgressRate = rate;
            }
            else if (session.Stage == KimodoBridgeCommandStage.InvokeBackend &&
                     message.IndexOf("ETA", StringComparison.OrdinalIgnoreCase) < 0)
            {
                session.EstimatedSecondsRemaining = null;
                session.EstimatedCompletionUtc = string.Empty;
            }
        }

        private static void Complete(
            UnityEngine.Object target,
            Guid requestId,
            KimodoEditorGenerationResult payload,
            string message)
        {
            Mutate(target, requestId, session =>
            {
                if (!session.IsRunning) return;
                session.Status = KimodoEditorGenerationJobStatus.Completed;
                session.Stage = KimodoBridgeCommandStage.Completed;
                session.Message = message ?? string.Empty;
                session.Error = string.Empty;
                session.Payload = payload;
                session.EstimatedSecondsRemaining = 0d;
                session.EstimatedCompletionUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            });
        }

        private static void Fail(UnityEngine.Object target, Guid requestId, string error)
        {
            Mutate(target, requestId, session =>
            {
                if (!session.IsRunning) return;
                session.Status = KimodoEditorGenerationJobStatus.Failed;
                session.Message = "Generation failed.";
                session.Error = error ?? string.Empty;
                session.EstimatedSecondsRemaining = null;
                session.EstimatedCompletionUtc = string.Empty;
            });
        }

        private static void Cancel(UnityEngine.Object target, Guid requestId, string reason)
        {
            Mutate(target, requestId, session =>
            {
                if (!session.IsRunning) return;
                session.Status = KimodoEditorGenerationJobStatus.Canceled;
                session.Message = string.IsNullOrWhiteSpace(reason) ? "Generation canceled." : reason;
                session.Error = string.Empty;
                session.EstimatedSecondsRemaining = null;
                session.EstimatedCompletionUtc = string.Empty;
            });
        }

        private static async Task ExecuteAsync(
            RunningSessionState state,
            Func<KimodoEditorGenerationJobSession, CancellationToken, Task<KimodoEditorGenerationResult>> executeAsync)
        {
            try
            {
                KimodoEditorGenerationResult payload = await executeAsync(state.Session, state.Token);
                state.Token.ThrowIfCancellationRequested();
                Complete(state.Target, state.Session.RequestId, payload, "Generation complete.");
            }
            catch (OperationCanceledException)
            {
                Cancel(state.Target, state.Session.RequestId, "Generation canceled.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Fail(state.Target, state.Session.RequestId, ex.Message);
            }
            finally
            {
                SessionsByRequest.TryRemove(state.Session.RequestId, out _);
                state.Dispose();
            }
        }

        private static void Mutate(
            UnityEngine.Object target,
            Guid requestId,
            Action<KimodoEditorGenerationJobSession> mutate)
        {
            if (target == null || mutate == null) return;
            if (!SessionsByRequest.TryGetValue(requestId, out RunningSessionState state) ||
                state == null || state.Target != target || state.Session == null)
            {
                return;
            }

            mutate(state.Session);
            state.StatusChanged?.Invoke(state.Session);
        }

        private static RunningSessionState[] SnapshotForTarget(UnityEngine.Object target)
        {
            return target == null
                ? Array.Empty<RunningSessionState>()
                : SessionsByRequest.Values.Where(state => state != null && state.Target == target).ToArray();
        }

        private static void CancelState(RunningSessionState state, string reason)
        {
            if (state?.Session == null) return;
            Cancel(state.Target, state.Session.RequestId, reason);
            state.RequestCancel();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                CancelAll("Generation canceled: entering runtime.");
            }
        }

        private sealed class RunningSessionState : IDisposable
        {
            private int disposed;

            public RunningSessionState(
                UnityEngine.Object target,
                Action<KimodoEditorGenerationJobSession> statusChanged)
            {
                Target = target;
                CancellationTokenSource = new CancellationTokenSource();
                StatusChanged = statusChanged;
                Session = new KimodoEditorGenerationJobSession
                {
                    RequestId = Guid.NewGuid(),
                    Stage = KimodoBridgeCommandStage.None,
                    Message = "Queued.",
                    Error = string.Empty,
                    Status = KimodoEditorGenerationJobStatus.Running,
                    StartedAtUtc = DateTime.UtcNow
                };
            }

            public UnityEngine.Object Target { get; }
            public KimodoEditorGenerationJobSession Session { get; }
            public CancellationTokenSource CancellationTokenSource { get; }
            public Action<KimodoEditorGenerationJobSession> StatusChanged { get; }
            public CancellationToken Token => CancellationTokenSource.Token;

            public void RequestCancel()
            {
                try
                {
                    if (!CancellationTokenSource.IsCancellationRequested) CancellationTokenSource.Cancel();
                }
                catch
                {
                    // Ignore cancellation races.
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                CancellationTokenSource.Dispose();
            }
        }
    }
}
