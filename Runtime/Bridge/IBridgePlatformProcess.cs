using System.Diagnostics;

namespace KimodoBridge
{
    internal interface IBridgePlatformProcess
    {
        bool SupportsCurrentPlatform();
        ProcessStartInfo BuildLauncherStartInfo(
            string launcherPath,
            int ownerProcessId);
    }
}
