using Newtonsoft.Json.Linq;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public enum BridgePingStatus
    {
        Unknown = 0,
        Ready = 1,
        Loading = 2,
        Error = 3,
        Unreachable = 4,
        InvalidEndpoint = 5
    }

    public readonly struct BridgePingResult
    {
        public readonly BridgePingStatus Status;
        public readonly string Host;
        public readonly int Port;
        public readonly string Message;

        public BridgePingResult(BridgePingStatus status, string host, int port, string message)
        {
            Status = status;
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            Port = port;
            Message = message ?? string.Empty;
        }

        public bool IsReady => Status == BridgePingStatus.Ready;
        public bool IsLoading => Status == BridgePingStatus.Loading;
        public bool IsError => Status == BridgePingStatus.Error;
        public bool IsReachable => Status == BridgePingStatus.Ready || Status == BridgePingStatus.Loading || Status == BridgePingStatus.Error;
        public string Endpoint => Port > 0 ? $"{Host}:{Port}" : $"{Host}:(invalid)";

        public bool IsHealthy(bool acceptLoading)
        {
            return Status == BridgePingStatus.Ready || (acceptLoading && Status == BridgePingStatus.Loading);
        }
    }

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

        public static BridgePingResult QueryPing(
            string host,
            int port,
            int connectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
            int ioTimeoutMs = BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
            CancellationToken token = default)
        {
            return QueryPingAsync(host, port, connectTimeoutMs, ioTimeoutMs, token).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public static async Task<BridgePingResult> QueryPingAsync(
            string host,
            int port,
            int connectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
            int ioTimeoutMs = BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            {
                return new BridgePingResult(BridgePingStatus.InvalidEndpoint, host, port, "Bridge endpoint is invalid.");
            }

            try
            {
                using var client = new BridgeProtocolClient(connectTimeoutMs, ioTimeoutMs);
                JObject response = await client.SendAsync(host, port, new JObject { ["cmd"] = "ping" }, token).ConfigureAwait(false);
                string status = response?.Value<string>("status") ?? string.Empty;
                string message = response?.Value<string>("message") ?? string.Empty;

                if (string.Equals(status, "pong", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return new BridgePingResult(BridgePingStatus.Ready, host, port, message);
                }

                if (string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "initializing", StringComparison.OrdinalIgnoreCase))
                {
                    return new BridgePingResult(BridgePingStatus.Loading, host, port, message);
                }

                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    return new BridgePingResult(BridgePingStatus.Error, host, port, message);
                }

                string unexpected = string.IsNullOrWhiteSpace(status)
                    ? "Bridge ping response did not include a status."
                    : $"Unexpected bridge ping status: {status}.";
                if (!string.IsNullOrWhiteSpace(message))
                {
                    unexpected += " " + message;
                }
                return new BridgePingResult(BridgePingStatus.Unreachable, host, port, unexpected);
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return new BridgePingResult(BridgePingStatus.Unreachable, host, port, "Bridge ping was canceled.");
            }
            catch (Exception ex)
            {
                return new BridgePingResult(BridgePingStatus.Unreachable, host, port, ex.Message);
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
