using System;
using System.Globalization;
using System.IO;
using System.Net;

namespace KimodoBridge
{
    internal static class BridgeEndpointResolver
    {
        internal static string GetServerPortFilePath(string runtimeRoot)
        {
            return Path.Combine(runtimeRoot, "serverport");
        }

        internal static string ResolveAttachLogPath(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return string.Empty;
            }

            string logDir = Path.Combine(runtimeRoot, "log");
            try
            {
                if (Directory.Exists(logDir))
                {
                    string bridgeServerLog = Path.Combine(logDir, "bridge_server.log");
                    if (File.Exists(bridgeServerLog))
                    {
                        return bridgeServerLog;
                    }

                    string runServerLog = Path.Combine(logDir, "run_server.log");
                    if (File.Exists(runServerLog))
                    {
                        return runServerLog;
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
            }
            catch
            {
                // fall through to default path
            }

            // Default bridge log path used by bridge_server.py when KIMODO_BRIDGE_LOG is not provided.
            return Path.Combine(logDir, "bridge_server.log");
        }

        internal static bool TryReadServerEndpoint(string runtimeRoot, string hostFallback, out string host, out int port, out string error)
        {
            return TryReadServerEndpointFromFile(GetServerPortFilePath(runtimeRoot), hostFallback, out host, out port, out error);
        }

        internal static bool TryReadServerProcessId(string runtimeRoot, out int processId)
        {
            processId = -1;
            try
            {
                string path = GetServerPortFilePath(runtimeRoot);
                if (!File.Exists(path))
                {
                    return false;
                }

                foreach (string line in File.ReadAllLines(path))
                {
                    int eqIndex = line.IndexOf('=');
                    if (eqIndex <= 0 || !line.Substring(0, eqIndex).Trim().Equals("pid", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return int.TryParse(line.Substring(eqIndex + 1).Trim(), out processId) && processId > 0;
                }
            }
            catch
            {
                // The endpoint file may disappear while the server is shutting down.
            }
            return false;
        }

        internal static bool TryReadServerEndpointFromFile(string serverPortPath, string hostFallback, out string host, out int port, out string error)
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

                string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string firstLine = lines.Length > 0 ? lines[0].Trim() : text;
                foreach (string line in lines)
                {
                    int eqIndex = line.IndexOf('=');
                    if (eqIndex <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eqIndex).Trim();
                    string value = line.Substring(eqIndex + 1).Trim();
                    if (key.Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            host = value;
                        }
                    }
                    else if (key.Equals("port", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParsePort(value, out int parsedPort))
                        {
                            port = parsedPort;
                        }
                    }
                }

                if (port > 0 && TryParseHost(host, out host))
                {
                    return true;
                }

                int split = firstLine.LastIndexOf(':');
                if (split > 0 && split < firstLine.Length - 1)
                {
                    string rawHost = firstLine.Substring(0, split).Trim();
                    string rawPort = firstLine.Substring(split + 1).Trim();
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

                if (!TryParsePort(firstLine, out port))
                {
                    error = $"invalid serverport content: '{firstLine}'";
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
