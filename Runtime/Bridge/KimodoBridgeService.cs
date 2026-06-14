using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoBridge
{
    public sealed class KimodoBridgeService : IDisposable
    {
        private const int BridgeMessageLogPumpWaitFileTimeoutMs = 60000;
        private const int BridgeMessageLogPumpMissingFilePollMs = 90;

        private readonly BridgeRuntimeSettings settings;
        private readonly BridgeProtocolClient protocolClient;
        private readonly BridgeProcessManager processManager;
        private readonly BridgeLogPump logPump;
        private readonly List<BridgeLogPump> sideLogPumps = new List<BridgeLogPump>(2);
        private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);

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
            ValidateRuntimeRootOrThrow();

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
            await lifecycleGate.WaitAsync(token);
            try
            {
                return await StartCoreAsync(progress, token);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private async Task<string> StartCoreAsync(Action<string> progress, CancellationToken token)
        {
            ValidateRuntimeRootOrThrow();
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

                await InvalidateCurrentEndpointAsync();
                TryDeleteServerPortFile();
            }

            await InvalidateCurrentEndpointAsync();
            bool reusingExistingProcess = processManager.IsRunning;
            if (!reusingExistingProcess)
            {
                processManager.Start(
                    settings.launcherPath,
                    settings.modelName,
                    settings.highVram,
                    settings.forceSetup,
                    settings.modelsRoot,
                    settings.idleTimeoutSeconds);
                progress?.Invoke("Bridge process launched.");
            }

            StartLogPump(BridgeEndpointResolver.ResolveAttachLogPath(settings.runtimeRoot), progress);

            try
            {
                progress?.Invoke(reusingExistingProcess ? "Bridge process already exists, waiting for endpoint..." : "Starting bridge...");
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
            catch (OperationCanceledException)
            {
                await InvalidateCurrentEndpointAsync();
                if (settings.preserveProcessOnCancellation)
                {
                    await DetachCoreAsync();
                }
                else
                {
                    await StopCoreAsync(CancellationToken.None);
                }
                throw;
            }
            catch
            {
                await InvalidateCurrentEndpointAsync();
                await StopCoreAsync(CancellationToken.None);
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
            await EnsureHealthyOrStartAsync(progress, token);
            Debug.Log(
                $"[KimodoBridge] Generate request: host={currentHost}:{currentPort}, " +
                $"promptLen={(prompt ?? string.Empty).Length}, duration={durationSeconds:F3}, " +
                $"steps={diffusionSteps}, seed={(seed.HasValue ? seed.Value.ToString() : "null")}, " +
                $"constraintsPath='{constraintsJson ?? string.Empty}', " +
                $"loopHint={loopHint}, segmentIndex={segmentIndex}, transition={transitionDurationSeconds:F3}, " +
                $"boundaryPoseLen={(boundaryPoseJson ?? string.Empty).Length}");

            JObject response;
            try
            {
                response = await SendGenerateRequestAsync(
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
            }
            catch (Exception ex) when (IsLikelyTransportFailure(ex))
            {
                progress?.Invoke($"Bridge connection lost, restarting bridge... {ex.Message}");
                Debug.LogWarning($"[KimodoBridge] Transport failure during generate. Restarting bridge once. {ex}");
                await InvalidateCurrentEndpointAsync();
                TryDeleteServerPortFile();
                _ = await StartAsync(progress, token);
                await EnsureHealthyOrThrowAsync(token);
                response = await SendGenerateRequestAsync(
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
            }

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

        private Task<JObject> SendGenerateRequestAsync(
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
            return protocolClient.GenerateAsync(
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
        }

        public async Task<bool> PingAsync(CancellationToken token, bool acceptLoading = true)
        {
            ThrowIfDisposed();
            if (!TryResolveCurrentEndpoint(out string host, out int port))
            {
                await InvalidateCurrentEndpointAsync();
                return false;
            }

            bool ok = await protocolClient.PingAsync(host, port, token, acceptLoading);
            if (!ok)
            {
                await InvalidateCurrentEndpointAsync();
                return false;
            }

            currentHost = host;
            currentPort = port;
            return true;
        }

        public async Task StopAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(token);
            try
            {
                await StopCoreAsync(token);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        public async Task DetachAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(token);
            try
            {
                await DetachCoreAsync();
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        public async Task KillAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(token);
            try
            {
                await KillCoreAsync();
            }
            finally
            {
                lifecycleGate.Release();
            }
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
                lifecycleGate?.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        private void ValidateRuntimeRootOrThrow()
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
            string endpointBeforePing = TryResolveCurrentEndpoint(out string host, out int port)
                ? $"{host}:{port}"
                : "(none)";
            bool ok = await PingAsync(token, acceptLoading: true);
            if (!ok)
            {
                throw new Exception($"Bridge port is unreachable. endpoint={endpointBeforePing}");
            }
        }

        private async Task EnsureHealthyOrStartAsync(Action<string> progress, CancellationToken token)
        {
            bool ok = await PingAsync(token, acceptLoading: true);
            if (ok)
            {
                return;
            }

            progress?.Invoke("Bridge endpoint is unreachable, restarting bridge...");
            _ = await StartAsync(progress, token);
            await EnsureHealthyOrThrowAsync(token);
        }

        private async Task StopCoreAsync(CancellationToken token)
        {
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

        private async Task DetachCoreAsync()
        {
            StopLogPump();
            await protocolClient.DetachAsync();
            processManager.DetachProcess();
            currentPort = -1;
            currentHost = settings.hostFallback;
        }

        private async Task KillCoreAsync()
        {
            StopLogPump();
            await protocolClient.DetachAsync();
            processManager.KillProcessTree();
            TryDeleteServerPortFile();
            currentPort = -1;
            currentHost = settings.hostFallback;
        }

        private static bool IsLikelyTransportFailure(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is IOException ||
                    current is ObjectDisposedException)
                {
                    return true;
                }

                string typeName = current.GetType().Name;
                if (string.Equals(typeName, "SocketException", StringComparison.Ordinal))
                {
                    return true;
                }

                string message = current.Message ?? string.Empty;
                if (message.IndexOf("Bridge connect timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Bridge read timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Empty bridge response", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveCurrentEndpoint(out string host, out int port)
        {
            string filePath = BridgeEndpointResolver.GetServerPortFilePath(settings.runtimeRoot);
            if (BridgeEndpointResolver.TryReadServerEndpoint(settings.runtimeRoot, settings.hostFallback, out string fileHost, out int filePort, out _))
            {
                host = fileHost;
                port = filePort;
                return true;
            }

            if (currentPort > 0 && !string.IsNullOrWhiteSpace(currentHost) && !File.Exists(filePath))
            {
                host = currentHost;
                port = currentPort;
                return true;
            }

            host = string.IsNullOrWhiteSpace(settings.hostFallback) ? "127.0.0.1" : settings.hostFallback;
            port = -1;
            return false;
        }

        private async Task InvalidateCurrentEndpointAsync()
        {
            currentPort = -1;
            currentHost = settings.hostFallback;
            await protocolClient.DetachAsync();
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
            StartSideLogPumpIfDifferent(
                Path.Combine(settings.runtimeRoot, "log", "bridge_message.log"),
                "[BridgeMessage]",
                mainLogFullPath,
                progress,
                BridgeMessageLogPumpWaitFileTimeoutMs,
                BridgeMessageLogPumpMissingFilePollMs,
                BridgeMessageLogPumpMissingFilePollMs);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "run_server.log"), "[RunServer]", mainLogFullPath, progress);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "setup.log"), "[Setup]", mainLogFullPath, progress);
            StartSideLogPumpIfDifferent(Path.Combine(settings.runtimeRoot, "log", "download_model.log"), "[Download]", mainLogFullPath, progress);
        }

        private void StopLogPump()
        {
            logPump.Stop();
            StopSideLogPumps();
        }

        private void StartSideLogPump(
            string logPath,
            string tag,
            Action<string> progress,
            int? waitFileTimeoutMsOverride = null,
            int? missingFilePollMinMsOverride = null,
            int? missingFilePollMaxMsOverride = null)
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
            }, settings, waitFileTimeoutMsOverride, missingFilePollMinMsOverride, missingFilePollMaxMsOverride);
        }

        private void StartSideLogPumpIfDifferent(
            string logPath,
            string tag,
            string mainLogFullPath,
            Action<string> progress,
            int? waitFileTimeoutMsOverride = null,
            int? missingFilePollMinMsOverride = null,
            int? missingFilePollMaxMsOverride = null)
        {
            string sideLogFullPath = GetNormalizedPathOrEmpty(logPath);
            if (!string.IsNullOrWhiteSpace(mainLogFullPath) &&
                !string.IsNullOrWhiteSpace(sideLogFullPath) &&
                string.Equals(mainLogFullPath, sideLogFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StartSideLogPump(logPath, tag, progress, waitFileTimeoutMsOverride, missingFilePollMinMsOverride, missingFilePollMaxMsOverride);
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
