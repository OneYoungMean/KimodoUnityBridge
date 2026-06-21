using System;
using System.IO;
using UnityEngine;

namespace KimodoBridge
{
    public static class BridgeLauncherResolver
    {
        public static string ResolveStartScript(string runtimeRoot)
        {
            EnsureNoLegacyScripts(runtimeRoot);

            bool preferShellLauncher =
                Application.platform == RuntimePlatform.LinuxEditor ||
                Application.platform == RuntimePlatform.LinuxPlayer;

            string primary = Path.Combine(runtimeRoot, preferShellLauncher ? "run_server.sh" : "run_server.bat");
            if (File.Exists(primary))
            {
                return primary;
            }

            string fallback = Path.Combine(runtimeRoot, preferShellLauncher ? "run_server.bat" : "run_server.sh");
            if (File.Exists(fallback))
            {
                return fallback;
            }

            return string.Empty;
        }

        public static void EnsureNoLegacyScripts(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return;
            }

            string[] legacyScripts =
            {
                Path.Combine(runtimeRoot, "start_kimodo_bridge_offline.bat"),
                Path.Combine(runtimeRoot, "start_kimodo_bridge_offline.sh"),
                Path.Combine(runtimeRoot, "setup_kimodo_offline.bat"),
                Path.Combine(runtimeRoot, "setup_kimodo_offline.sh")
            };

            for (int i = 0; i < legacyScripts.Length; i++)
            {
                string legacyScript = legacyScripts[i];
                if (File.Exists(legacyScript))
                {
                    throw new InvalidOperationException(
                        $"Legacy script detected and not supported: {legacyScript}");
                }
            }
        }
    }
}
