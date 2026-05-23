using System;
using System.IO;

namespace KimodoUnityMotionTools.Bridge
{
    public static class BridgeLauncherResolver
    {
        public static string ResolveStartScript(string runtimeRoot)
        {
            EnsureNoLegacyScripts(runtimeRoot);

            string s1 = Path.Combine(runtimeRoot, "run_server.bat");
            if (File.Exists(s1))
            {
                return s1;
            }

            string s2 = Path.Combine(runtimeRoot, "bash", "start_server.bat");
            if (File.Exists(s2))
            {
                return s2;
            }

            string s2Root = Path.Combine(runtimeRoot, "start_server.bat");
            if (File.Exists(s2Root))
            {
                return s2Root;
            }

            string s2Sh = Path.Combine(runtimeRoot, "bash", "start_server.sh");
            if (File.Exists(s2Sh))
            {
                return s2Sh;
            }

            string s2RootSh = Path.Combine(runtimeRoot, "start_server.sh");
            if (File.Exists(s2RootSh))
            {
                return s2RootSh;
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
