using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    internal sealed class BridgeProcessManager : IDisposable
    {
        private readonly IBridgePlatformProcess platformProcess;
        private Process process;
        private int processId = -1;
        private bool disposed;

        internal BridgeProcessManager(IBridgePlatformProcess platformProcess)
        {
            if (platformProcess == null)
            {
                throw new ArgumentNullException(nameof(platformProcess));
            }

            if (!platformProcess.SupportsCurrentPlatform())
            {
                throw new PlatformNotSupportedException("Current platform is not supported by the selected bridge process implementation.");
            }

            this.platformProcess = platformProcess;
        }

        public bool IsRunning
        {
            get
            {
                try
                {
                    return process != null && !process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public int ProcessId => processId;

        public Process Start(
            string launcherPath,
            int ownerProcessId)
        {
            ThrowIfDisposed();
            if (IsRunning)
            {
                throw new InvalidOperationException("Bridge process is already running.");
            }

            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new InvalidOperationException("launcherPath is empty.");
            }

            string resolvedLauncher = Path.GetFullPath(launcherPath.Trim());
            if (!File.Exists(resolvedLauncher))
            {
                throw new FileNotFoundException($"Bridge launcher not found: {resolvedLauncher}");
            }

            ProcessStartInfo startInfo = platformProcess.BuildLauncherStartInfo(
                resolvedLauncher,
                ownerProcessId);
            UnityEngine.Debug.Log($"[Kimodo][BridgeProcess] launch cmd: {startInfo.FileName} {startInfo.Arguments} (cwd={startInfo.WorkingDirectory})");
            var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!proc.Start())
            {
                throw new Exception("Failed to start bridge process.");
            }

            process = proc;
            processId = proc.Id;
            return proc;
        }

        public async Task WaitUntilReadyAsync(
            string runtimeRoot,
            string hostFallback,
            int startupTimeoutMs,
            int pollIntervalMs,
            CancellationToken token)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(Math.Max(BridgeRuntimeDefaults.StartupTimeoutMs / 20, startupTimeoutMs));
            CancellationToken waitToken = timeoutCts.Token;

            while (true)
            {
                waitToken.ThrowIfCancellationRequested();
                if (BridgeEndpointResolver.TryReadServerEndpoint(runtimeRoot, hostFallback, out string host, out int port, out _) &&
                    await CanOpenConnectionAsync(host, port, BridgeRuntimeDefaults.StatusConnectTimeoutMs, waitToken).ConfigureAwait(false))
                {
                    return;
                }

                if (process != null && process.HasExited)
                {
                    throw new Exception($"Bridge exited with code {process.ExitCode}.");
                }

                await Task.Delay(Math.Max(BridgeRuntimeDefaults.PollIntervalMs / 2, pollIntervalMs), waitToken).ConfigureAwait(false);
            }
        }

        public void DetachProcess()
        {
            Process proc = process;
            process = null;
            processId = -1;

            if (proc != null)
            {
                try { proc.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            DetachProcess();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BridgeProcessManager));
            }
        }

        private static async Task<bool> CanOpenConnectionAsync(
            string host,
            int port,
            int connectTimeoutMs,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            {
                return false;
            }

            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(connectTimeoutMs);

            try
            {
                Task connectTask = client.ConnectAsync(host, port);
                Task timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    token.ThrowIfCancellationRequested();
                    return false;
                }

                await connectTask.ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
