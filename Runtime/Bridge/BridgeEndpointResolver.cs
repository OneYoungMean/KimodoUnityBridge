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

                error = $"invalid serverport content: '{text}'";
                return false;
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
