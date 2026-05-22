using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace KimodoUnityMotionTools.Bridge
{
    internal sealed class LinuxBridgePlatformProcess : IBridgePlatformProcess
    {
        public bool SupportsCurrentPlatform()
        {
            return Application.platform == RuntimePlatform.LinuxEditor || Application.platform == RuntimePlatform.LinuxPlayer;
        }

        public ProcessStartInfo BuildLauncherStartInfo(string launcherPath, string modelName, bool highVram, bool forceSetup, string modelsRoot)
        {
            string ext = Path.GetExtension(launcherPath)?.ToLowerInvariant() ?? string.Empty;
            if (ext != ".sh" && ext != ".bat")
            {
                throw new NotSupportedException($"Linux launcher must be .sh/.bat (bash), got: {ext}");
            }

            EnsureExecutableByBash(launcherPath);

            string modelArg = $" --model \"{(string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim())}\"";
            string vramArg = highVram ? " --highvram" : string.Empty;
            string forceSetupArg = forceSetup ? " --force-setup" : string.Empty;
            string modelsArg = string.IsNullOrWhiteSpace(modelsRoot) ? string.Empty : $" --models-root \"{modelsRoot.Trim()}\"";
            string args = modelArg + vramArg + forceSetupArg + modelsArg;

            return new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"\"{launcherPath}\"{args}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? Environment.CurrentDirectory
            };
        }

        public void KillProcessTreeByPid(int pid)
        {
            if (pid <= 0)
            {
                return;
            }

            try
            {
                using Process killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = $"-TERM -P {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                killer?.WaitForExit(2000);
            }
            catch
            {
                // ignore
            }

            try
            {
                using Process killMain = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-TERM {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                killMain?.WaitForExit(2000);
            }
            catch
            {
                // ignore
            }
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
