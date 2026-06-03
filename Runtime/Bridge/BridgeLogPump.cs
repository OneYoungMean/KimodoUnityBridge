using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoUnityMotionTools.Bridge
{
    public sealed class BridgeLogPump : IDisposable
    {
        private const int StopWaitTimeoutMs = 1500;

        private readonly object gate = new object();
        private CancellationTokenSource cts;
        private Task pumpTask;
        private SynchronizationContext callbackContext;
        private bool disposed;

        public void Start(string logPath, Action<string> onLine, BridgeRuntimeSettings settings = null)
        {
            Start(logPath, onLine, settings, null, null, null);
        }

        public void Start(
            string logPath,
            Action<string> onLine,
            BridgeRuntimeSettings settings,
            int? waitFileTimeoutMsOverride,
            int? missingFilePollMinMsOverride,
            int? missingFilePollMaxMsOverride)
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
                Math.Max(Math.Max(10, idlePollMinMs), idlePollMaxMs)));

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

            await ObserveStopAsync(currentPumpTask, currentCts, timeoutMs, token);
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

        private static async Task ObserveStopAsync(Task currentPumpTask, CancellationTokenSource currentCts, int timeoutMs, CancellationToken token)
        {
            try
            {
                if (currentPumpTask != null)
                {
                    Task completed = await Task.WhenAny(currentPumpTask, Task.Delay(Math.Max(10, timeoutMs), token));
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
            int idlePollMaxMs)
        {
            try
            {
                DateTime waitUntil = DateTime.UtcNow.AddMilliseconds(waitFileTimeoutMs);
                int missingFileDelayMs = missingFilePollMinMs;
                while (!token.IsCancellationRequested && !File.Exists(logPath))
                {
                    if (DateTime.UtcNow >= waitUntil)
                    {
                        return;
                    }

                    await Task.Delay(missingFileDelayMs, token);
                    missingFileDelayMs = Math.Min(missingFilePollMaxMs, missingFileDelayMs + missingFilePollMinMs);
                }

                if (!File.Exists(logPath))
                {
                    return;
                }

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.CanSeek)
                {
                    fs.Seek(0, SeekOrigin.End);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                int idleDelayMs = idlePollMinMs;
                while (!token.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync();
                    if (line != null)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            EmitLine(onLine, callbackContext, trimmed);
                        }

                        idleDelayMs = idlePollMinMs;
                        continue;
                    }

                    if (fs.CanSeek && fs.Length < fs.Position)
                    {
                        fs.Seek(0, SeekOrigin.Begin);
                        reader.DiscardBufferedData();
                        idleDelayMs = idlePollMinMs;
                        continue;
                    }

                    if (fs.Length > fs.Position)
                    {
                        string tailChunk = await reader.ReadToEndAsync();
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

                        idleDelayMs = idlePollMinMs;
                        continue;
                    }

                    await Task.Delay(idleDelayMs, token);
                    idleDelayMs = Math.Min(idlePollMaxMs, idleDelayMs + idlePollMinMs);
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
    }
}
