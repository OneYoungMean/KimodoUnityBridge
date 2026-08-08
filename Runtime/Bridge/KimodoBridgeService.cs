using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KimodoBridge
{
    public sealed class KimodoBridgeGenerationResult
    {
        public string MotionJsonCompact { get; set; }
        public KimodoRawMotionData MotionData { get; set; }
        public string MotionFormat { get; set; }
        public string RawStatus { get; set; }
        public string Message { get; set; }
        public byte[] MotionBytes { get; set; }
        public string MotionRepFingerprint { get; set; }
        public int? ResolvedSeed { get; set; }
        public int StartFrame { get; set; }
        public int EndFrameExclusive { get; set; }
        public double? ArdyPlaybackReserveSeconds { get; set; }
        public string AnalysisJson { get; set; }
    }

    public sealed class KimodoBridgeService : IDisposable
    {
        private sealed class ActiveLogPump
        {
            public string Path = string.Empty;
            public BridgeLogPump Pump;
        }

        private sealed class ResolvedRuntimeContext
        {
            public string RuntimeRoot = string.Empty;
            public string LauncherPath = string.Empty;
            public bool? EnableKimodoStaticGraph;
        }

        private static readonly object RegistryLock = new object();
        private static readonly HashSet<KimodoBridgeService> Registry = new HashSet<KimodoBridgeService>();
        private static readonly Lazy<BridgeProcessManager> GlobalProcessManager =
            new Lazy<BridgeProcessManager>(
                () => new BridgeProcessManager(CreatePlatformProcess()),
                LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly Lazy<KimodoBridgeService> SharedInstance =
            new Lazy<KimodoBridgeService>(() => new KimodoBridgeService(true), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly BridgeProtocolClient protocolClient;
        private readonly BridgeProcessManager processManager;
        private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly SynchronizationContext creationContext;
        private readonly List<ActiveLogPump> logPumps = new List<ActiveLogPump>(4);
        private readonly bool isDefaultSession;

        private string currentHost = DefaultHost;
        private int currentPort = -1;
        private string currentRuntimeRoot = string.Empty;
        private string textEncoderStatusMessage = string.Empty;
        private int sessionVersion;
        private int stopRequested;
        private int disposeStarted;
        private bool explicitSessionOpened;
        private string protocolSessionId = "session:default";

        private const string DefaultHost = "127.0.0.1";

        private KimodoBridgeService(bool isDefaultSession)
        {
            this.isDefaultSession = isDefaultSession;
            protocolClient = new BridgeProtocolClient();
            processManager = GlobalProcessManager.Value;
            creationContext = SynchronizationContext.Current;
            lock (RegistryLock)
            {
                Registry.Add(this);
            }
        }

        public static KimodoBridgeService Shared => SharedInstance.Value;

        public static KimodoBridgeService CreateOwned()
        {
            return new KimodoBridgeService(false);
        }

        public bool IsConnected => protocolClient.IsConnected;
        public bool IsDefaultSession => isDefaultSession;
        public bool IsDisposed => Volatile.Read(ref disposeStarted) != 0;
        public string TextEncoderStatusMessage => Volatile.Read(ref textEncoderStatusMessage) ?? string.Empty;

        public Task<KimodoBridgeGenerationResult> GenerateAsync(
            string prompt,
            float durationSeconds,
            CancellationToken token = default)
        {
            return GenerateAsync(
                new KimodoGenerationRequestDto
                {
                    prompt = prompt ?? string.Empty,
                    duration = durationSeconds
                },
                progress: null,
                token);
        }

        public Task<KimodoBridgeGenerationResult> GenerateAsync(
            KimodoGenerationRequestDto request,
            CancellationToken token = default)
        {
            return GenerateAsync(request, progress: null, token);
        }

        internal async Task WarmupAsync(
            Action<string> progress,
            CancellationToken token)
        {
            await EnsureConnectedAsync(progress, token).ConfigureAwait(false);
        }

        internal async Task<JObject> ActivateRuntimeAsync(
            string model,
            string textEncoderMode,
            string modelsRoot,
            Action<string> progress,
            CancellationToken token)
        {
            ThrowIfStopRequested();
            await EnsureConnectedAsync(progress, token).ConfigureAwait(false);
            BridgeProtocolResponse response = await protocolClient.ActivateRuntimeAsync(
                currentHost,
                currentPort,
                model,
                textEncoderMode,
                modelsRoot,
                token).ConfigureAwait(false);
            JObject header = response?.Header ?? throw new InvalidOperationException("Bridge activation returned no response.");
            if (!string.Equals(header.Value<string>("status"), "done", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(header.Value<string>("message") ?? "Bridge runtime activation failed.");
            }
            ReportProgress(progress, "Kimodo runtime activated.");
            return header;
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

            ThrowIfStopRequested();
            int requestSessionVersion = Volatile.Read(ref sessionVersion);
            string taskId = string.Empty;
            try
            {
                ThrowIfStopRequested();
                ThrowIfSessionChanged(requestSessionVersion);
                await EnsureConnectedAsync(progress, token).ConfigureAwait(false);
                ThrowIfSessionChanged(requestSessionVersion);

                if (string.IsNullOrWhiteSpace(request.task_id))
                {
                    request.task_id = Guid.NewGuid().ToString("N");
                }

                if (request.owner_pid <= 0)
                {
                    request.owner_pid = Process.GetCurrentProcess().Id;
                }

                taskId = request.task_id;
                EmitDebugLog(
                    $"[KimodoBridge] Generate request: host={currentHost}:{currentPort}, " +
                    $"taskId='{request.task_id}', " +
                    $"promptLen={(request.prompt ?? string.Empty).Length}, " +
                    $"duration={(request.duration.HasValue ? request.duration.Value.ToString("F3") : "<stream>")}, " +
                    $"steps={request.steps}, seed={(request.seed.HasValue ? request.seed.Value.ToString() : "null")}, " +
                    $"model='{request.model ?? string.Empty}', text_encoder_mode='{request.text_encoder_mode ?? string.Empty}', " +
                    $"simulate_free_vram_gb={(request.simulate_free_vram_gb.HasValue ? request.simulate_free_vram_gb.Value.ToString() : "auto")}, " +
                    $"models_root='{request.models_root ?? string.Empty}'");

                Task<BridgeProtocolResponse> protocolTask = SendGenerateRequestAsync(request, progress, CancellationToken.None);
                BridgeProtocolResponse response = await AwaitGenerateCompletionAsync(protocolTask, taskId, token).ConfigureAwait(false);

                JObject header = response?.Header;
                string status = header?.Value<string>("status") ?? string.Empty;
                string responseMessage = header?.Value<string>("message") ?? string.Empty;
                string outputFormat = header?.Value<string>("output_format") ?? string.Empty;
                string motionJson = header?.Value<string>("motion_json_compact");
                string analysisJson = header?["analysis"]?.ToString(Newtonsoft.Json.Formatting.None);
                string errorCode = header?.Value<string>("error_code") ?? string.Empty;
                string resolvedEncoderMode = header?.Value<string>("text_encoder_mode") ?? string.Empty;
                string resolvedEncoderRoute = header?.Value<string>("text_encoder_route") ?? string.Empty;
                string resolvedEncoderDevice = header?.Value<string>("text_encoder_device") ?? string.Empty;
                string resolvedEncoderReason = header?.Value<string>("text_encoder_reason") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(resolvedEncoderRoute) || !string.IsNullOrWhiteSpace(resolvedEncoderDevice))
                {
                    Volatile.Write(
                        ref textEncoderStatusMessage,
                        $"Resolved: {resolvedEncoderMode} / {resolvedEncoderRoute} / {resolvedEncoderDevice} ({resolvedEncoderReason})");
                }
                EmitDebugLog(
                    $"[KimodoBridge] Generate response: status='{status}', format='{outputFormat}', hasJson={!string.IsNullOrWhiteSpace(motionJson)}, " +
                    $"hasBinary={(response?.BinaryPayload != null && response.BinaryPayload.Length > 0)}, message='{responseMessage}', " +
                    $"frames=[{header?.Value<int?>("start_frame") ?? 0},{header?.Value<int?>("end_frame_exclusive") ?? 0}), " +
                    $"text_encoder='{resolvedEncoderMode}/{resolvedEncoderRoute}/{resolvedEncoderDevice}'");

                if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OperationCanceledException(
                        string.IsNullOrWhiteSpace(responseMessage) ? "Bridge generation cancelled." : responseMessage);
                }

                if (!string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception(
                        $"Unexpected bridge response status: {status}. error_code={errorCode}. message={responseMessage}");
                }

                if (string.Equals(outputFormat, "kmb_v1", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] payload = response.BinaryPayload ?? Array.Empty<byte>();
                    KimodoRawMotionData motionData = null;
                    if (payload.Length > 0 &&
                        !KimodoRawMotionUtility.TryParseFlatBuffer(payload, out motionData, out string parseError))
                    {
                        throw new Exception($"Failed to parse bridge KMB: {parseError}");
                    }
                    ReportProgress(progress, "Bridge generation complete.");
                    return new KimodoBridgeGenerationResult
                    {
                        MotionData = motionData,
                        MotionBytes = payload,
                        MotionFormat = outputFormat,
                        RawStatus = status,
                        Message = string.IsNullOrWhiteSpace(responseMessage) ? "Bridge generation complete." : responseMessage,
                        MotionRepFingerprint = header?.Value<string>("motion_rep_fingerprint") ?? string.Empty,
                        ResolvedSeed = header?.Value<int?>("resolved_seed"),
                        StartFrame = header?.Value<int?>("start_frame") ?? 0,
                        EndFrameExclusive = header?.Value<int?>("end_frame_exclusive") ?? 0,
                        ArdyPlaybackReserveSeconds = header?.Value<double?>("ardy_playback_reserve_seconds"),
                        AnalysisJson = analysisJson
                    };
                }

                if (string.IsNullOrWhiteSpace(motionJson))
                {
                    throw new Exception("Bridge completed without motion_json_compact.");
                }

                ReportProgress(progress, "Bridge generation complete.");
                return new KimodoBridgeGenerationResult
                {
                    MotionJsonCompact = motionJson,
                    MotionFormat = string.IsNullOrWhiteSpace(outputFormat) ? "json_compact" : outputFormat,
                    RawStatus = status,
                    Message = string.IsNullOrWhiteSpace(responseMessage) ? "Bridge generation complete." : responseMessage,
                    AnalysisJson = analysisJson
                };
            }
            catch (IOException exception) when (requestSessionVersion != Volatile.Read(ref sessionVersion))
            {
                ReportProgress(progress, "Server has been stopped.");
                EmitDebugLog("[KimodoBridge] Server has been stopped.");
                throw new OperationCanceledException("Server has been stopped.", exception);
            }
        }

        public Task CancelActiveAsync(CancellationToken token = default)
        {
            return CancelTaskAsync(string.Empty, token);
        }

        internal bool QueueCancelTask(string taskId)
        {
            if (!TryResolveCurrentEndpoint(out string host, out int port) || !protocolClient.IsConnected)
            {
                return false;
            }
            _ = protocolClient.TryCancelGenerateAsync(host, port, taskId, CancellationToken.None);
            return true;
        }

        public async Task StopAsync(CancellationToken token = default)
        {
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Volatile.Write(ref stopRequested, 1);
                Interlocked.Increment(ref sessionVersion);

                bool hasEndpoint = TryResolveCurrentEndpoint(out string host, out int port);
                int serverProcessId = -1;
                if (isDefaultSession && !hasEndpoint)
                {
                    try
                    {
                        currentRuntimeRoot = ResolveRuntimeContext().RuntimeRoot;
                        hasEndpoint = TryReadRuntimeEndpoint(currentRuntimeRoot, out host, out port);
                    }
                    catch
                    {
                        // There may be no installed runtime to stop.
                    }
                }
                if (isDefaultSession &&
                    IsLoopbackHost(host) &&
                    !string.IsNullOrWhiteSpace(currentRuntimeRoot))
                {
                    BridgeEndpointResolver.TryReadServerProcessId(currentRuntimeRoot, out serverProcessId);
                }

                if (hasEndpoint)
                {
                    try
                    {
                        if (!protocolClient.IsConnected)
                        {
                            await protocolClient.ConnectAsync(host, port, token).ConfigureAwait(false);
                        }
                        await protocolClient.CloseSessionAsync(host, port, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Disconnect cleanup is the fallback for a lost server.
                    }
                }

                await protocolClient.DetachAsync().ConfigureAwait(false);
                if (isDefaultSession)
                {
                    await DetachOwnedConnectionsAsync().ConfigureAwait(false);
                    await StopLogPumpsAsync(token).ConfigureAwait(false);
                    if (hasEndpoint)
                    {
                        await BridgeProcessManager.WaitUntilStoppedAsync(
                            host,
                            port,
                            serverProcessId,
                            BridgeRuntimeDefaults.ShutdownTimeoutMs,
                            BridgeRuntimeDefaults.PollIntervalMs,
                            token).ConfigureAwait(false);
                    }
                    processManager.DetachProcess();
                    DeleteServerPortFile();
                }
                ResetConnectionState();
            }
            finally
            {
                Volatile.Write(ref stopRequested, 0);
                lifecycleGate.Release();
            }
        }

        private async Task EnsureConnectedAsync(Action<string> progress, CancellationToken token)
        {
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsConnected && currentPort > 0 && (isDefaultSession || explicitSessionOpened))
                {
                    return;
                }

                ResolvedRuntimeContext context = ResolveRuntimeContext();
                currentRuntimeRoot = context.RuntimeRoot;

                if (TryReadRuntimeEndpoint(context.RuntimeRoot, out string host, out int port))
                {
                    try
                    {
                        await protocolClient.ConnectAsync(host, port, token).ConfigureAwait(false);
                        currentHost = host;
                        currentPort = port;
                        await EnsureProtocolSessionAsync(host, port, token).ConfigureAwait(false);
                        StartLogPumpsIfNeeded();
                        StartRuntimeLogPumpsIfNeeded();
                        ReportProgress(progress, $"Bridge attached to {host}:{port}.");
                        return;
                    }
                    catch
                    {
                        await protocolClient.DetachAsync().ConfigureAwait(false);
                        currentHost = DefaultHost;
                        currentPort = -1;
                    }
                }

                if (!processManager.IsRunning)
                {
                    processManager.Start(
                        context.LauncherPath,
                        ownerProcessId: Process.GetCurrentProcess().Id,
                        enableKimodoStaticGraph: context.EnableKimodoStaticGraph);
                    ReportProgress(progress, "Bridge process launched.");
                }
                else
                {
                    ReportProgress(progress, "Bridge process already exists. Waiting for QuickServer...");
                }

                StartLogPumpsIfNeeded();
                ReportProgress(progress, "Waiting for QuickServer...");

                await processManager.WaitUntilReadyAsync(
                    context.RuntimeRoot,
                    DefaultHost,
                    BridgeRuntimeDefaults.StartupTimeoutMs,
                    BridgeRuntimeDefaults.PollIntervalMs,
                    token).ConfigureAwait(false);

                if (!TryReadRuntimeEndpoint(context.RuntimeRoot, out host, out port))
                {
                    throw new Exception($"QuickServer started but serverport is missing under '{context.RuntimeRoot}'.");
                }

                await protocolClient.ConnectAsync(host, port, token).ConfigureAwait(false);
                currentHost = host;
                currentPort = port;
                await EnsureProtocolSessionAsync(host, port, token).ConfigureAwait(false);
                StartRuntimeLogPumpsIfNeeded();
                ReportProgress(progress, $"Bridge attached to {host}:{port}.");
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private async Task EnsureProtocolSessionAsync(string host, int port, CancellationToken token)
        {
            if (isDefaultSession || explicitSessionOpened)
            {
                return;
            }
            protocolSessionId = await protocolClient.OpenSessionAsync(host, port, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(protocolSessionId))
            {
                throw new InvalidOperationException("QuickServer did not return an explicit Session id.");
            }
            explicitSessionOpened = true;
        }

        private async Task<BridgeProtocolResponse> AwaitGenerateCompletionAsync(
            Task<BridgeProtocolResponse> protocolTask,
            string taskId,
            CancellationToken token)
        {
            if (!token.CanBeCanceled)
            {
                return await protocolTask.ConfigureAwait(false);
            }

            Task cancellationTask = Task.Delay(Timeout.Infinite, token);
            Task completed = await Task.WhenAny(protocolTask, cancellationTask).ConfigureAwait(false);
            if (completed == protocolTask)
            {
                return await protocolTask.ConfigureAwait(false);
            }

            await CancelTaskAsync(taskId, CancellationToken.None).ConfigureAwait(false);
            return await protocolTask.ConfigureAwait(false);
        }

        private Task<BridgeProtocolResponse> SendGenerateRequestAsync(
            KimodoGenerationRequestDto request,
            Action<string> progress,
            CancellationToken token)
        {
            return protocolClient.GenerateAsync(
                currentHost,
                currentPort,
                request,
                message => ReportProgress(progress, message),
                token);
        }

        private async Task CancelTaskAsync(string taskId, CancellationToken token)
        {
            if (!TryResolveCurrentEndpoint(out string host, out int port))
            {
                return;
            }

            try
            {
                await protocolClient.TryCancelGenerateAsync(host, port, taskId, token).ConfigureAwait(false);
            }
            catch
            {
                // best effort only
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

            host = DefaultHost;
            port = -1;
            return false;
        }

        private bool TryReadRuntimeEndpoint(string runtimeRoot, out string host, out int port)
        {
            return BridgeEndpointResolver.TryReadServerEndpoint(runtimeRoot, DefaultHost, out host, out port, out _);
        }

        private static bool IsLoopbackHost(string host)
        {
            return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
        }

        private void DeleteServerPortFile()
        {
            if (string.IsNullOrWhiteSpace(currentRuntimeRoot))
            {
                return;
            }

            string path = BridgeEndpointResolver.GetServerPortFilePath(currentRuntimeRoot);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // best effort only
            }
        }

        private void ResetConnectionState()
        {
            currentHost = DefaultHost;
            currentPort = -1;
            currentRuntimeRoot = string.Empty;
            explicitSessionOpened = false;
            protocolSessionId = isDefaultSession ? "session:default" : string.Empty;
        }

        private void EmitDebugLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (creationContext != null)
            {
                creationContext.Post(_ => UnityEngine.Debug.Log(message), null);
                return;
            }

            Debug.Log(message);
        }

        private void ReportProgress(Action<string> progress, string message)
        {
            if (progress == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (creationContext != null)
            {
                creationContext.Post(_ => SafeInvokeProgress(progress, message), null);
                return;
            }

            SafeInvokeProgress(progress, message);
        }

        private void StartLogPumpsIfNeeded()
        {
            if (!isDefaultSession)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(currentRuntimeRoot))
            {
                return;
            }

            if (logPumps.Count > 0)
            {
                return;
            }

            StartLogPumpForPath(
                Path.Combine(currentRuntimeRoot, "log", "bridge_message.log"),
                "[BridgeMessage]",
                BridgeRuntimeDefaults.LogPumpWaitFileTimeoutMs * 3,
                BridgeRuntimeDefaults.LogPumpMissingFilePollMinMs,
                BridgeRuntimeDefaults.LogPumpMissingFilePollMinMs);
            StartLogPumpForPath(Path.Combine(currentRuntimeRoot, "log", "run_server.log"), "[RunServer]");
            StartLogPumpForPath(Path.Combine(currentRuntimeRoot, "log", "setup.log"), "[Setup]");
        }

        private void StartRuntimeLogPumpsIfNeeded()
        {
            if (!isDefaultSession || string.IsNullOrWhiteSpace(currentRuntimeRoot))
            {
                return;
            }

            StartLogPumpForPath(Path.Combine(currentRuntimeRoot, "log", "bridge_server.log"), "[BridgeServer]");
            StartLogPumpForPath(BridgeEndpointResolver.ResolveAttachLogPath(currentRuntimeRoot), "[Bridge]");
        }

        private void StartLogPumpForPath(
            string logPath,
            string tag,
            int? waitFileTimeoutMsOverride = null,
            int? missingFilePollMinMsOverride = null,
            int? missingFilePollMaxMsOverride = null)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            string normalizedPath = NormalizePathOrEmpty(logPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            for (int i = 0; i < logPumps.Count; i++)
            {
                if (string.Equals(logPumps[i].Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            var pump = new BridgeLogPump();
            logPumps.Add(new ActiveLogPump
            {
                Path = normalizedPath,
                Pump = pump
            });
            pump.Start(
                normalizedPath,
                line => OnLogLine($"{tag} {line}"),
                waitFileTimeoutMsOverride,
                missingFilePollMinMsOverride,
                missingFilePollMaxMsOverride);
        }

        private async Task StopLogPumpsAsync(CancellationToken token)
        {
            if (logPumps.Count == 0)
            {
                return;
            }

            ActiveLogPump[] pumps = logPumps.ToArray();
            logPumps.Clear();

            for (int i = 0; i < pumps.Length; i++)
            {
                try
                {
                    await pumps[i].Pump.StopAsync(token: token).ConfigureAwait(false);
                }
                catch
                {
                    // best effort only
                }
                finally
                {
                    try { pumps[i].Pump.Dispose(); } catch { }
                }
            }
        }

        private void OnLogLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            EmitDebugLog(message);
        }

        private static void SafeInvokeProgress(Action<string> callback, string message)
        {
            try
            {
                callback?.Invoke(message);
            }
            catch
            {
                // ignore callback failures
            }
        }

        private static string NormalizePathOrEmpty(string path)
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

        private void ThrowIfSessionChanged(int expectedSessionVersion)
        {
            if (expectedSessionVersion != Volatile.Read(ref sessionVersion))
            {
                throw new OperationCanceledException("Bridge was stopped while this generation request was waiting.");
            }
        }

        private void ThrowIfStopRequested()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(KimodoBridgeService));
            }
            if (Volatile.Read(ref stopRequested) != 0)
            {
                throw new OperationCanceledException("Bridge is stopping.");
            }
        }

        private static async Task DetachOwnedConnectionsAsync()
        {
            KimodoBridgeService[] snapshot;
            lock (RegistryLock)
            {
                snapshot = new List<KimodoBridgeService>(Registry).FindAll(item => !item.isDefaultSession).ToArray();
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    await snapshot[i].protocolClient.DetachAsync().ConfigureAwait(false);
                    snapshot[i].ResetConnectionState();
                }
                catch
                {
                    // The server is already closing; disconnect cleanup is sufficient.
                }
            }
        }

        public async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            {
                return;
            }
            try
            {
                if (!isDefaultSession)
                {
                    await StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                await protocolClient.DisposeAsync().ConfigureAwait(false);
                lifecycleGate.Dispose();
                lock (RegistryLock)
                {
                    Registry.Remove(this);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            {
                return;
            }
            protocolClient.Dispose();
            lock (RegistryLock)
            {
                Registry.Remove(this);
            }
        }

        private static IBridgePlatformProcess CreatePlatformProcess()
        {
            RuntimePlatform platform = Application.platform;
            if (platform == RuntimePlatform.WindowsEditor || platform == RuntimePlatform.WindowsPlayer)
            {
                return new WindowsBridgePlatformProcess();
            }

            if (platform == RuntimePlatform.OSXEditor || platform == RuntimePlatform.OSXPlayer)
            {
                return new MacBridgePlatformProcess();
            }

            if (platform == RuntimePlatform.LinuxEditor || platform == RuntimePlatform.LinuxPlayer)
            {
                return new LinuxBridgePlatformProcess();
            }

            throw new PlatformNotSupportedException($"Unsupported bridge platform: {platform}");
        }

        private static ResolvedRuntimeContext ResolveRuntimeContext()
        {
            string runtimeRoot;
            bool? enableKimodoStaticGraph = null;
#if UNITY_EDITOR
            runtimeRoot = ResolveEditorRuntimeRootOrThrow();
            enableKimodoStaticGraph = ResolveEditorKimodoStaticGraphEnabled();
#else
            runtimeRoot = KimodoRuntimeBootstrapUtility.EnsureRuntimeRootForCurrentMode(
                Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "NvlabKimodoQuickServer~")));
            if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
            {
                throw new DirectoryNotFoundException($"Bridge runtime root not found: {runtimeRoot}");
            }
#endif

            string launcherPath = BridgeLauncherResolver.ResolveStartScript(runtimeRoot);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
            {
                throw new FileNotFoundException(
                    $"Bridge launcher not found under runtime root: {runtimeRoot}. Expected run_server.bat or run_server.sh.");
            }

            return new ResolvedRuntimeContext
            {
                RuntimeRoot = Path.GetFullPath(runtimeRoot),
                LauncherPath = Path.GetFullPath(launcherPath),
                EnableKimodoStaticGraph = enableKimodoStaticGraph
            };
        }

