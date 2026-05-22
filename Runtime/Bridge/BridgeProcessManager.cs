using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoUnityMotionTools.Bridge
{
    internal sealed class BridgeProcessManager : IDisposable
    {
        private readonly IBridgePlatformProcess platformProcess;
        private Process process;
        private int processId = -1;
        private bool disposed;

        internal BridgeProcessManager(IBridgePlatformProcess platformProcess)
        {
            this.platformProcess = platformProcess ?? throw new ArgumentNullException(nameof(platformProcess));
            if (!this.platformProcess.SupportsCurrentPlatform())
            {
                throw new PlatformNotSupportedException("Current platform is not supported by the selected bridge process implementation.");
            }
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

        public Process Start(string launcherPath, string modelName, bool highVram, bool forceSetup, string modelsRoot)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new InvalidOperationException("launcherPath is empty.");
            }

            string resolvedLauncher = Path.GetFullPath(launcherPath.Trim());
            if (!File.Exists(resolvedLauncher))
            {
                throw new FileNotFoundException($"Bridge launcher not found: {resolvedLauncher}");
            }

            ProcessStartInfo psi = platformProcess.BuildLauncherStartInfo(
                resolvedLauncher,
                modelName,
                highVram,
                forceSetup,
                modelsRoot);
            UnityEngine.Debug.Log($"[Kimodo][BridgeProcess] launch cmd: {psi.FileName} {psi.Arguments} (cwd={psi.WorkingDirectory})");
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
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
            BridgeProtocolClient protocolClient,
            int startupTimeoutMs,
            int pollIntervalMs,
            CancellationToken token)
        {
            if (protocolClient == null)
            {
                throw new ArgumentNullException(nameof(protocolClient));
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(Math.Max(30000, startupTimeoutMs));
            CancellationToken waitToken = timeoutCts.Token;

            while (true)
            {
                waitToken.ThrowIfCancellationRequested();
                if (BridgeEndpointResolver.TryReadServerEndpoint(runtimeRoot, hostFallback, out string host, out int port, out _))
                {
                    bool ok = await protocolClient.PingAsync(host, port, waitToken, acceptLoading: true);
                    if (ok)
                    {
                        return;
                    }
                }

                if (process != null && process.HasExited)
                {
                    throw new Exception($"Bridge exited with code {process.ExitCode}.");
                }

                await Task.Delay(Math.Max(100, pollIntervalMs), waitToken);
            }
        }

        public void KillProcessTree()
        {
            Process proc = process;
            int pid = processId;
            process = null;
            processId = -1;

            if (pid <= 0 && proc != null)
            {
                try
                {
                    pid = proc.Id;
                }
                catch
                {
                    pid = -1;
                }
            }

            if (pid > 0)
            {
                platformProcess.KillProcessTreeByPid(pid);
            }

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
            KillProcessTree();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BridgeProcessManager));
            }
        }
    }
}
