using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public static class BridgeRuntimeControl
    {
        public static bool TryReadServerEndpoint(string runtimeRoot, out string host, out int port)
        {
            return BridgeEndpointResolver.TryReadServerEndpoint(runtimeRoot, "127.0.0.1", out host, out port, out _);
        }


        public static bool CanConnect(
            string host,
            int port,
            int connectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
            CancellationToken token = default)
        {
            return CanConnectAsync(host, port, connectTimeoutMs, token).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public static async Task<bool> CanConnectAsync(
            string host,
            int port,
            int connectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            {
                return false;
            }

            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(Math.Max(100, connectTimeoutMs));

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

        public static async Task<bool> TrySendQuitAsync(
            string host,
            int port,
            int connectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
            int ioTimeoutMs = BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
            CancellationToken token = default)
        {
            using var client = new BridgeProtocolClient(connectTimeoutMs, ioTimeoutMs);
            return await client.TrySendQuitAsync(host, port, token).ConfigureAwait(false);
        }
    }
}
