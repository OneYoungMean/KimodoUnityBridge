using System;
using System.Globalization;
using System.IO;
using System.Net;

namespace KimodoUnityMotionTools.Bridge
{
    public static class BridgeEndpointResolver
    {
        public static string GetServerPortFilePath(string runtimeRoot)
        {
            return Path.Combine(runtimeRoot, "serverport");
        }

        public static string ResolveAttachLogPath(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return string.Empty;
            }

            string logDir = Path.Combine(runtimeRoot, "log");
            if (!Directory.Exists(logDir))
            {
                return string.Empty;
            }

            try
            {
                string runServerLog = Path.Combine(logDir, "run_server.log");
                if (File.Exists(runServerLog))
                {
                    return runServerLog;
                }

                string bridgeServerLog = Path.Combine(logDir, "bridge_server.log");
                if (File.Exists(bridgeServerLog))
                {
                    return bridgeServerLog;
                }

                string bridgeRuntimeLog = Path.Combine(logDir, "test_input_log.log");
                if (File.Exists(bridgeRuntimeLog))
                {
                    return bridgeRuntimeLog;
                }

                string[] bridgeLogs = Directory.GetFiles(logDir, "unity_bridge_*.log");
                if (bridgeLogs.Length > 0)
                {
                    Array.Sort(bridgeLogs, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                    return bridgeLogs[0];
                }
            }
            catch
            {
                return string.Empty;
            }

            // Default bridge log path used by bridge_server.py when KIMODO_BRIDGE_LOG is not provided.
            return Path.Combine(logDir, "bridge_server.log");
        }

        public static bool TryReadServerEndpoint(string runtimeRoot, string hostFallback, out string host, out int port, out string error)
        {
            return TryReadServerEndpointFromFile(GetServerPortFilePath(runtimeRoot), hostFallback, out host, out port, out error);
        }

        public static bool TryReadServerEndpointFromFile(string serverPortPath, string hostFallback, out string host, out int port, out string error)
        {
            host = string.IsNullOrWhiteSpace(hostFallback) ? "127.0.0.1" : hostFallback.Trim();
            port = -1;
            error = string.Empty;

            try
            {
                if (!File.Exists(serverPortPath))
                {
                    error = $"serverport file not found: {serverPortPath}";
                    return false;
                }

                string text = File.ReadAllText(serverPortPath).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = $"serverport is empty: {serverPortPath}";
                    return false;
                }

                int split = text.LastIndexOf(':');
                if (split > 0 && split < text.Length - 1)
                {
                    string rawHost = text.Substring(0, split).Trim();
                    string rawPort = text.Substring(split + 1).Trim();
                    if (!TryParsePort(rawPort, out port))
                    {
                        error = $"invalid port in serverport: '{rawPort}'";
                        return false;
                    }

                    if (!TryParseHost(rawHost, out host))
                    {
                        error = $"invalid host in serverport: '{rawHost}'";
                        return false;
                    }

                    return true;
                }

                if (!TryParsePort(text, out port))
                {
                    error = $"invalid serverport content: '{text}'";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                error = $"read serverport failed: {e.Message}";
                return false;
            }
        }

        private static bool TryParsePort(string raw, out int port)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0 && port <= 65535;
        }

        private static bool TryParseHost(string rawHost, out string host)
        {
            host = rawHost;
            if (string.IsNullOrWhiteSpace(rawHost))
            {
                return false;
            }

            if (IPAddress.TryParse(rawHost, out _))
            {
                return true;
            }

            try
            {
                _ = new DnsEndPoint(rawHost, 1);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
