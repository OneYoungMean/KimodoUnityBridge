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
            timeoutCts.CancelAfter(Math.Max(BridgeRuntimeSettings.DefaultStartupTimeoutMs / 20, startupTimeoutMs));
            CancellationToken waitToken = timeoutCts.Token;

            while (true)
            {
                waitToken.ThrowIfCancellationRequested();
                if (BridgeEndpointResolver.TryReadServerEndpoint(runtimeRoot, hostFallback, out string host, out int port, out _))
                {
                    BridgePingResult ping = await BridgeRuntimeControl.QueryPingAsync(
                        host,
                        port,
                        BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                        BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                        waitToken).ConfigureAwait(false);
                    if (ping.IsHealthy(acceptLoading: true))
                    {
                        return;
                    }

                    if (ping.IsError)
                    {
                        throw new Exception($"Bridge startup failed: {SummarizeBridgeMessage(ping.Message)}");
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

        private static string SummarizeBridgeMessage(string message, int maxLength = 500)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            string normalized = string.Join(" ", message.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized.Substring(0, maxLength) + "...";
        }
    }
}
