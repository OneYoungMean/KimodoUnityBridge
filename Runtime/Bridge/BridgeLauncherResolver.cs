using System.IO;
using UnityEngine;

namespace KimodoBridge
{
    internal static class BridgeLauncherResolver
    {
        internal static string ResolveStartScript(string runtimeRoot)
        {
            bool preferShellLauncher =
                Application.platform == RuntimePlatform.OSXEditor ||
                Application.platform == RuntimePlatform.OSXPlayer ||
                Application.platform == RuntimePlatform.LinuxEditor ||
                Application.platform == RuntimePlatform.LinuxPlayer;

            string primary = Path.Combine(runtimeRoot, preferShellLauncher ? "run_server.sh" : "run_server.bat");
            if (File.Exists(primary))
            {
                return primary;
            }

            return string.Empty;
        }
    }
}
