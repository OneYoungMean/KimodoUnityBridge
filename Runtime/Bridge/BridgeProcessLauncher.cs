using System;
using System.Diagnostics;
using System.IO;

namespace KimodoBridge
{
    internal sealed class BridgeProcessLauncher
    {
        private readonly IBridgePlatformProcess platformProcess;

        internal BridgeProcessLauncher(IBridgePlatformProcess platformProcess)
        {
            this.platformProcess = platformProcess ?? throw new ArgumentNullException(nameof(platformProcess));
        }

        internal Process Start(
            string launcherPath,
            string modelName,
            bool highVram,
            bool forceSetup,
            bool forceCpu,
            string modelsRoot,
            int idleTimeoutSeconds,
            int ownerProcessId)
        {
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new InvalidOperationException("launcherPath is empty.");
            }

            string resolvedLauncher = Path.GetFullPath(launcherPath.Trim());
            if (!File.Exists(resolvedLauncher))
            {
                throw new FileNotFoundException($"Bridge launcher not found: {resolvedLauncher}");
            }

            ProcessStartInfo psi = platformProcess.BuildLauncherStartInfo(
                resolvedLauncher,
                modelName,
                highVram,
                forceSetup,
                forceCpu,
                modelsRoot,
                idleTimeoutSeconds,
                ownerProcessId);

            return StartWithLogging(psi);
        }

        internal static Process StartWithLogging(ProcessStartInfo psi)
        {
            if (psi == null)
            {
                throw new ArgumentNullException(nameof(psi));
            }

            UnityEngine.Debug.Log($"[Kimodo][BridgeProcess] launch cmd: {psi.FileName} {psi.Arguments} (cwd={psi.WorkingDirectory})");
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
            {
                throw new Exception("Failed to start bridge process.");
            }

            return proc;
        }
    }
}
