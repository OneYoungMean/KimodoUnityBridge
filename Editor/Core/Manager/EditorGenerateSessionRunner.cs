using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal enum KimodoEditorRequestStatus
    {
        None = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Canceled = 4
    }

    internal sealed class EditorGenerateSession
    {
        public Guid RequestId;
        public string TargetKey = string.Empty;
        public KimodoEditorCommandKind Kind;
        public KimodoBridgeCommandStage Stage;
        public string Message = string.Empty;
        public string Error = string.Empty;
        public KimodoEditorRequestStatus Status;
        public IKimodoEditorCommandResult Payload;
        public DateTime StartedAtUtc;

        public bool IsRunning => Status == KimodoEditorRequestStatus.Running;
        public bool IsCompleted => Status == KimodoEditorRequestStatus.Completed;
        public bool IsFailed => Status == KimodoEditorRequestStatus.Failed;
        public bool IsCanceled => Status == KimodoEditorRequestStatus.Canceled;
    }

    [InitializeOnLoad]
    internal static class EditorGenerateSessionRunner
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<UnityEngine.Object, RunningSessionState> SessionsByTarget =
            new Dictionary<UnityEngine.Object, RunningSessionState>();
        private static readonly Dictionary<Guid, RunningSessionState> SessionsByRequest =
            new Dictionary<Guid, RunningSessionState>();

        static EditorGenerateSessionRunner()
        {
            CompilationPipeline.compilationStarted += _ => CancelAll("Generation canceled: compilation started.");
            AssemblyReloadEvents.beforeAssemblyReload += () => CancelAll("Generation canceled: assembly reload.");
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += () => CancelAll("Generation canceled: editor quitting.");
        }

        public static bool Start(
            UnityEngine.Object target,
            string targetKey,
            KimodoEditorCommandKind kind,
            Func<EditorGenerateSession, CancellationToken, Task<IKimodoEditorCommandResult>> executeAsync,
            out EditorGenerateSession session,
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

            RunningSessionState state;
            lock (Sync)
            {
                if (SessionsByTarget.TryGetValue(target, out RunningSessionState existing) &&
                    existing != null &&
                    existing.Session != null &&
                    existing.Session.IsRunning)
                {
                    error = $"A generation session is already running for '{targetKey ?? target.name}'.";
                    session = existing.Session;
                    return false;
                }

                state = new RunningSessionState(target, targetKey, kind);
                SessionsByTarget[target] = state;
                SessionsByRequest[state.Session.RequestId] = state;
                session = state.Session;
            }

            _ = ExecuteAsync(state, executeAsync);
            return true;
        }

        public static bool Cancel(UnityEngine.Object target, string reason = "Generation canceled.")
        {
            if (target == null)
            {
                return false;
            }

            RunningSessionState state;
            lock (Sync)
            {
                if (!SessionsByTarget.TryGetValue(target, out state) ||
                    state == null ||
                    state.Session == null ||
                    !state.Session.IsRunning)
                {
                    return false;
                }
            }

            CancelState(state, reason);
            return true;
        }

        public static bool Cancel(Guid requestId, string reason = "Generation canceled.")
        {
            RunningSessionState state;
            lock (Sync)
            {
                if (!SessionsByRequest.TryGetValue(requestId, out state) ||
                    state == null ||
                    state.Session == null ||
                    !state.Session.IsRunning)
                {
                    return false;
                }
            }

            CancelState(state, reason);
            return true;
        }

        public static void CancelAll(string reason = "Generation canceled.")
        {
            RunningSessionState[] snapshot;
            lock (Sync)
            {
                var states = new List<RunningSessionState>(SessionsByTarget.Count);
                foreach (KeyValuePair<UnityEngine.Object, RunningSessionState> pair in SessionsByTarget)
                {
                    if (pair.Value?.Session != null && pair.Value.Session.IsRunning)
                    {
                        states.Add(pair.Value);
                    }
                }

                snapshot = states.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                CancelState(snapshot[i], reason);
            }
        }

        public static bool TryGet(UnityEngine.Object target, out EditorGenerateSession session)
        {
            session = null;
            if (target == null)
            {
                return false;
            }

            lock (Sync)
            {
                if (!SessionsByTarget.TryGetValue(target, out RunningSessionState state) ||
                    state == null)
                {
                    return false;
                }

                session = state.Session;
                return session != null;
            }
        }

        public static void Clear(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            RunningSessionState removed = null;
            lock (Sync)
            {
                if (!SessionsByTarget.TryGetValue(target, out removed) || removed == null)
                {
                    return;
                }

                if (removed.Session != null && removed.Session.IsRunning)
                {
                    removed.Session.Status = KimodoEditorRequestStatus.Canceled;
                    removed.Session.Message = "Generation canceled.";
                    removed.Session.Error = string.Empty;
                    removed.RequestCancel();
                }

                SessionsByTarget.Remove(target);
                SessionsByRequest.Remove(removed.Session.RequestId);
            }

            removed.Dispose();
        }

        public static void UpdateProgress(
            UnityEngine.Object target,
            Guid requestId,
            KimodoBridgeCommandStage stage,
            string message)
        {
            Mutate(target, requestId, session =>
            {
                session.Status = KimodoEditorRequestStatus.Running;
                session.Stage = stage;
                session.Message = message ?? string.Empty;
                session.Error = string.Empty;
            });
        }

        public static void Complete(
            UnityEngine.Object target,
            Guid requestId,
            IKimodoEditorCommandResult payload,
            string message)
        {
            Mutate(target, requestId, session =>
            {
                if (!session.IsRunning)
                {
                    return;
                }

                session.Status = KimodoEditorRequestStatus.Completed;
                session.Stage = KimodoBridgeCommandStage.Completed;
                session.Message = message ?? string.Empty;
                session.Error = string.Empty;
                session.Payload = payload;
            });
        }

        public static void Fail(
            UnityEngine.Object target,
            Guid requestId,
            string error)
        {
            Mutate(target, requestId, session =>
            {
                if (!session.IsRunning)
                {
                    return;
                }

                session.Status = KimodoEditorRequestStatus.Failed;
                session.Message = "Generation failed.";
                session.Error = error ?? string.Empty;
            });
        }

        public static void Cancel(
            UnityEngine.Object target,
            Guid requestId,
            string reason)
        {
            Mutate(target, requestId, session =>
            {
                if (!session.IsRunning)
                {
                    return;
                }

                session.Status = KimodoEditorRequestStatus.Canceled;
                session.Message = string.IsNullOrWhiteSpace(reason) ? "Generation canceled." : reason;
                session.Error = string.Empty;
            });
        }

        private static async Task ExecuteAsync(
            RunningSessionState state,
            Func<EditorGenerateSession, CancellationToken, Task<IKimodoEditorCommandResult>> executeAsync)
        {
            try
            {
                IKimodoEditorCommandResult payload = await executeAsync(state.Session, state.Token);
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
                lock (Sync)
                {
                    SessionsByRequest.Remove(state.Session.RequestId);
                }

                state.Dispose();
            }
        }

        private static void Mutate(
            UnityEngine.Object target,
            Guid requestId,
            Action<EditorGenerateSession> mutate)
        {
            if (target == null || mutate == null)
            {
                return;
            }

            lock (Sync)
            {
                if (!SessionsByTarget.TryGetValue(target, out RunningSessionState state) ||
                    state == null ||
                    state.Session == null ||
                    state.Session.RequestId != requestId)
                {
                    return;
                }

                mutate(state.Session);
            }
        }

        private static void CancelState(RunningSessionState state, string reason)
        {
            if (state == null || state.Session == null)
            {
                return;
            }

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

            public RunningSessionState(UnityEngine.Object target, string targetKey, KimodoEditorCommandKind kind)
            {
                Target = target;
                CancellationTokenSource = new CancellationTokenSource();
                Session = new EditorGenerateSession
                {
                    RequestId = Guid.NewGuid(),
                    TargetKey = string.IsNullOrWhiteSpace(targetKey) ? "global" : targetKey,
                    Kind = kind,
                    Stage = KimodoBridgeCommandStage.None,
                    Message = "Queued.",
                    Error = string.Empty,
                    Status = KimodoEditorRequestStatus.Running,
                    Payload = null,
                    StartedAtUtc = DateTime.UtcNow
                };
            }

            public UnityEngine.Object Target { get; }

            public EditorGenerateSession Session { get; }

            public CancellationTokenSource CancellationTokenSource { get; }

            public CancellationToken Token => CancellationTokenSource.Token;

            public void RequestCancel()
            {
                try
                {
                    if (!CancellationTokenSource.IsCancellationRequested)
                    {
                        CancellationTokenSource.Cancel();
                    }
                }
                catch
                {
                    // Ignore cancellation races.
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                CancellationTokenSource.Dispose();
            }
        }
    }
}
