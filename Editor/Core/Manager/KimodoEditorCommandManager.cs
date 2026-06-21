using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    public static class KimodoEditorCommandManager
    {
        public static event Action<KimodoEditorCommandProgressEvent> CommandProgress;
        public static event Action<KimodoEditorCommandCompletedEvent> CommandCompleted;
        public static event Action<KimodoEditorCommandFailedEvent> CommandFailed;
        public static event Action<KimodoEditorCommandCanceledEvent> CommandCanceled;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, RunningCommandState> RunningByTarget = new Dictionary<string, RunningCommandState>(StringComparer.Ordinal);
        private static readonly Dictionary<Guid, RunningCommandState> RunningByRequest = new Dictionary<Guid, RunningCommandState>();

        static KimodoEditorCommandManager()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            AssemblyReloadEvents.beforeAssemblyReload += CancelAllRunning;
            EditorApplication.quitting += CancelAllRunning;
        }

        public static bool Dispatch(IKimodoEditorCommand command)
        {
            if (command == null)
            {
                return false;
            }

            if (command is CancelPlayableClipGenerationCommand cancelClip)
            {
                return Cancel(cancelClip.Clip == null ? cancelClip.TargetKey : "clip:" + cancelClip.Clip.GetInstanceID());
            }

            if (command.Kind == KimodoEditorCommandKind.GeneratePlayableClip && EditorCompilationStateGate.IsCompilingOrReloading)
            {
                EmitFailed(
                    command,
                    "Editor is compiling or reloading scripts.",
                    new InvalidOperationException("Editor is compiling or reloading scripts."));
                return false;
            }

            RunningCommandState state = new RunningCommandState(command);
            lock (Sync)
            {
                if (RunningByTarget.ContainsKey(command.TargetKey))
                {
                    string message = $"A command is already running for target '{command.TargetKey}'.";
                    EmitFailed(command, message, new InvalidOperationException(message));
                    return false;
                }

                RunningByTarget[command.TargetKey] = state;
                RunningByRequest[command.RequestId] = state;
            }

            EmitProgress(command, "Dispatched.");
            _ = ExecuteAsync(state);
            return true;
        }

        public static bool Cancel(string targetKey)
        {
            if (string.IsNullOrWhiteSpace(targetKey))
            {
                return false;
            }

            RunningCommandState state;
            lock (Sync)
            {
                if (!RunningByTarget.TryGetValue(targetKey, out state))
                {
                    return false;
                }
            }

            state.RequestCancel();
            return true;
        }

        public static bool Cancel(Guid requestId)
        {
            RunningCommandState state;
            lock (Sync)
            {
                if (!RunningByRequest.TryGetValue(requestId, out state))
                {
                    return false;
                }
            }

            state.RequestCancel();
            return true;
        }

        private static async Task ExecuteAsync(RunningCommandState state)
        {
            IKimodoEditorCommand command = state.Command;
            try
            {
                switch (command)
                {
                    case GeneratePlayableClipCommand cmd:
                        await HandleGeneratePlayableClipAsync(state, cmd);
                        break;
                    case GenerateFromPromptCommand cmd:
                        await HandleGenerateFromPromptAsync(state, cmd);
                        break;
                    case BridgeControlCommand cmd:
                        await HandleBridgeControlAsync(state, cmd);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported command type: {command.GetType().Name}");
                }
            }
            catch (OperationCanceledException)
            {
                EmitCanceled(command, "Canceled.");
            }
            catch (Exception ex)
            {
                EmitFailed(command, ex.Message, ex);
            }
            finally
            {
                CleanupState(state);
            }
        }

        private static async Task HandleGeneratePlayableClipAsync(RunningCommandState state, GeneratePlayableClipCommand command)
        {
            await ExecuteGeneratePlayableClipAsync(state, command, command.Clip, command.PromptOverride, command.ExternalConstraint);
        }

        private static async Task HandleGenerateFromPromptAsync(RunningCommandState state, GenerateFromPromptCommand command)
        {
            UnityEngine.Object obj = EditorUtility.InstanceIDToObject(command.ClipInstanceId);
            if (!(obj is KimodoPlayableClip clip))
            {
                throw new InvalidOperationException("KimodoPlayableClip not found for command target.");
            }

            await ExecuteGeneratePlayableClipAsync(state, command, clip, command.PromptOverride, command.ExternalConstraint);
        }

        private static async Task ExecuteGeneratePlayableClipAsync(
            RunningCommandState state,
            IKimodoEditorCommand eventCommand,
            KimodoPlayableClip clip,
            string promptOverride,
            KimodoExternalConstraintRequest externalConstraint)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            state.Token.ThrowIfCancellationRequested();
            EmitProgress(eventCommand, "Generating and baking...", KimodoGeneratePipelineStage.Validate);

            string prompt = string.IsNullOrWhiteSpace(promptOverride) ? (clip.motionPrompt ?? string.Empty) : promptOverride.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new InvalidOperationException("Prompt is empty.");
            }

            state.Token.ThrowIfCancellationRequested();
            if (!string.Equals(clip.motionPrompt, prompt, StringComparison.Ordinal))
            {
                clip.motionPrompt = prompt;
                EditorUtility.SetDirty(clip);
            }

            KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                clip,
                prompt,
                externalConstraint,
                state.Token);
            try
            {
                request.Progress = (stage, message) => EmitProgress(eventCommand, message, stage);

                KimodoEditorGenerateResult result = await KimodoEditorGeneratePipelineOrchestrator.ExecuteAsync(request);
                state.Token.ThrowIfCancellationRequested();
                KimodoPlayableClipGenerationHostService.FinalizeGeneration(clip, request, result);

                EmitCompleted(eventCommand, result);
            }
            catch
            {
                KimodoPlayableClipGenerationHostService.CleanupFailedGeneration(request);
                throw;
            }
        }

        private static async Task HandleBridgeControlAsync(RunningCommandState state, BridgeControlCommand command)
        {
            if (EditorCompilationStateGate.IsCompilingOrReloading)
            {
                throw new InvalidOperationException("Editor is compiling or reloading scripts.");
            }

            string runtimeRoot = string.IsNullOrWhiteSpace(command.RuntimeRoot)
                ? KimodoBridgeController.GetRuntimeRootPath()
                : command.RuntimeRoot;
            string modelName = string.IsNullOrWhiteSpace(command.ModelName)
                ? "Kimodo-SOMA-RP-v1"
                : command.ModelName.Trim();
            bool highVram = command.VramMode == KimodoBridgeVramMode.High;
            string modelsRoot = command.ModelsRootOverride?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(modelsRoot))
            {
                modelsRoot = Path.GetFullPath(modelsRoot);
            }

            switch (command.Operation)
            {
                case KimodoBridgeOperation.Start:
                    {
                        EmitProgress(command, "Starting server...");
                        string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(runtimeRoot);
                        await KimodoBridgeController.StartServerAsync(
                            launcherPath,
                            modelName,
                            highVram,
                            runtimeRoot,
                            modelsRoot,
                            forceSetup: false,
                            progress => EmitProgress(command, progress),
                            state.Token);
                        break;
                    }
                case KimodoBridgeOperation.Stop:
                    EmitProgress(command, "Stopping server...");
                    await KimodoBridgeController.CloseServerAsync();
                    break;
                case KimodoBridgeOperation.TryFix:
                    EmitProgress(command, "Running TryFix...");
                    using (KimodoBridgeController.EnterRuntimeMaintenanceScope())
                    {
                        await KimodoBridgeController.CloseServerAsync();
                        if (Directory.Exists(runtimeRoot))
                        {
                            Directory.Delete(runtimeRoot, recursive: true);
                        }

                        if (!KimodoBridgeController.BootstrapRuntimeRootIfMissing())
                        {
                            throw new InvalidOperationException("TryFix failed: cannot bootstrap runtime.");
                        }

                        string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(runtimeRoot);
                        await KimodoBridgeController.StartServerAsync(
                            launcherPath,
                            modelName,
                            highVram,
                            runtimeRoot,
                            modelsRoot,
                            forceSetup: true,
                            progress => EmitProgress(command, progress),
                            state.Token);
                    }
                    break;
                case KimodoBridgeOperation.DeleteAllData:
                    EmitProgress(command, "Deleting runtime data...");
                    await KimodoBridgeController.CloseServerAsync();
                    if (Directory.Exists(runtimeRoot))
                    {
                        Directory.Delete(runtimeRoot, recursive: true);
                    }
                    break;
                case KimodoBridgeOperation.RefreshStatus:
                    EmitProgress(command, "Refreshing bridge status...");
                    break;
                case KimodoBridgeOperation.EnsureRuntimeRoot:
                    EmitProgress(command, "Creating runtime root...");
                    if (!KimodoBridgeController.BootstrapRuntimeRootIfMissing())
                    {
                        throw new InvalidOperationException("Failed to create runtime root from package template.");
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported bridge operation: {command.Operation}");
            }

            ServerStatusSnapshot snapshot = KimodoBridgeController.GetServerStatusSnapshot();
            if (command.Operation == KimodoBridgeOperation.Stop && snapshot.Running)
            {
                throw new InvalidOperationException(
                    $"Bridge stop requested but server still running at {snapshot.Host}:{snapshot.Port}.");
            }

            EmitCompleted(command, new KimodoEditorBridgeOperationResult
            {
                Running = snapshot.Running,
                HasPort = snapshot.HasPort,
                Host = snapshot.Host,
                Port = snapshot.Port,
                Status = snapshot.Ready ? "Ready" : "Pending",
                Error = string.Empty
            });
        }

        private static void CancelAllRunning()
        {
            CancelRunning(null);
        }

        private static void OnCompilationStarted(object _)
        {
            CancelRunning(state => state.Command.Kind == KimodoEditorCommandKind.GeneratePlayableClip);
        }

        private static void CancelRunning(Func<RunningCommandState, bool> predicate)
        {
            RunningCommandState[] snapshot;
            lock (Sync)
            {
                List<RunningCommandState> states = new List<RunningCommandState>(RunningByTarget.Count);
                foreach (KeyValuePair<string, RunningCommandState> kv in RunningByTarget)
                {
                    if (predicate == null || predicate(kv.Value))
                    {
                        states.Add(kv.Value);
                    }
                }

                snapshot = states.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i]?.RequestCancel();
            }
        }

        private static void CleanupState(RunningCommandState state)
        {
            lock (Sync)
            {
                RunningByTarget.Remove(state.Command.TargetKey);
                RunningByRequest.Remove(state.Command.RequestId);
            }

            state.Dispose();
        }

        private static void EmitProgress(IKimodoEditorCommand command, string message, KimodoGeneratePipelineStage stage = KimodoGeneratePipelineStage.None)
        {
            CommandProgress?.Invoke(new KimodoEditorCommandProgressEvent(command, message, stage));
        }

        private static void EmitCompleted(IKimodoEditorCommand command, IKimodoEditorCommandResult payload)
        {
            CommandCompleted?.Invoke(new KimodoEditorCommandCompletedEvent(command, payload));
        }

        private static void EmitFailed(IKimodoEditorCommand command, string message, Exception exception)
        {
            if (exception != null)
            {
                Debug.LogException(exception);
            }
            else
            {
                Debug.LogError(message);
            }
            //Debug.Log(message);
            CommandFailed?.Invoke(new KimodoEditorCommandFailedEvent(command, message, exception));
        }

        private static void EmitCanceled(IKimodoEditorCommand command, string reason)
        {
            CommandCanceled?.Invoke(new KimodoEditorCommandCanceledEvent(command, reason));
        }

        private sealed class RunningCommandState : IDisposable
        {
            private int disposed;

            public RunningCommandState(IKimodoEditorCommand command)
            {
                Command = command;
                CancellationTokenSource = new CancellationTokenSource();
            }

            public IKimodoEditorCommand Command { get; }

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
                    // Ignore cancellation errors.
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
