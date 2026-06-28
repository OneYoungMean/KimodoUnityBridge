using System;
using UnityEngine;

namespace KimodoBridge
{
    [Serializable]
    public sealed class BridgeRuntimeSettings
    {
        public const int DefaultStartupTimeoutMs = 600000;
        public const int DefaultPollIntervalMs = 1000;
        public const int DefaultConnectTimeoutMs = 3000;
        public const int DefaultIoTimeoutMs = 600000;
        public const int DefaultModelLoadingTimeoutMs = 3600000;
        public const int DefaultModelLoadingPollIntervalMs = 1000;
        public const int DefaultStatusConnectTimeoutMs = 1500;
        public const int DefaultStatusIoTimeoutMs = 1200;
        public const int DefaultLogPumpWaitFileTimeoutMs = 20000;
        public const int DefaultLogPumpMissingFilePollMinMs = 120;
        public const int DefaultLogPumpMissingFilePollMaxMs = 900;
        public const int DefaultLogPumpIdlePollMinMs = 20;
        public const int DefaultLogPumpIdlePollMaxMs = 260;

        public string runtimeRoot;
        public string launcherPath;
        public string modelName = "Kimodo-SOMA-RP-v1";
        public bool highVram;
        public bool forceSetup;
        public bool forceCpu;
        public string modelsRoot;
        public string hostFallback = "127.0.0.1";
        public int startupTimeoutMs = DefaultStartupTimeoutMs;
        public int pollIntervalMs = DefaultPollIntervalMs;
        public int connectTimeoutMs = DefaultConnectTimeoutMs;
        public int ioTimeoutMs = DefaultIoTimeoutMs;
        public int modelLoadingTimeoutMs = DefaultModelLoadingTimeoutMs;
        public int modelLoadingPollIntervalMs = DefaultModelLoadingPollIntervalMs;
        public int statusConnectTimeoutMs = DefaultStatusConnectTimeoutMs;
        public int statusIoTimeoutMs = DefaultStatusIoTimeoutMs;
        public int idleTimeoutSeconds = 0;
        public bool preserveProcessOnCancellation;
        public int logPumpWaitFileTimeoutMs = DefaultLogPumpWaitFileTimeoutMs;
        public int logPumpMissingFilePollMinMs = DefaultLogPumpMissingFilePollMinMs;
        public int logPumpMissingFilePollMaxMs = DefaultLogPumpMissingFilePollMaxMs;
        public int logPumpIdlePollMinMs = DefaultLogPumpIdlePollMinMs;
        public int logPumpIdlePollMaxMs = DefaultLogPumpIdlePollMaxMs;
        public bool enableWindows = true;
        public bool enableMac = true;
        public bool enableLinux = true;
        public int ownerProcessId;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                throw new InvalidOperationException("runtimeRoot is empty.");
            }

            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new InvalidOperationException("launcherPath is empty.");
            }

            if (!enableWindows && !enableMac && !enableLinux)
            {
                throw new InvalidOperationException("All runtime platforms are disabled.");
            }

            startupTimeoutMs = Math.Max(30000, startupTimeoutMs);
            pollIntervalMs = Math.Max(100, pollIntervalMs);
            connectTimeoutMs = Math.Max(500, connectTimeoutMs);
            ioTimeoutMs = Math.Max(1000, ioTimeoutMs);
            modelLoadingTimeoutMs = Math.Max(10000, modelLoadingTimeoutMs);
            modelLoadingPollIntervalMs = Math.Max(100, modelLoadingPollIntervalMs);
            statusConnectTimeoutMs = Math.Max(250, statusConnectTimeoutMs);
            statusIoTimeoutMs = Math.Max(250, statusIoTimeoutMs);
            idleTimeoutSeconds = Math.Max(0, idleTimeoutSeconds);
            ownerProcessId = Math.Max(0, ownerProcessId);
            logPumpWaitFileTimeoutMs = Math.Max(1000, logPumpWaitFileTimeoutMs);
            logPumpMissingFilePollMinMs = Math.Max(30, logPumpMissingFilePollMinMs);
            logPumpMissingFilePollMaxMs = Math.Max(logPumpMissingFilePollMinMs, logPumpMissingFilePollMaxMs);
            logPumpIdlePollMinMs = Math.Max(10, logPumpIdlePollMinMs);
            logPumpIdlePollMaxMs = Math.Max(logPumpIdlePollMinMs, logPumpIdlePollMaxMs);

            RuntimePlatform platform = Application.platform;
            if (platform == RuntimePlatform.WindowsEditor || platform == RuntimePlatform.WindowsPlayer)
            {
                if (!enableWindows)
                {
                    throw new PlatformNotSupportedException("Windows runtime is disabled in BridgeRuntimeSettings.");
                }
            }
            else if (platform == RuntimePlatform.OSXEditor || platform == RuntimePlatform.OSXPlayer)
            {
                if (!enableMac)
                {
                    throw new PlatformNotSupportedException("macOS runtime is disabled in BridgeRuntimeSettings.");
                }
            }
            else if (platform == RuntimePlatform.LinuxEditor || platform == RuntimePlatform.LinuxPlayer)
            {
                if (!enableLinux)
                {
                    throw new PlatformNotSupportedException("Linux runtime is disabled in BridgeRuntimeSettings.");
                }
            }
            else
            {
                throw new PlatformNotSupportedException($"Unsupported platform for bridge runtime: {platform}");
            }
        }
    }
}
