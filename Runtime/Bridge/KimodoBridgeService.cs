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
    }

    public sealed class KimodoBridgeService
    {
        private sealed class ProgressSink
        {
            public Action<string> Callback;
            public SynchronizationContext Context;
        }

        private sealed class ProgressSubscription : IDisposable
        {
            private readonly KimodoBridgeService owner;
            private readonly ProgressSink sink;
            private bool disposed;

            public ProgressSubscription(KimodoBridgeService owner, ProgressSink sink)
            {
                this.owner = owner;
                this.sink = sink;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                owner.RemoveProgressSink(sink);
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new NoopDisposable();

            public void Dispose()
            {
            }
        }

        private sealed class TrackedBridgeTask
        {
            public string TaskId = string.Empty;
            public DateTime CreatedAtUtc;
        }

        private sealed class ActiveLogPump
        {
            public string Path = string.Empty;
            public BridgeLogPump Pump;
        }

        private sealed class ResolvedRuntimeContext
        {
            public string RuntimeRoot = string.Empty;
            public string LauncherPath = string.Empty;
            public bool ForceSetup;
        }

        private static readonly Lazy<KimodoBridgeService> SharedInstance =
            new Lazy<KimodoBridgeService>(() => new KimodoBridgeService(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly BridgeProtocolClient protocolClient;
        private readonly BridgeProcessManager processManager;
        private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly object taskGate = new object();
        private readonly object progressGate = new object();
        private readonly SynchronizationContext creationContext;
        private readonly Dictionary<string, TrackedBridgeTask> trackedTasks = new Dictionary<string, TrackedBridgeTask>(StringComparer.Ordinal);
        private readonly List<ProgressSink> progressSinks = new List<ProgressSink>(2);
        private readonly List<ActiveLogPump> logPumps = new List<ActiveLogPump>(4);

        private string currentHost = DefaultHost;
        private int currentPort = -1;
        private string currentRuntimeRoot = string.Empty;

        private const string DefaultHost = "127.0.0.1";

        private KimodoBridgeService()
        {
            protocolClient = new BridgeProtocolClient();
            processManager = new BridgeProcessManager(CreatePlatformProcess());
            creationContext = SynchronizationContext.Current;
        }

        public static KimodoBridgeService Shared => SharedInstance.Value;

        public bool IsConnected => protocolClient.IsConnected;

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
            bool forceSetup,
            CancellationToken token)
        {
            using var progressSubscription = SubscribeProgressSink(progress);
            await EnsureConnectedAsync(forceSetup, token).ConfigureAwait(false);
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

            using var progressSubscription = SubscribeProgressSink(progress);
            await EnsureConnectedAsync(forceSetup: false, token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(request.task_id))
            {
                request.task_id = Guid.NewGuid().ToString("N");
            }

            if (request.owner_pid <= 0)
            {
                request.owner_pid = Process.GetCurrentProcess().Id;
            }

            TrackTask(request.task_id);
            EmitDebugLog(
                $"[KimodoBridge] Generate request: host={currentHost}:{currentPort}, " +
                $"taskId='{request.task_id}', " +
                $"promptLen={(request.prompt ?? string.Empty).Length}, duration={request.duration:F3}, " +
                $"steps={request.steps}, seed={(request.seed.HasValue ? request.seed.Value.ToString() : "null")}, " +
                $"model='{request.model ?? string.Empty}', highvram={request.highvram}, force_cpu={request.force_cpu}, " +
                $"models_root='{request.models_root ?? string.Empty}'");

            Task<BridgeProtocolResponse> protocolTask = SendGenerateRequestAsync(request, CancellationToken.None);
            BridgeProtocolResponse response;
            try
            {
                response = await AwaitGenerateCompletionAsync(protocolTask, request.task_id, token).ConfigureAwait(false);
            }
            finally
            {
                UntrackTask(request.task_id);
            }

            JObject header = response?.Header;
            string status = header?.Value<string>("status") ?? string.Empty;
            string responseMessage = header?.Value<string>("message") ?? string.Empty;
            string outputFormat = header?.Value<string>("output_format") ?? string.Empty;
            string motionJson = header?.Value<string>("motion_json_compact");
            EmitDebugLog(
                $"[KimodoBridge] Generate response: status='{status}', format='{outputFormat}', hasJson={!string.IsNullOrWhiteSpace(motionJson)}, " +
                $"hasBinary={(response?.BinaryPayload != null && response.BinaryPayload.Length > 0)}, message='{responseMessage}'");

            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException(
                    string.IsNullOrWhiteSpace(responseMessage) ? "Bridge generation cancelled." : responseMessage);
            }

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

                PublishProgressMessage("Bridge generation complete.");
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

            PublishProgressMessage("Bridge generation complete.");
            return new KimodoBridgeGenerationResult
            {
                MotionJsonCompact = motionJson,
                MotionFormat = string.IsNullOrWhiteSpace(outputFormat) ? "json_compact" : outputFormat,
                RawStatus = status,
                Message = string.IsNullOrWhiteSpace(responseMessage) ? "Bridge generation complete." : responseMessage
            };
        }

        public async Task CancelAllAsync(CancellationToken token = default)
        {
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CancelTrackedTasksAsync(token).ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        public async Task StopAsync(CancellationToken token = default)
        {
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CancelTrackedTasksAsync(token).ConfigureAwait(false);

                if (TryResolveCurrentEndpoint(out string host, out int port))
                {
                    await protocolClient.TrySendQuitAsync(host, port, token).ConfigureAwait(false);
                }

                await protocolClient.DetachAsync().ConfigureAwait(false);
                await StopLogPumpsAsync(token).ConfigureAwait(false);
                processManager.DetachProcess();
                DeleteServerPortFile();
                ResetConnectionState();
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private async Task EnsureConnectedAsync(bool forceSetup, CancellationToken token)
        {
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsConnected && currentPort > 0)
                {
                    return;
                }

                ResolvedRuntimeContext context = ResolveRuntimeContext(forceSetup);
                currentRuntimeRoot = context.RuntimeRoot;

                if (TryReadRuntimeEndpoint(context.RuntimeRoot, out string host, out int port))
                {
                    try
                    {
                        await protocolClient.ConnectAsync(host, port, token).ConfigureAwait(false);
                        currentHost = host;
                        currentPort = port;
                        StartLogPumpsIfNeeded();
                        PublishProgressMessage($"Bridge attached to {host}:{port}.");
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
                        forceSetup: context.ForceSetup,
                        ownerProcessId: Process.GetCurrentProcess().Id);
                    PublishProgressMessage("Bridge process launched.");
                }
                else
                {
                    PublishProgressMessage("Bridge process already exists. Waiting for QuickServer...");
                }

                StartLogPumpsIfNeeded();
                PublishProgressMessage("Waiting for QuickServer...");

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
                PublishProgressMessage($"Bridge attached to {host}:{port}.");
            }
            finally
            {
                lifecycleGate.Release();
            }
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
            CancellationToken token)
        {
            return protocolClient.GenerateAsync(
                currentHost,
                currentPort,
                request,
                PublishProgressMessage,
                token);
        }

        private async Task CancelTrackedTasksAsync(CancellationToken token)
        {
            string[] trackedTaskIds;
            lock (taskGate)
            {
                trackedTaskIds = new string[trackedTasks.Count];
                trackedTasks.Keys.CopyTo(trackedTaskIds, 0);
            }

            for (int i = 0; i < trackedTaskIds.Length; i++)
            {
                await CancelTaskAsync(trackedTaskIds[i], token).ConfigureAwait(false);
            }
        }

        private async Task CancelTaskAsync(string taskId, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return;
            }

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
            ClearTrackedTasks();
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

        private void PublishProgressMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            ProgressSink[] sinks;
            lock (progressGate)
            {
                if (progressSinks.Count == 0)
                {
                    return;
                }

                sinks = progressSinks.ToArray();
            }

            for (int i = 0; i < sinks.Length; i++)
            {
                ProgressSink sink = sinks[i];
                if (sink == null || sink.Callback == null)
                {
                    continue;
                }

                if (sink.Context != null)
                {
                    sink.Context.Post(_ => SafeInvokeProgress(sink.Callback, message), null);
                    continue;
                }

                SafeInvokeProgress(sink.Callback, message);
            }
        }

        private IDisposable SubscribeProgressSink(Action<string> progress)
        {
            if (progress == null)
            {
                return NoopDisposable.Instance;
            }

            var sink = new ProgressSink
            {
                Callback = progress,
                Context = SynchronizationContext.Current
            };
            lock (progressGate)
            {
                progressSinks.Add(sink);
            }

            return new ProgressSubscription(this, sink);
        }

        private void RemoveProgressSink(ProgressSink sink)
        {
            if (sink == null)
            {
                return;
            }

            lock (progressGate)
            {
                progressSinks.Remove(sink);
            }
        }

        private void StartLogPumpsIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(currentRuntimeRoot))
            {
                return;
            }

            lock (progressGate)
            {
                if (logPumps.Count > 0)
                {
                    return;
                }

                StartLogPumpForPath(BridgeEndpointResolver.ResolveAttachLogPath(currentRuntimeRoot), "[Bridge]");
                StartLogPumpForPath(Path.Combine(currentRuntimeRoot, "log", "bridge_server.log"), "[BridgeServer]");
                StartLogPumpForPath(
                    Path.Combine(currentRuntimeRoot, "log", "bridge_message.log"),
                    "[BridgeMessage]",
                    BridgeRuntimeDefaults.LogPumpWaitFileTimeoutMs * 3,
                    BridgeRuntimeDefaults.LogPumpMissingFilePollMinMs,
                    BridgeRuntimeDefaults.LogPumpMissingFilePollMinMs);
                StartLogPumpForPath(Path.Combine(currentRuntimeRoot, "log", "run_server.log"), "[RunServer]");
                StartLogPumpForPath(Path.Combine(currentRuntimeRoot, "log", "setup.log"), "[Setup]");
            }
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
            ActiveLogPump[] pumps;
            lock (progressGate)
            {
                if (logPumps.Count == 0)
                {
                    return;
                }

                pumps = logPumps.ToArray();
                logPumps.Clear();
            }

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
            PublishProgressMessage(message);
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

        private void TrackTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return;
            }

            lock (taskGate)
            {
                trackedTasks[taskId] = new TrackedBridgeTask
                {
                    TaskId = taskId,
                    CreatedAtUtc = DateTime.UtcNow
                };
            }
        }

        private void UntrackTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return;
            }

            lock (taskGate)
            {
                trackedTasks.Remove(taskId);
            }
        }

        private void ClearTrackedTasks()
        {
            lock (taskGate)
            {
                trackedTasks.Clear();
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

        private static ResolvedRuntimeContext ResolveRuntimeContext(bool forceSetup)
        {
            string runtimeRoot;
#if UNITY_EDITOR
            runtimeRoot = ResolveEditorRuntimeRootOrThrow();
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
                ForceSetup = forceSetup
            };
        }

#if UNITY_EDITOR
        private static string ResolveEditorRuntimeRootOrThrow()
        {
            const string typeName = "KimodoBridge.Editor.KimodoBridgeRuntimeInstallFacade";
            const string methodName = "ResolveRuntimeRootOrThrow";

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

            if (facadeType == null)
            {
                throw new TypeLoadException($"Cannot resolve editor runtime facade '{typeName}'.");
            }

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
#endif
    }
}