#if UNITY_EDITOR
        private static Type ResolveEditorRuntimeFacadeTypeOrThrow()
        {
            const string typeName = "KimodoBridge.Editor.KimodoBridgeRuntimeInstallFacade";
            Type facadeType = Type.GetType($"{typeName}, KimodoTool.Editor");
            if (facadeType == null)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    facadeType = assemblies[i].GetType(typeName, throwOnError: false);
                    if (facadeType != null)
                    {
                        break;
                    }
                }
            }

            return facadeType ??
                throw new TypeLoadException($"Cannot resolve editor runtime facade '{typeName}'.");
        }

        private static string ResolveEditorRuntimeRootOrThrow()
        {
            const string typeName = "KimodoBridge.Editor.KimodoBridgeRuntimeInstallFacade";
            const string methodName = "ResolveRuntimeRootOrThrow";

            Type facadeType = ResolveEditorRuntimeFacadeTypeOrThrow();
            MethodInfo resolveMethod = facadeType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (resolveMethod == null)
            {
                throw new MissingMethodException(typeName, methodName);
            }

            object result = resolveMethod.Invoke(null, null);
            if (result is string runtimeRoot && !string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return Path.GetFullPath(runtimeRoot);
            }

            throw new InvalidOperationException("Editor runtime root resolve returned an empty path.");
        }

        private static bool ResolveEditorKimodoStaticGraphEnabled()
        {
            const string typeName = "KimodoBridge.Editor.KimodoBridgeRuntimeInstallFacade";
            const string methodName = "ResolveKimodoStaticGraphEnabled";

            Type facadeType = ResolveEditorRuntimeFacadeTypeOrThrow();
            MethodInfo resolveMethod = facadeType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (resolveMethod == null)
            {
                throw new MissingMethodException(typeName, methodName);
            }

            return resolveMethod.Invoke(null, null) is bool enabled && enabled;
        }
#endif
    }
}
