using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    internal sealed class BridgeProcessManager : IDisposable
    {
        private readonly BridgeProcessLauncher launcher;
        private readonly BridgeStartupWaiter startupWaiter;
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

            launcher = new BridgeProcessLauncher(platformProcess);
            startupWaiter = new BridgeStartupWaiter();
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
            string modelName,
            bool highVram,
            bool forceSetup,
            bool forceCpu,
            string modelsRoot,
            int idleTimeoutSeconds,
            int ownerProcessId)
        {
            ThrowIfDisposed();
            if (IsRunning)
            {
                throw new InvalidOperationException("Bridge process is already running.");
            }

            Process proc = launcher.Start(
                launcherPath,
                modelName,
                highVram,
                forceSetup,
                forceCpu,
                modelsRoot,
                idleTimeoutSeconds,
                ownerProcessId);
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

            await startupWaiter.WaitUntilReadyAsync(
                hasProcessExited: () => process != null && process.HasExited,
                getExitCode: () => process != null ? process.ExitCode : -1,
                runtimeRoot: runtimeRoot,
                hostFallback: hostFallback,
                protocolClient: protocolClient,
                startupTimeoutMs: startupTimeoutMs,
                pollIntervalMs: pollIntervalMs,
                token: token).ConfigureAwait(false);
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
    }
}
