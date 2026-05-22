using System.Diagnostics;

namespace KimodoUnityMotionTools.Bridge
{
    internal interface IBridgePlatformProcess
    {
        bool SupportsCurrentPlatform();
        ProcessStartInfo BuildLauncherStartInfo(string launcherPath, string modelName, bool highVram, bool forceSetup, string modelsRoot);
        void KillProcessTreeByPid(int pid);
    }
}
