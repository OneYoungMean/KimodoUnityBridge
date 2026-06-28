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
                throw new NotSupportedException($"macOS launcher must be .sh/.bat (bash), got: {ext}");
            }

            EnsureReadableByBash(launcherPath);

            string modelArg = $" --model \"{(string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim())}\"";
            string vramArg = highVram ? " --highvram" : string.Empty;
            string forceSetupArg = forceSetup ? " --force-setup" : string.Empty;
            string forceCpuArg = forceCpu ? " --device cpu" : string.Empty;
            string modelsArg = string.IsNullOrWhiteSpace(modelsRoot) ? string.Empty : $" --models-root \"{modelsRoot.Trim()}\"";
            string watchPidArg = ownerProcessId > 0 ? $" --watchpid {ownerProcessId}" : string.Empty;
            string outputArg = " --output file";
            string args = modelArg + vramArg + forceSetupArg + forceCpuArg + modelsArg + watchPidArg + outputArg;
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
