using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoBridgeGenerationResult
    {
        public string MotionJsonCompact { get; set; }
        public KimodoRawMotionData MotionData { get; set; }
        public string MotionFormat { get; set; }
        public string RawStatus { get; set; }
        public string Message { get; set; }
    }

    public sealed class KimodoBridgeService : IDisposable
    {
        private readonly BridgeRuntimeSettings settings;
        private readonly BridgeProtocolClient protocolClient;
        private readonly BridgeProcessManager processManager;
        private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly SynchronizationContext creationContext;

        private string currentHost;
        private int currentPort = -1;
        private string currentPortFilePath = string.Empty;
        private IDisposable logSubscription;
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
            creationContext = SynchronizationContext.Current;
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

        private void EmitDebugLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (creationContext != null)
            {
                creationContext.Post(_ => Debug.Log(message), null);
                return;
            }

            Debug.Log(message);
        }

        private void EmitProgress(Action<string> progress, string message)
        {
            if (progress == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (creationContext != null)
            {
                creationContext.Post(_ => progress(message), null);
                return;
            }

            progress(message);
        }

        public async Task<bool> AttachAsync(Action<string> progress, CancellationToken token)
        {
            ThrowIfDisposed();
            ValidateRuntimeRootOrThrow();

            currentPortFilePath = BridgeEndpointResolver.GetServerPortFilePath(settings.runtimeRoot);
            if (!BridgeEndpointResolver.TryReadServerEndpoint(settings.runtimeRoot, settings.hostFallback, out string host, out int port, out _))
            {
                return false;
            }

            currentHost = host;
            currentPort = port;
            StartLogPump(progress);
            return true;
        }

        public async Task<string> StartAsync(Action<string> progress, CancellationToken token)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await StartCoreAsync(progress, token).ConfigureAwait(false);
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
            bool canReuseExistingEndpoint = false;
            if (File.Exists(currentPortFilePath))
            {
                if (!BridgeEndpointResolver.TryReadServerEndpoint(settings.runtimeRoot, settings.hostFallback, out string host, out int port, out string endpointError))
                {
                    EmitProgress(progress, $"Bridge endpoint file is invalid, starting server to recover. {endpointError}");
                }
                else
                {
                    bool reachable = await protocolClient.PingAsync(host, port, token, acceptLoading: true).ConfigureAwait(false);
                    if (reachable)
                    {
                        currentHost = host;
                        currentPort = port;
                        canReuseExistingEndpoint = true;
                    }
                    else
                    {
                        EmitProgress(progress, $"Bridge endpoint is unreachable, starting server to recover: {host}:{port}");
                    }
                }
            }

            if (canReuseExistingEndpoint)
            {
                StartLogPump(progress);
                return $"Ready - {settings.modelName} on {currentHost}:{currentPort}";
            }

            await InvalidateCurrentEndpointAsync().ConfigureAwait(false);
            bool reusingExistingProcess = processManager.IsRunning;
            if (!reusingExistingProcess)
            {
                processManager.Start(
                    settings.launcherPath,
                    settings.modelName,
                    settings.highVram,
                    settings.forceSetup,
                    settings.forceCpu,
                    settings.modelsRoot,
                    settings.idleTimeoutSeconds,
                    settings.ownerProcessId);
                EmitProgress(progress, "Bridge process launched.");
            }

            StartLogPump(progress);

            try
            {
                EmitProgress(progress, reusingExistingProcess ? "Bridge process already exists, waiting for endpoint..." : "Starting bridge...");
                await processManager.WaitUntilReadyAsync(
                    settings.runtimeRoot,
                    settings.hostFallback,
                    protocolClient,
                    settings.startupTimeoutMs,
                    settings.pollIntervalMs,
                    token).ConfigureAwait(false);

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
                await DetachCurrentConnectionAsync().ConfigureAwait(false);
                throw;
            }
            catch
            {
                await InvalidateCurrentEndpointAsync().ConfigureAwait(false);
                await DetachCoreAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal async Task<KimodoBridgeGenerationResult> GenerateAsync(
            KimodoGenerationRequestDto request,
            Action<string> progress,
            CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            ThrowIfDisposed();
            await EnsureHealthyOrThrowAsync(token).ConfigureAwait(false);
            EmitDebugLog(
                $"[KimodoBridge] Generate request: host={currentHost}:{currentPort}, " +
                $"promptLen={(request.prompt ?? string.Empty).Length}, duration={request.duration:F3}, " +
                $"steps={request.steps}, seed={(request.seed.HasValue ? request.seed.Value.ToString() : "null")}, " +
                $"constraintsPath='{request.constraints_json ?? string.Empty}', " +
                $"loopHint={request.loop_hint}, segmentIndex={request.segment_index}, transition={request.transition_duration:F3}, " +
                $"boundaryPoseLen={(request.boundary_pose_json ?? string.Empty).Length}");

            BridgeProtocolResponse response;
            try
            {
                response = await SendGenerateRequestAsync(request, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryCancelActiveGenerateAsync().ConfigureAwait(false);
                await DetachCurrentConnectionAsync().ConfigureAwait(false);
                throw;
            }

            JObject header = response?.Header;
            string status = header?.Value<string>("status") ?? string.Empty;
            string responseMessage = header?.Value<string>("message") ?? string.Empty;
            string outputFormat = header?.Value<string>("output_format") ?? string.Empty;
            string motionJson = header?.Value<string>("motion_json_compact");
            EmitDebugLog(
                $"[KimodoBridge] Generate response: status='{status}', format='{outputFormat}', hasJson={!string.IsNullOrWhiteSpace(motionJson)}, " +
                $"hasBinary={(response?.BinaryPayload != null && response.BinaryPayload.Length > 0)}, message='{responseMessage}'");
            if (!string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Unexpected bridge response status: {status}. message={responseMessage}");
            }

            if (string.Equals(outputFormat, "flatbuf_motion_v1", StringComparison.OrdinalIgnoreCase))
            {
                byte[] payload = response.BinaryPayload;
                if (payload == null || payload.Length == 0)
                {
                    throw new Exception("Bridge completed without FlatBuffer payload bytes.");
                }

                if (!KimodoRawMotionUtility.TryParseFlatBuffer(payload, out KimodoRawMotionData motionData, out string parseError))
                {
                    throw new Exception($"Failed to parse bridge FlatBuffer motion: {parseError}");
                }

                EmitProgress(progress, "Bridge generation complete.");
                return new KimodoBridgeGenerationResult
                {
                    MotionData = motionData,
                    MotionFormat = outputFormat,
                    RawStatus = status,
                    Message = string.IsNullOrWhiteSpace(responseMessage) ? "Bridge generation complete." : responseMessage
                };
            }

            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new Exception("Bridge completed without motion_json_compact.");
            }

            EmitProgress(progress, "Bridge generation complete.");
            return new KimodoBridgeGenerationResult
            {
                MotionJsonCompact = motionJson,
                MotionFormat = string.IsNullOrWhiteSpace(outputFormat) ? "json_compact" : outputFormat,
                RawStatus = status,
                Message = string.IsNullOrWhiteSpace(responseMessage) ? "Bridge generation complete." : responseMessage
            };
        }

        private Task<BridgeProtocolResponse> SendGenerateRequestAsync(
            KimodoGenerationRequestDto request,
            Action<string> progress,
            CancellationToken token)
        {
            Action<string> marshaledProgress = progress == null ? null : msg => EmitProgress(progress, msg);
            return protocolClient.GenerateAsync(
                currentHost,
                currentPort,
                request,
                marshaledProgress,
                token);
        }

        private async Task TryCancelActiveGenerateAsync()
        {
            if (!TryResolveCurrentEndpoint(out string host, out int port))
            {
                return;
            }

            using var cancelCts = new CancellationTokenSource(Math.Max(500, settings.ioTimeoutMs));
            try
            {
                await protocolClient.TryCancelGenerateAsync(host, port, cancelCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // best effort only
            }
        }

        public async Task<bool> PingAsync(CancellationToken token, bool acceptLoading = true)
        {
            ThrowIfDisposed();
            if (!TryResolveCurrentEndpoint(out string host, out int port))
            {
                await InvalidateCurrentEndpointAsync().ConfigureAwait(false);
                return false;
            }

            bool ok = await protocolClient.PingAsync(host, port, token, acceptLoading).ConfigureAwait(false);
            if (!ok)
            {
                await InvalidateCurrentEndpointAsync().ConfigureAwait(false);
                return false;
            }

            currentHost = host;
            currentPort = port;
            return true;
        }

        public async Task StopAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        public async Task DetachAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await DetachCoreAsync().ConfigureAwait(false);
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
            bool ok = await PingAsync(token, acceptLoading: true).ConfigureAwait(false);
            if (!ok)
            {
                throw new Exception($"Bridge port is unreachable. endpoint={endpointBeforePing}");
            }
        }

        private async Task StopCoreAsync(CancellationToken token)
        {
            StopLogPump();
            await protocolClient.DetachAsync().ConfigureAwait(false);

            bool endpointResolved = TryResolveCurrentEndpoint(out string host, out int port);
            if (endpointResolved)
            {
                bool quitSent = await protocolClient.TrySendQuitAsync(host, port, token).ConfigureAwait(false);
                if (!quitSent)
                {
                    bool stillReachable = await protocolClient.PingAsync(host, port, token, acceptLoading: true).ConfigureAwait(false);
                    if (stillReachable)
                    {
                        throw new InvalidOperationException($"Bridge quit command failed: {host}:{port}");
                    }
                }
            }

            processManager.DetachProcess();
            currentPort = -1;
            currentHost = settings.hostFallback;
        }

        private async Task DetachCoreAsync()
        {
            StopLogPump();
            await protocolClient.DetachAsync().ConfigureAwait(false);
            processManager.DetachProcess();
            currentPort = -1;
            currentHost = settings.hostFallback;
        }

        private async Task DetachCurrentConnectionAsync()
        {
            await protocolClient.DetachAsync().ConfigureAwait(false);
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
            await protocolClient.DetachAsync().ConfigureAwait(false);
        }

        private void StartLogPump(Action<string> progress)
        {
            logSubscription?.Dispose();
            logSubscription = null;
            if (progress == null)
            {
                return;
            }

            logSubscription = BridgeLogPump.SubscribeShared(settings.runtimeRoot, settings, progress);
        }

        private void StopLogPump()
        {
            logSubscription?.Dispose();
            logSubscription = null;
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

            if (p == RuntimePlatform.OSXEditor || p == RuntimePlatform.OSXPlayer)
            {
                if (!settings.enableMac)
                {
                    throw new PlatformNotSupportedException("Bridge macOS platform disabled.");
                }

                return new MacBridgePlatformProcess();
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
