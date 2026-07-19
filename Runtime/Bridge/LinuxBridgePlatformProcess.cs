using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class LinuxBridgePlatformProcess : IBridgePlatformProcess
    {
        public bool SupportsCurrentPlatform()
        {
            return Application.platform == RuntimePlatform.LinuxEditor || Application.platform == RuntimePlatform.LinuxPlayer;
        }

        public ProcessStartInfo BuildLauncherStartInfo(
            string launcherPath,
            int ownerProcessId)
        {
            string ext = Path.GetExtension(launcherPath)?.ToLowerInvariant() ?? string.Empty;
            if (ext != ".sh" && ext != ".bat")
            {
                throw new NotSupportedException($"Linux launcher must be .sh/.bat (bash), got: {ext}");
            }

            EnsureExecutableByBash(launcherPath);

            string watchPidArg = ownerProcessId > 0 ? $" --watchpid {ownerProcessId}" : string.Empty;
            string outputArg = " --output file";
            string args = watchPidArg + outputArg;
            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"bash \\\"{launcherPath}\\\"{args}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? Environment.CurrentDirectory
            };
            startInfo.EnvironmentVariables["KIMODO_IDLE_TIMEOUT_SEC"] = "0";
            return startInfo;
        }

        private static void EnsureExecutableByBash(string launcherPath)
        {
            // bash can run non-executable scripts, but when policy enforces executable files
            // we expose a clear error early if filesystem permissions reject read access.
            try
            {
                using FileStream fs = File.Open(launcherPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 0)
                {
                    throw new IOException("invalid file stream length.");
                }
            }
            catch (Exception e)
            {
                throw new IOException($"Launcher cannot be read on Linux: {launcherPath}. {e.Message}", e);
            }
        }
    }
}
