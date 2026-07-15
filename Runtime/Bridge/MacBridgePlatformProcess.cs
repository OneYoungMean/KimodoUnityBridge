using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class MacBridgePlatformProcess : IBridgePlatformProcess
    {
        public bool SupportsCurrentPlatform()
        {
            return Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer;
        }

        public ProcessStartInfo BuildLauncherStartInfo(
            string launcherPath,
            bool forceSetup,
            int ownerProcessId)
        {
            string ext = Path.GetExtension(launcherPath)?.ToLowerInvariant() ?? string.Empty;
            if (ext != ".sh" && ext != ".bat")
            {
                throw new NotSupportedException($"macOS launcher must be .sh/.bat (bash), got: {ext}");
            }

            EnsureReadableByBash(launcherPath);

            string forceSetupArg = forceSetup ? " --force-setup" : string.Empty;
            string watchPidArg = ownerProcessId > 0 ? $" --watchpid {ownerProcessId}" : string.Empty;
            string outputArg = " --output file";
            string args = forceSetupArg + watchPidArg + outputArg;
            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"bash \\\"{launcherPath}\\\"{args}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? Environment.CurrentDirectory
            };
            startInfo.EnvironmentVariables["KIMODO_IDLE_TIMEOUT_SEC"] = "0";
            startInfo.EnvironmentVariables["KIMODO_ARDY_FILE_ROOTS"] = ArdyUnityMotionCache.ManagedRoot;
            return startInfo;
        }

        private static void EnsureReadableByBash(string launcherPath)
        {
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
                throw new IOException($"Launcher cannot be read on macOS: {launcherPath}. {e.Message}", e);
            }
        }
    }
}
