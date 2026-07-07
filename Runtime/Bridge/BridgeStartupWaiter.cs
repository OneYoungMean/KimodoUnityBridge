using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    internal sealed class BridgeStartupWaiter
    {
        internal async Task WaitUntilReadyAsync(
            Func<bool> hasProcessExited,
            Func<int> getExitCode,
            string runtimeRoot,
            string hostFallback,
            int startupTimeoutMs,
            int pollIntervalMs,
            CancellationToken token)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(Math.Max(BridgeRuntimeSettings.DefaultStartupTimeoutMs / 20, startupTimeoutMs));
            CancellationToken waitToken = timeoutCts.Token;

            while (true)
            {
                waitToken.ThrowIfCancellationRequested();
                if (BridgeEndpointResolver.TryReadServerEndpoint(runtimeRoot, hostFallback, out string host, out int port, out _))
                {
                    bool canConnect = await BridgeRuntimeControl.CanOpenConnectionAsync(
                        host,
                        port,
                        BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                        waitToken).ConfigureAwait(false);
                    if (canConnect)
                    {
                        return;
                    }
                }

                if (hasProcessExited != null && hasProcessExited())
                {
                    int exitCode = getExitCode != null ? getExitCode() : -1;
                    throw new Exception($"Bridge exited with code {exitCode}.");
                }

                await Task.Delay(Math.Max(BridgeRuntimeSettings.DefaultPollIntervalMs / 2, pollIntervalMs), waitToken).ConfigureAwait(false);
            }
        }
    }
}
