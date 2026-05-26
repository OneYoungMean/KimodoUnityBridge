using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoUnityMotionTools.Bridge
{
    public sealed class KimodoBridgeService : IDisposable
    {
        private readonly BridgeRuntimeSettings settings;
        private readonly BridgeProtocolClient protocolClient;
        private readonly BridgeProcessManager processManager;
        private readonly BridgeLogPump logPump;
        private readonly List<BridgeLogPump> sideLogPumps = new List<BridgeLogPump>(2);

        private string currentHost;
        private int currentPort = -1;
        private string currentPortFilePath = string.Empty;
        private bool disposed;

        public KimodoBridgeService(BridgeRuntimeSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.settings.Validate();

            IBridgePlatformProcess platform = CreatePlatformProcess(this.settings);
            protocolClient = new BridgeProtocolClient(
                this.settings.connectTimeoutMs,
                this.settings.ioTimeoutMs,
                this.settings.modelLoadingTimeoutMs,
                this.settings.modelLoadingPollIntervalMs);
            processManager = new BridgeProcessManager(platform);
            logPump = new BridgeLogPump();
            currentHost = string.IsNullOrWhiteSpace(this.settings.hostFallback) ? "127.0.0.1" : this.settings.hostFallback;
        }

        public bool IsRunning
        {
            get
            {
                if (processManager.IsRunning)
                {
                    return true;
                }

                return currentPort > 0;
            }
        }

        public string RuntimeRoot => settings.runtimeRoot;
        public string LauncherPath => settings.launcherPath;

        public async Task<bool> AttachAsync(Action<string> progress, CancellationToken token)
        {
            ThrowIfDisposed();
            EnsureRuntimeRootExists();

            currentPortFilePath = BridgeEndpointResolver.GetServerPortFilePath(settings.runtimeRoot);
            if (!BridgeEndpointResolver.TryReadServerEndpoint(settings.runtimeRoot, settings.hostFallback, out string host, out int port, out _))
            {
                return false;
            }

            bool ok = await protocolClient.PingAsync(host, port, token, acceptLoading: true);
            if (!ok)
            {
                return false;
            }

            currentHost = host;
            currentPort = port;
            string attachLogPath = BridgeEndpointResolver.ResolveAttachLogPath(settings.runtimeRoot);
            StartLogPump(attachLogPath, progress);
            return true;
        }

        public async Task<string> StartAsync(Action<string> progress, CancellationToken token)
        {
            ThrowIfDisposed();
            EnsureRuntimeRootExists();
            EnsureLauncherExists();

            currentPortFilePath = BridgeEndpointResolver.GetServerPortFilePath(settings.runtimeRoot);
            if (File.Exists(currentPortFilePath))
            {
                if (BridgeEndpointResolver.TryReadServerEndpoint(settings.runtimeRoot, settings.hostFallback, out string host, out int port, out _) &&
                    await protocolClient.PingAsync(host, port, token, acceptLoading: true))
                {
                    currentHost = host;
                    currentPort = port;
                    StartLogPump(BridgeEndpointResolver.ResolveAttachLogPath(settings.runtimeRoot), progress);
                    return $"Ready - {settings.modelName} on {host}:{port}";
                }

                TryDeleteServerPortFile();
            }

            processManager.Start(
                settings.launcherPath,
                settings.modelName,
                settings.highVram,
                settings.forceSetup,
                settings.modelsRoot);
            progress?.Invoke("Bridge process launched.");
            StartLogPump(BridgeEndpointResolver.ResolveAttachLogPath(settings.runtimeRoot), progress);

            try
            {
                progress?.Invoke("Starting bridge...");
                await processManager.WaitUntilReadyAsync(
                    settings.runtimeRoot,
                    settings.hostFallback,
                    protocolClient,
                    settings.startupTimeoutMs,
                    settings.pollIntervalMs,
                    token);

                if (!BridgeEndpointResolver.TryReadServerEndpoint(settings.runtimeRoot, settings.hostFallback, out string host, out int port, out string endpointError))
                {
                    throw new Exception($"Bridge started but server endpoint missing. {endpointError}");
                }

                currentHost = host;
                currentPort = port;
                return $"Ready - {settings.modelName} on {host}:{port}";
            }
            catch
            {
                await StopAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task<string> GenerateAsync(
            string prompt,
            float durationSeconds,
            int? seed,
            int diffusionSteps,
            string constraintsJson,
            string boundaryPoseJson,
            bool loopHint,
            int segmentIndex,
            float transitionDurationSeconds,
            Action<string> progress,
            CancellationToken token)
        {
            ThrowIfDisposed();
            await EnsureHealthyOrThrowAsync(token);
            Debug.Log(
                $"[KimodoBridge] Generate request: host={currentHost}:{currentPort}, " +
                $"promptLen={(prompt ?? string.Empty).Length}, duration={durationSeconds:F3}, " +
                $"steps={diffusionSteps}, seed={(seed.HasValue ? seed.Value.ToString() : "null")}, " +
                $"constraintsPath='{constraintsJson ?? string.Empty}', " +
                $"loopHint={loopHint}, segmentIndex={segmentIndex}, transition={transitionDurationSeconds:F3}, " +
                $"boundaryPoseLen={(boundaryPoseJson ?? string.Empty).Length}");

            JObject response = await protocolClient.GenerateAsync(
                currentHost,
                currentPort,
                prompt,
                durationSeconds,
                seed,
                diffusionSteps,
                constraintsJson,
                boundaryPoseJson,
                loopHint,
                segmentIndex,
                transitionDurationSeconds,
                progress,
                token);

            string status = response?.Value<string>("status") ?? string.Empty;
            string responseMessage = response?.Value<string>("message") ?? string.Empty;
            string motionJson = response?.Value<string>("motion_json_compact");
            Debug.Log(
                $"[KimodoBridge] Generate response: status='{status}', hasMotion={!string.IsNullOrWhiteSpace(motionJson)}, " +
                $"message='{responseMessage}'");
            if (!string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Unexpected bridge response status: {status}. message={responseMessage}");
            }

            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new Exception("Bridge completed without motion_json_compact.");
            }

            progress?.Invoke("Bridge generation complete.");
            return motionJson;
        }

        public async Task<bool> PingAsync(CancellationToken token, bool acceptLoading = true)
        {
            ThrowIfDisposed();
            if (!TryResolveCurrentEndpoint(out string host, out int port))
            {
                return false;
            }

            currentHost = host;
            currentPort = port;
            return await protocolClient.PingAsync(host, port, token, acceptLoading);
        }

        public async Task StopAsync(CancellationToken token)
        {
            ThrowIfDisposed();

            StopLogPump();
            await protocolClient.DetachAsync();

            if (TryResolveCurrentEndpoint(out string host, out int port))
            {
                _ = await protocolClient.TrySendQuitAsync(host, port, token);
            }

            processManager.KillProcessTree();
            TryDeleteServerPortFile();
            currentPort = -1;
            currentHost = settings.hostFallback;
        }

        public async Task DetachAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            StopLogPump();
            await protocolClient.DetachAsync();
        }

        public async Task KillAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            StopLogPump();
            await protocolClient.DetachAsync();
            processManager.KillProcessTree();
            TryDeleteServerPortFile();
            currentPort = -1;
            currentHost = settings.hostFallback;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                StopLogPump();
                protocolClient?.Dispose();
                processManager?.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        private void EnsureRuntimeRootExists()
        {
            if (string.IsNullOrWhiteSpace(settings.runtimeRoot))
            {
                throw new InvalidOperationException("Bridge runtimeRoot is empty.");
            }

            if (!Directory.Exists(settings.runtimeRoot))
            {
                throw new DirectoryNotFoundException($"Bridge runtime root not found: {settings.runtimeRoot}");
            }
        }

        private void EnsureLauncherExists()
        {
            if (string.IsNullOrWhiteSpace(settings.launcherPath))
            {
                throw new InvalidOperationException("Bridge launcher path is empty.");
            }

            if (!File.Exists(settings.launcherPath))
            {
                throw new FileNotFoundException($"Bridge launcher not found: {settings.launcherPath}");
            }
        }

        private async Task EnsureHealthyOrThrowAsync(CancellationToken token)
        {
            bool ok = await PingAsync(token, acceptLoading: true);
            if (!ok)
            {
                throw new Exception("Bridge port is unreachable.");
            }
        }

        private bool TryResolveCurrentEndpoint(out string host, out int port)
        {
            if (currentPort > 0 && !string.IsNullOrWhiteSpace(currentHost))
            {
                host = currentHost;
                port = currentPort;
                return true;
            }

            bool ok = BridgeEndpointResolver.TryReadServerEndpoint(settings.runtimeRoot, settings.hostFallback, out host, out port, out _);
            return ok;
        }

        private void StartLogPump(string logPath, Action<string> progress)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            StopSideLogPumps();
            string mainLogFullPath = GetNormalizedPathOrEmpty(logPath);
            logPump.Start(logPath, line =>
            {
                string msg = $"[Bridge] {line}";
                progress?.Invoke(msg);
                Debug.Log(msg);
            }, settings);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "bridge_server.log"), "[BridgeServer]", mainLogFullPath, progress);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "bridge_message.log"), "[BridgeMessage]", mainLogFullPath, progress);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "run_server.log"), "[RunServer]", mainLogFullPath, progress);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "setup.log"), "[Setup]", mainLogFullPath, progress);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "download_model.log"), "[Download]", mainLogFullPath, progress);
        }

        private void StopLogPump()
        {
            logPump.Stop();
            StopSideLogPumps();
        }

        private void StartSideLogPump(string logPath, string tag, Action<string> progress)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            var pump = new BridgeLogPump();
            sideLogPumps.Add(pump);
            pump.Start(logPath, line =>
            {
                string msg = $"{tag} {line}";
                progress?.Invoke(msg);
                Debug.Log(msg);
            }, settings);
        }

        private void StartSideLogPumpIfDifferent(string logPath, string tag, string mainLogFullPath, Action<string> progress)
        {
            string sideLogFullPath = GetNormalizedPathOrEmpty(logPath);
            if (!string.IsNullOrWhiteSpace(mainLogFullPath) &&
                !string.IsNullOrWhiteSpace(sideLogFullPath) &&
                string.Equals(mainLogFullPath, sideLogFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StartSideLogPump(logPath, tag, progress);
        }

        private static string GetNormalizedPathOrEmpty(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return string.Empty;
            }
        }

        private void StopSideLogPumps()
        {
            if (sideLogPumps.Count == 0)
            {
                return;
            }

            for (int i = 0; i < sideLogPumps.Count; i++)
            {
                try
                {
                    sideLogPumps[i]?.Stop();
                    sideLogPumps[i]?.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
            sideLogPumps.Clear();
        }

        private void TryDeleteServerPortFile()
        {
            string path = string.IsNullOrWhiteSpace(currentPortFilePath)
                ? BridgeEndpointResolver.GetServerPortFilePath(settings.runtimeRoot)
                : currentPortFilePath;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup failure
            }
        }

        private static IBridgePlatformProcess CreatePlatformProcess(BridgeRuntimeSettings settings)
        {
            RuntimePlatform p = Application.platform;
            if (p == RuntimePlatform.WindowsEditor || p == RuntimePlatform.WindowsPlayer)
            {
                if (!settings.enableWindows)
                {
                    throw new PlatformNotSupportedException("Bridge Windows platform disabled.");
                }

                return new WindowsBridgePlatformProcess();
            }

            if (p == RuntimePlatform.LinuxEditor || p == RuntimePlatform.LinuxPlayer)
            {
                if (!settings.enableLinux)
                {
                    throw new PlatformNotSupportedException("Bridge Linux platform disabled.");
                }

                return new LinuxBridgePlatformProcess();
            }

            throw new PlatformNotSupportedException($"Unsupported bridge platform: {p}");
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(KimodoBridgeService));
            }
        }
    }
}
