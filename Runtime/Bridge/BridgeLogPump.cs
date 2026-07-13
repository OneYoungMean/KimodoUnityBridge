using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class BridgeLogPump : IDisposable
    {
        private const int StopWaitTimeoutMs = 1500;

        private readonly object gate = new object();
        private CancellationTokenSource cts;
        private Task pumpTask;
        private bool disposed;

        public void Start(
            string logPath,
            Action<string> onLine,
            int? waitFileTimeoutMsOverride = null,
            int? missingFilePollMinMsOverride = null,
            int? missingFilePollMaxMsOverride = null,
            bool readFromStart = false)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(logPath) || onLine == null)
            {
                return;
            }

            int waitFileTimeoutMs = Math.Max(1000, waitFileTimeoutMsOverride ?? BridgeRuntimeDefaults.LogPumpWaitFileTimeoutMs);
            int missingFilePollMinMs = Math.Max(30, missingFilePollMinMsOverride ?? BridgeRuntimeDefaults.LogPumpMissingFilePollMinMs);
            int missingFilePollMaxMs = Math.Max(missingFilePollMinMs, missingFilePollMaxMsOverride ?? BridgeRuntimeDefaults.LogPumpMissingFilePollMaxMs);
            int idlePollMinMs = BridgeRuntimeDefaults.LogPumpIdlePollMinMs;
            int idlePollMaxMs = BridgeRuntimeDefaults.LogPumpIdlePollMaxMs;

            var newCts = new CancellationTokenSource();
            Task newTask = Task.Run(() => PumpAsync(
                logPath,
                onLine,
                newCts.Token,
                waitFileTimeoutMs,
                missingFilePollMinMs,
                missingFilePollMaxMs,
                idlePollMinMs,
                idlePollMaxMs,
                readFromStart));

            lock (gate)
            {
                cts = newCts;
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
            int waitFileTimeoutMs,
            int missingFilePollMinMs,
            int missingFilePollMaxMs,
            int idlePollMinMs,
            int idlePollMaxMs,
            bool readFromStart)
        {
            try
            {
                DateTime waitStartUtc = DateTime.UtcNow;
                int missingDelayMs = missingFilePollMinMs;
                while (!token.IsCancellationRequested && !File.Exists(logPath))
                {
                    if ((DateTime.UtcNow - waitStartUtc).TotalMilliseconds > waitFileTimeoutMs)
                    {
                        return;
                    }

                    await Task.Delay(missingDelayMs, token).ConfigureAwait(false);
                    missingDelayMs = Math.Min(missingFilePollMaxMs, missingDelayMs + missingFilePollMinMs);
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
                                SafeEmitLine(onLine, trimmed);
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
                                        SafeEmitLine(onLine, trimmed);
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
                SafeEmitLine(onLine, $"[BridgeLogPump] stopped: {e.Message}");
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

        private static void SafeEmitLine(Action<string> onLine, string line)
        {
            if (onLine == null || string.IsNullOrWhiteSpace(line))
            {
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
