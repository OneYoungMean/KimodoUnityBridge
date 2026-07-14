namespace KimodoBridge
{
    internal static class BridgeRuntimeDefaults
    {
        internal const int StartupTimeoutMs = 600000;
        internal const int PollIntervalMs = 1000;
        internal const int ConnectTimeoutMs = 3000;
        internal const int IoTimeoutMs = 600000;
        internal const int ModelLoadingTimeoutMs = 3600000;
        internal const int ModelLoadingPollIntervalMs = 1000;
        internal const int StatusConnectTimeoutMs = 1500;
        internal const int LogPumpWaitFileTimeoutMs = 20000;
        internal const int LogPumpMissingFilePollMinMs = 120;
        internal const int LogPumpMissingFilePollMaxMs = 900;
        internal const int LogPumpIdlePollMinMs = 20;
        internal const int LogPumpIdlePollMaxMs = 260;
    }
}
