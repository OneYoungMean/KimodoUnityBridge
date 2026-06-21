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
            string modelName,
            bool highVram,
            bool forceSetup,
            bool forceCpu,
            string modelsRoot,
            int idleTimeoutSeconds,
            int ownerProcessId)
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
            string forceCpuArg = forceCpu ? " --device cpu" : string.Empty;
            string modelsArg = string.IsNullOrWhiteSpace(modelsRoot) ? string.Empty : $" --models-root \"{modelsRoot.Trim()}\"";
            string outputArg = " --output file";
            string args = modelArg + vramArg + forceSetupArg + forceCpuArg + modelsArg + outputArg;
            string envPrefix = $"KIMODO_IDLE_TIMEOUT_SEC={Math.Max(0, idleTimeoutSeconds)}";

            return new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"{envPrefix} bash \\\"{launcherPath}\\\"{args}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? Environment.CurrentDirectory
            };
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
