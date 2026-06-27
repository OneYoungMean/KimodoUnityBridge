using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoBridge
{
    public sealed class BridgeLogPump : IDisposable
    {
        private const int StopWaitTimeoutMs = 1500;
        private const int BridgeMessageLogPumpWaitFileTimeoutMs = 60000;
        private const int BridgeMessageLogPumpMissingFilePollMs = 90;
        private const int MaxBufferedLines = 200;

        private static readonly object SharedGate = new object();
        private static readonly Dictionary<string, SharedRuntimeState> SharedStates = new Dictionary<string, SharedRuntimeState>(StringComparer.OrdinalIgnoreCase);

        private readonly object gate = new object();
        private CancellationTokenSource cts;
        private Task pumpTask;
        private SynchronizationContext callbackContext;
        private bool disposed;

        public void Start(string logPath, Action<string> onLine, BridgeRuntimeSettings settings = null)
        {
            Start(logPath, onLine, settings, null, null, null, null);
        }

        public void Start(
            string logPath,
            Action<string> onLine,
            BridgeRuntimeSettings settings,
            int? waitFileTimeoutMsOverride,
            int? missingFilePollMinMsOverride,
            int? missingFilePollMaxMsOverride,
            bool? readFromStartOverride)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(logPath) || onLine == null)
            {
                return;
            }

            int waitFileTimeoutMs = waitFileTimeoutMsOverride ?? settings?.logPumpWaitFileTimeoutMs ?? BridgeRuntimeSettings.DefaultLogPumpWaitFileTimeoutMs;
            int missingFilePollMinMs = missingFilePollMinMsOverride ?? settings?.logPumpMissingFilePollMinMs ?? BridgeRuntimeSettings.DefaultLogPumpMissingFilePollMinMs;
            int missingFilePollMaxMs = missingFilePollMaxMsOverride ?? settings?.logPumpMissingFilePollMaxMs ?? BridgeRuntimeSettings.DefaultLogPumpMissingFilePollMaxMs;
            int idlePollMinMs = settings?.logPumpIdlePollMinMs ?? BridgeRuntimeSettings.DefaultLogPumpIdlePollMinMs;
            int idlePollMaxMs = settings?.logPumpIdlePollMaxMs ?? BridgeRuntimeSettings.DefaultLogPumpIdlePollMaxMs;
            bool readFromStart = readFromStartOverride ?? false;

            var newCts = new CancellationTokenSource();
            SynchronizationContext newContext = SynchronizationContext.Current;
            Task newTask = Task.Run(() => PumpAsync(
                logPath,
                onLine,
                newCts.Token,
                newContext,
                Math.Max(1000, waitFileTimeoutMs),
                Math.Max(30, missingFilePollMinMs),
                Math.Max(Math.Max(30, missingFilePollMinMs), missingFilePollMaxMs),
                Math.Max(10, idlePollMinMs),
                Math.Max(Math.Max(10, idlePollMinMs), idlePollMaxMs),
                readFromStart));

            lock (gate)
            {
                cts = newCts;
                callbackContext = newContext;
                pumpTask = newTask;
            }
        }

        public void Stop()
        {
            CancellationTokenSource currentCts;
            Task currentPumpTask;
            lock (gate)
            {
                currentCts = cts;
                currentPumpTask = pumpTask;
                cts = null;
                pumpTask = null;
                callbackContext = null;
            }

            if (currentCts != null)
            {
                try { currentCts.Cancel(); } catch { }
            }

            _ = ObserveStopAsync(currentPumpTask, currentCts, StopWaitTimeoutMs, CancellationToken.None);
        }

        public async Task StopAsync(int timeoutMs = StopWaitTimeoutMs, CancellationToken token = default)
        {
            CancellationTokenSource currentCts;
            Task currentPumpTask;
            lock (gate)
            {
                currentCts = cts;
                currentPumpTask = pumpTask;
                cts = null;
                pumpTask = null;
                callbackContext = null;
            }

            if (currentCts != null)
            {
                try { currentCts.Cancel(); } catch { }
            }

            await ObserveStopAsync(currentPumpTask, currentCts, timeoutMs, token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Stop();
        }

        internal static IDisposable SubscribeShared(string runtimeRoot, BridgeRuntimeSettings settings, Action<string> onLine)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot) || onLine == null)
            {
                return NoopDisposable.Instance;
            }

            string key = NormalizeRuntimeRoot(runtimeRoot);
            SharedRuntimeState state;
            SharedSubscription subscription;
            List<string> replayLines;
            lock (SharedGate)
            {
                if (!SharedStates.TryGetValue(key, out state))
                {
                    state = new SharedRuntimeState(key, settings);
                    SharedStates[key] = state;
                }

                subscription = new SharedSubscription(state, onLine);
                state.Subscriptions.Add(subscription);
                replayLines = state.BufferedLines.Count > 0 ? new List<string>(state.BufferedLines) : null;
                if (!state.Started)
                {
                    StartSharedState(state);
                }
            }

            if (replayLines != null)
            {
                for (int i = 0; i < replayLines.Count; i++)
                {
                    subscription.Emit(replayLines[i]);
                }
            }

            return subscription;
        }

        internal static void StopSharedRuntime(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return;
            }

            SharedRuntimeState state = null;
            lock (SharedGate)
            {
                string key = NormalizeRuntimeRoot(runtimeRoot);
                if (!SharedStates.TryGetValue(key, out state))
                {
                    return;
                }

                SharedStates.Remove(key);
                state.Subscriptions.Clear();
            }

            StopSharedState(state);
        }

        private static async Task ObserveStopAsync(Task currentPumpTask, CancellationTokenSource currentCts, int timeoutMs, CancellationToken token)
        {
            try
            {
                if (currentPumpTask != null)
                {
                    Task completed = await Task.WhenAny(currentPumpTask, Task.Delay(Math.Max(10, timeoutMs), token)).ConfigureAwait(false);
                    if (completed != currentPumpTask)
                    {
                        Debug.LogWarning("[KimodoBridge][LogPump] stop timeout.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[KimodoBridge][LogPump] stop observe failed: {e.Message}");
            }
            finally
            {
                if (currentCts != null)
                {
                    try { currentCts.Dispose(); } catch { }
                }
            }
        }

        private static async Task PumpAsync(
            string logPath,
            Action<string> onLine,
            CancellationToken token,
            SynchronizationContext callbackContext,
            int waitFileTimeoutMs,
            int missingFilePollMinMs,
            int missingFilePollMaxMs,
            int idlePollMinMs,
            int idlePollMaxMs,
            bool readFromStart)
        {
            try
            {
                while (!token.IsCancellationRequested && !File.Exists(logPath))
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }

                if (!File.Exists(logPath))
                {
                    return;
                }

                OpenReader(logPath, readFromStart, out FileStream fs, out StreamReader reader, out DateTime openedWriteTimeUtc, out long openedLength);
                int idleDelayMs = idlePollMinMs;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (ShouldReopenForRotation(logPath, fs, openedWriteTimeUtc, openedLength))
                        {
                            try { reader.Dispose(); } catch { }
                            try { fs.Dispose(); } catch { }
                            OpenReader(logPath, readFromStart, out fs, out reader, out openedWriteTimeUtc, out openedLength);
                            idleDelayMs = idlePollMinMs;
                            continue;
                        }

                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line != null)
                        {
                            string trimmed = line.Trim();
                            if (!string.IsNullOrWhiteSpace(trimmed))
                            {
                                EmitLine(onLine, callbackContext, trimmed);
                            }

                            openedWriteTimeUtc = SafeGetLastWriteTimeUtc(logPath, openedWriteTimeUtc);
                            openedLength = SafeGetLength(logPath, fs.Length);
                            idleDelayMs = idlePollMinMs;
                            continue;
                        }

                        if (fs.CanSeek && fs.Length < fs.Position)
                        {
                            fs.Seek(0, SeekOrigin.Begin);
                            reader.DiscardBufferedData();
                            openedWriteTimeUtc = SafeGetLastWriteTimeUtc(logPath, openedWriteTimeUtc);
                            openedLength = SafeGetLength(logPath, fs.Length);
                            idleDelayMs = idlePollMinMs;
                            continue;
                        }

                        if (fs.Length > fs.Position)
                        {
                            string tailChunk = await reader.ReadToEndAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(tailChunk))
                            {
                                string[] parts = tailChunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                for (int i = 0; i < parts.Length; i++)
                                {
                                    string trimmed = parts[i].Trim();
                                    if (!string.IsNullOrWhiteSpace(trimmed))
                                    {
                                        EmitLine(onLine, callbackContext, trimmed);
                                    }
                                }
                            }

                            openedWriteTimeUtc = SafeGetLastWriteTimeUtc(logPath, openedWriteTimeUtc);
                            openedLength = SafeGetLength(logPath, fs.Length);
                            idleDelayMs = idlePollMinMs;
                            continue;
                        }

                        await Task.Delay(idleDelayMs, token).ConfigureAwait(false);
                        idleDelayMs = Math.Min(idlePollMaxMs, idleDelayMs + idlePollMinMs);
                    }
                }
                finally
                {
                    try { reader.Dispose(); } catch { }
                    try { fs.Dispose(); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception e)
            {
                EmitLine(onLine, callbackContext, $"[BridgeLogPump] stopped: {e.Message}");
            }
        }

        private static void OpenReader(
            string logPath,
            bool readFromStart,
            out FileStream fs,
            out StreamReader reader,
            out DateTime openedWriteTimeUtc,
            out long openedLength)
        {
            fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.CanSeek && !readFromStart)
            {
                fs.Seek(0, SeekOrigin.End);
            }

            reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            openedWriteTimeUtc = SafeGetLastWriteTimeUtc(logPath, DateTime.MinValue);
            openedLength = SafeGetLength(logPath, fs.Length);
        }

        private static bool ShouldReopenForRotation(string logPath, FileStream fs, DateTime openedWriteTimeUtc, long openedLength)
        {
            if (fs == null || string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(logPath);
                if (!info.Exists)
                {
                    return false;
                }

                if (fs.CanSeek && fs.Position < fs.Length)
                {
                    return false;
                }

                return info.LastWriteTimeUtc != openedWriteTimeUtc || info.Length != openedLength;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime SafeGetLastWriteTimeUtc(string path, DateTime fallback)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static long SafeGetLength(string path, long fallback)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static void EmitLine(Action<string> onLine, SynchronizationContext callbackContext, string line)
        {
            if (onLine == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (callbackContext != null)
            {
                callbackContext.Post(_ =>
                {
                    try
                    {
                        onLine(line);
                    }
                    catch
                    {
                        // ignore callback failures
                    }
                }, null);
                return;
            }

            try
            {
                onLine(line);
            }
            catch
            {
                // ignore callback failures
            }
        }

        private static void UnsubscribeShared(SharedSubscription subscription)
        {
            SharedRuntimeState state = subscription?.State;
            if (state == null)
            {
                return;
            }

            bool shouldStop = false;
            lock (SharedGate)
            {
                state.Subscriptions.Remove(subscription);
                if (state.Subscriptions.Count == 0)
                {
                    SharedStates.Remove(state.RuntimeRoot);
                    shouldStop = true;
                }
            }

            if (shouldStop)
            {
                StopSharedState(state);
            }
        }

        private static void StartSharedState(SharedRuntimeState state)
        {
            state.Started = true;
            string mainLogPath = BridgeEndpointResolver.ResolveAttachLogPath(state.RuntimeRoot);
            string mainLogFullPath = GetNormalizedPathOrEmpty(mainLogPath);
            state.MainPump = new BridgeLogPump();
            state.MainPump.Start(mainLogPath, line => PublishShared(state, $"[Bridge] {line}"), state.Settings);

            StartSharedSidePumpIfDifferent(
                state,
                Path.Combine(state.RuntimeRoot, "log", "bridge_server.log"),
                "[BridgeServer]",
                mainLogFullPath);
            StartSharedSidePumpIfDifferent(
                state,
                Path.Combine(state.RuntimeRoot, "log", "bridge_message.log"),
                "[BridgeMessage]",
                mainLogFullPath,
                BridgeMessageLogPumpWaitFileTimeoutMs,
                BridgeMessageLogPumpMissingFilePollMs,
                BridgeMessageLogPumpMissingFilePollMs);
            StartSharedSidePumpIfDifferent(
                state,
                Path.Combine(state.RuntimeRoot, "log", "run_server.log"),
                "[RunServer]",
                mainLogFullPath);
            StartSharedSidePumpIfDifferent(
                state,
                Path.Combine(state.RuntimeRoot, "log", "setup.log"),
                "[Setup]",
                mainLogFullPath);
        }

        private static void StopSharedState(SharedRuntimeState state)
        {
            try
            {
                state.MainPump?.Stop();
                state.MainPump?.Dispose();
            }
            catch
            {
                // ignore
            }

            if (state.SidePumps.Count > 0)
            {
                for (int i = 0; i < state.SidePumps.Count; i++)
                {
                    try
                    {
                        state.SidePumps[i]?.Stop();
                        state.SidePumps[i]?.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }
                state.SidePumps.Clear();
            }
        }

        private static void StartSharedSidePumpIfDifferent(
            SharedRuntimeState state,
            string logPath,
            string tag,
            string mainLogFullPath,
            int? waitFileTimeoutMsOverride = null,
            int? missingFilePollMinMsOverride = null,
            int? missingFilePollMaxMsOverride = null,
            bool? readFromStartOverride = null)
        {
            string sideLogFullPath = GetNormalizedPathOrEmpty(logPath);
            if (!string.IsNullOrWhiteSpace(mainLogFullPath) &&
                !string.IsNullOrWhiteSpace(sideLogFullPath) &&
                string.Equals(mainLogFullPath, sideLogFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var pump = new BridgeLogPump();
            state.SidePumps.Add(pump);
            pump.Start(
                logPath,
                line => PublishShared(state, $"{tag} {line}"),
                state.Settings,
                waitFileTimeoutMsOverride,
                missingFilePollMinMsOverride,
                missingFilePollMaxMsOverride,
                readFromStartOverride);
        }

        private static void PublishShared(SharedRuntimeState state, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            SharedSubscription[] subscribers;
            lock (SharedGate)
            {
                state.BufferedLines.Add(message);
                if (state.BufferedLines.Count > MaxBufferedLines)
                {
                    state.BufferedLines.RemoveAt(0);
                }
                subscribers = state.Subscriptions.ToArray();
            }

            Debug.Log(message);
            for (int i = 0; i < subscribers.Length; i++)
            {
                subscribers[i].Emit(message);
            }
        }

        private static string NormalizeRuntimeRoot(string runtimeRoot)
        {
            try
            {
                return Path.GetFullPath(runtimeRoot.Trim());
            }
            catch
            {
                return runtimeRoot.Trim();
            }
        }

        private static string GetNormalizedPathOrEmpty(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return string.Empty;
            }
        }

        private sealed class SharedRuntimeState
        {
            internal SharedRuntimeState(string runtimeRoot, BridgeRuntimeSettings settings)
            {
                RuntimeRoot = runtimeRoot;
                Settings = settings;
            }

            internal string RuntimeRoot { get; }
            internal BridgeRuntimeSettings Settings { get; }
            internal BridgeLogPump MainPump { get; set; }
            internal List<BridgeLogPump> SidePumps { get; } = new List<BridgeLogPump>(3);
            internal List<SharedSubscription> Subscriptions { get; } = new List<SharedSubscription>(2);
            internal List<string> BufferedLines { get; } = new List<string>(MaxBufferedLines);
            internal bool Started { get; set; }
        }

        private sealed class SharedSubscription : IDisposable
        {
            private readonly Action<string> onLine;
            private readonly SynchronizationContext sharedCallbackContext;
            private bool sharedDisposed;

            internal SharedSubscription(SharedRuntimeState state, Action<string> onLine)
            {
                State = state;
                this.onLine = onLine;
                sharedCallbackContext = SynchronizationContext.Current;
            }

            internal SharedRuntimeState State { get; }

            internal void Emit(string line)
            {
                if (sharedDisposed || string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                if (sharedCallbackContext != null)
                {
                    sharedCallbackContext.Post(_ =>
                    {
                        if (!sharedDisposed)
                        {
                            SafeInvokeShared(line);
                        }
                    }, null);
                    return;
                }

                SafeInvokeShared(line);
            }

            public void Dispose()
            {
                if (sharedDisposed)
                {
                    return;
                }

                sharedDisposed = true;
                UnsubscribeShared(this);
            }

            private void SafeInvokeShared(string line)
            {
                try
                {
                    onLine?.Invoke(line);
                }
                catch
                {
                    // ignore callback failures
                }
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            internal static readonly NoopDisposable Instance = new NoopDisposable();

            public void Dispose()
            {
            }
        }
    }
}
