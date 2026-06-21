using System.Diagnostics;

namespace KimodoBridge
{
    internal interface IBridgePlatformProcess
    {
        bool SupportsCurrentPlatform();
        ProcessStartInfo BuildLauncherStartInfo(
            string launcherPath,
            string modelName,
            bool highVram,
            bool forceSetup,
            bool forceCpu,
            string modelsRoot,
            int idleTimeoutSeconds,
            int ownerProcessId);
    }
}
