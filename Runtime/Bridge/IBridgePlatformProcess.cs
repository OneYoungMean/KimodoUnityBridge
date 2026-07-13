using System.Diagnostics;

namespace KimodoBridge
{
    internal interface IBridgePlatformProcess
    {
        bool SupportsCurrentPlatform();
        ProcessStartInfo BuildLauncherStartInfo(
            string launcherPath,
            bool forceSetup,
            int ownerProcessId);
    }
}
