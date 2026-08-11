using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CharacterAnimationCli.Unity.Command
{
    internal enum command_status
    {
        None = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Canceled = 4
    }

    internal sealed class command_generation_session
    {
        public Guid RequestId;
        public string TargetKey = string.Empty;
        public command_kind Kind;
        public KimodoBridgeCommandStage Stage;
        public string Message = string.Empty;
        public string Error = string.Empty;
        public command_status Status;
        public command_result Payload;
        public DateTime StartedAtUtc;

        public bool IsRunning => Status == command_status.Running;
        public bool IsCompleted => Status == command_status.Completed;
        public bool IsFailed => Status == command_status.Failed;
        public bool IsCanceled => Status == command_status.Canceled;
    }

    [InitializeOnLoad]
    internal static class command_generation_runner
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<UnityEngine.Object, RunningSessionState> SessionsByTarget =
            new Dictionary<UnityEngine.Object, RunningSessionState>();
        private static readonly Dictionary<Guid, RunningSessionState> SessionsByRequest =
            new Dictionary<Guid, RunningSessionState>();
        private static int reloadLockCount;

        static command_generation_runner()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => CancelAll("Generation canceled: assembly reload.");
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += () => CancelAll("Generation canceled: editor quitting.");
            EditorSceneManager.activeSceneChangedInEditMode += (_, _) => CancelAll("Generation canceled: active scene changed.");
        }

        public static bool Start(
            UnityEngine.Object target,
            string targetKey,
            command_kind kind,
            Func<command_generation_session, CancellationToken, Task<command_result>> executeAsync,
            out command_generation_session session,
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
                AcquireReloadLock();
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

        public static bool TryGet(UnityEngine.Object target, out command_generation_session session)
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
                    removed.Session.Status = command_status.Canceled;
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
                session.Status = command_status.Running;
                session.Stage = stage;
                session.Message = message ?? string.Empty;
                session.Error = string.Empty;
            });
        }

        public static void Complete(
            UnityEngine.Object target,
            Guid requestId,
            command_result payload,
            string message)
        {
            Mutate(target, requestId, session =>
            {
                if (!session.IsRunning)
                {
                    return;
                }

                session.Status = command_status.Completed;
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

                session.Status = command_status.Failed;
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

                session.Status = command_status.Canceled;
                session.Message = string.IsNullOrWhiteSpace(reason) ? "Generation canceled." : reason;
                session.Error = string.Empty;
            });
        }

        private static async Task ExecuteAsync(
            RunningSessionState state,
            Func<command_generation_session, CancellationToken, Task<command_result>> executeAsync)
        {
            try
            {
                command_result payload = await executeAsync(state.Session, state.Token);
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
                command_context.PersistGenerationJobStatus(state.Session);
                lock (Sync)
                {
                    SessionsByRequest.Remove(state.Session.RequestId);
                }

                state.Dispose();
                lock (Sync)
                {
                    ReleaseReloadLock();
                }
            }
        }

        private static void Mutate(
            UnityEngine.Object target,
            Guid requestId,
            Action<command_generation_session> mutate)
        {
            if (target == null || mutate == null)
            {
                return;
            }

            command_generation_session changed = null;
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
                changed = state.Session;
            }
            command_context.PersistGenerationJobStatus(changed);
        }

        private static void AcquireReloadLock()
        {
            if (reloadLockCount++ == 0)
            {
                EditorApplication.LockReloadAssemblies();
            }
        }

        private static void ReleaseReloadLock()
        {
            if (reloadLockCount <= 0)
            {
                reloadLockCount = 0;
                return;
            }
            if (--reloadLockCount == 0)
            {
                EditorApplication.UnlockReloadAssemblies();
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

            public RunningSessionState(UnityEngine.Object target, string targetKey, command_kind kind)
            {
                Target = target;
                CancellationTokenSource = new CancellationTokenSource();
                Session = new command_generation_session
                {
                    RequestId = Guid.NewGuid(),
                    TargetKey = string.IsNullOrWhiteSpace(targetKey) ? "global" : targetKey,
                    Kind = kind,
                    Stage = KimodoBridgeCommandStage.None,
                    Message = "Queued.",
                    Error = string.Empty,
                    Status = command_status.Running,
                    Payload = null,
                    StartedAtUtc = DateTime.UtcNow
                };
            }

            public UnityEngine.Object Target { get; }

            public command_generation_session Session { get; }

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
