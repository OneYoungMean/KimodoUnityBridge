using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoServerManagerSettingsProvider : SettingsProvider
    {
        private sealed class InstalledModelInfoView
        {
            public string Name;
            public string DirectoryPath;
        }

        private enum ServerState
        {
            Disabled = 0,
            Enabled = 1
        }

        private const float ModelListMinHeight = 120f;
        private const float ModelListMaxHeight = 260f;
        private const float ModelListRowHeight = 22f;
        private const float ModelDeleteButtonWidth = 70f;
        private const float ClearClipCacheButtonWidth = 160f;

        private string runtimeRoot = string.Empty;
        private string resolvedModelsRoot = string.Empty;
        private Vector2 scroll;
        private List<InstalledModelInfoView> models = new List<InstalledModelInfoView>();
        private bool runtimeExists;
        private bool usingCustomModelsPath;
        private string setupProfile = "unknown";

        private ServerState serverState = ServerState.Disabled;
        private double detectHintUntilTime;
        private string serverHost = "127.0.0.1";
        private int serverPort = -1;

        private bool operationInProgress;
        private string operationStatus = string.Empty;
        private string lastError = string.Empty;
        private string modelError = string.Empty;
        private PendingServerOperation pendingOperation = PendingServerOperation.None;
        private bool managerSubscribed;

        private string selectedModel = "Kimodo-SOMA-RP-v1";
        private KimodoBridgeVramMode selectedVramMode = KimodoBridgeVramMode.Low;

        private enum PendingServerOperation
        {
            None = 0,
            Start = 1,
            Stop = 2,
            TryFix = 3,
            DeleteAllData = 4
        }

        private KimodoServerManagerSettingsProvider(string path, SettingsScope scope) : base(path, scope) { }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new KimodoServerManagerSettingsProvider("Project/Kimodo Server Manager", SettingsScope.Project)
            {
                keywords = new HashSet<string>(new[] { "Kimodo", "Server", "Model", "Bridge", "VRAM", "Cache", "Runtime" })
            };
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            SubscribeManagerEvents();
            Refresh();
            detectHintUntilTime = EditorApplication.timeSinceStartup + 2.0;
        }

        public override void OnDeactivate()
        {
            UnsubscribeManagerEvents();
            base.OnDeactivate();
        }

        public override void OnGUI(string searchContext)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            }

            PullServerStatusFromController(forceRefresh: false);
            TryRunPendingOperationAfterCompile();

            EditorGUILayout.LabelField("Kimodo Server Manager", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Runtime Root", runtimeRoot, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Refresh", "Rescan runtime/model folders and request latest bridge server status."), GUILayout.Width(100f)))
            {
                Refresh();
                DispatchBridgeCommand(KimodoBridgeOperation.RefreshStatus);
            }

            if (!runtimeExists)
            {
                if (GUILayout.Button(new GUIContent("Create Kimodo Server", "Bootstrap Kimodo runtime folder and required server files."), GUILayout.Width(180f)))
                {
                    DispatchBridgeCommand(KimodoBridgeOperation.EnsureRuntimeRoot);
                }
            }
            EditorGUILayout.EndHorizontal();

            DrawStatusMessages();

            if (!runtimeExists)
            {
                EditorGUILayout.HelpBox("Directory does not exist. Click 'Create Kimodo Server' to bootstrap runtime.", MessageType.Warning);
                return;
            }

            DrawStartupSection();
            DrawServerSection();
            DrawModelSection();
            DrawActionsSection();
        }

        private void DrawStatusMessages()
        {
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }

            if (!string.IsNullOrWhiteSpace(operationStatus))
            {
                EditorGUILayout.HelpBox(operationStatus, MessageType.Info);
            }
        }

        private void DrawStartupSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Startup", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            string[] options = KimodoBridgeController.SupportedModelNames;
            int idx = Array.IndexOf(options, selectedModel);
            if (idx < 0)
            {
                idx = 0;
            }

            int newIdx = EditorGUILayout.Popup(new GUIContent("Model", "Default model used when starting server from this settings page."), idx, options);
            selectedModel = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
            selectedVramMode = (KimodoBridgeVramMode)EditorGUILayout.EnumPopup(
                new GUIContent("VRAM Mode", "Low: quantized encoder (~4G). High: full model stack (~16G)."),
                selectedVramMode);

            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                int newLimit = EditorGUILayout.IntSlider(
                    new GUIContent("Max Cached Clip", "Maximum number of generated clips kept in runtime cache. Range: 1-1000."),
                    settings.MaxGeneratedClips,
                    KimodoPlayableClipGenerationSettings.MinGeneratedClipsLimit,
                    KimodoPlayableClipGenerationSettings.MaxGeneratedClipsLimit);
                if (EditorGUI.EndChangeCheck())
                {
                    settings.MaxGeneratedClips = newLimit;
                    settings.SaveSettings();
                }

                using (new EditorGUI.DisabledScope(operationInProgress))
                {
                    if (GUILayout.Button(
                        new GUIContent("Clear Clip Cache", "Delete unreferenced Kimodo cache clips under Assets/KimodoGeneratedClips."),
                        GUILayout.Width(ClearClipCacheButtonWidth)))
                    {
                        TryClearUnreferencedClipCaches();
                    }
                }
            }

            EditorGUI.BeginChangeCheck();
            float timeoutSeconds = EditorGUILayout.FloatField(
                new GUIContent("Generate Timeout (sec)", "Global timeout used by Kimodo generation requests."),
                settings.GenerationTimeoutSeconds);
            if (EditorGUI.EndChangeCheck())
            {
                settings.GenerationTimeoutSeconds = timeoutSeconds;
                settings.SaveSettings();
            }

            EditorGUI.BeginChangeCheck();
            bool floatingUiEnabled = EditorGUILayout.Toggle(
                new GUIContent("Enable Floating UI", "Show the Kimodo floating prompt UI overlay on Timeline and Animator windows."),
                settings.FloatingUiEnabled);
            if (EditorGUI.EndChangeCheck())
            {
                settings.FloatingUiEnabled = floatingUiEnabled;
                settings.SaveSettings();
            }

            EditorGUI.BeginChangeCheck();
            bool alwaysKeepServerExperimental = EditorGUILayout.Toggle(
                new GUIContent(
                    "Always Keep Server (Experimental)",
                    "Keep the bridge server alive through compile, assembly reload, and Play Mode transitions. This may leak memory or keep stale runtime state alive."),
                settings.AlwaysKeepServerExperimental);
            if (EditorGUI.EndChangeCheck())
            {
                settings.AlwaysKeepServerExperimental = alwaysKeepServerExperimental;
                settings.SaveSettings();
            }

            EditorGUI.BeginChangeCheck();
            bool keepCpuForceExperimental = EditorGUILayout.Toggle(
                new GUIContent(
                    "Keep CPU Force (Experimental)",
                    "Force bridge startup to use CPU mode for testing. This is only intended for validating the CPU startup path."),
                settings.KeepCpuForceExperimental);
            if (EditorGUI.EndChangeCheck())
            {
                settings.KeepCpuForceExperimental = keepCpuForceExperimental;
                settings.SaveSettings();
            }

            EditorGUI.BeginChangeCheck();
            int idleShutdownMinutes = EditorGUILayout.IntField(
                new GUIContent(
                    "Idle Shutdown (min)",
                    "Auto-close bridge server after this many idle minutes. 0 disables idle shutdown. This value is passed to the bridge runtime as KIMODO_IDLE_TIMEOUT_SEC."),
                settings.ServerIdleShutdownMinutes);
            if (EditorGUI.EndChangeCheck())
            {
                settings.ServerIdleShutdownMinutes = idleShutdownMinutes;
                settings.SaveSettings();
            }

            if (settings.AlwaysKeepServerExperimental)
            {
                EditorGUILayout.HelpBox(
                    "Experimental: the bridge server will be kept alive during compile and Play Mode transitions. This may cause memory leaks or stale runtime state. Idle shutdown still depends on bridge runtime behavior while the server is otherwise idle.",
                    MessageType.Warning);
            }
            else if (settings.ServerIdleShutdownMinutes > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Bridge server will auto-close after {settings.ServerIdleShutdownMinutes} idle minute(s).",
                    MessageType.None);
            }

            string localModelsPath = settings.LocalModelsPath;
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            localModelsPath = EditorGUILayout.DelayedTextField(
                new GUIContent("Local Models Path", "Optional override for model detection list source. Does not move runtime root."),
                localModelsPath);
            bool textChanged = EditorGUI.EndChangeCheck();

            if (GUILayout.Button(new GUIContent("Browse...", "Pick local models folder path for detection list."), GUILayout.Width(90f)))
            {
                string startDir = string.IsNullOrWhiteSpace(localModelsPath)
                    ? runtimeRoot
                    : localModelsPath;
                string selected = EditorUtility.OpenFolderPanel("Select Local Models Folder", startDir, string.Empty);
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    localModelsPath = selected;
                    textChanged = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (textChanged)
            {
                settings.LocalModelsPath = localModelsPath;
                settings.SaveSettings();
                RefreshModelList();
            }

            int encoderVramGb = selectedVramMode == KimodoBridgeVramMode.High ? 16 : 4;
            int totalVramGb = 2 + encoderVramGb;
            EditorGUILayout.HelpBox(
                $"Estimated VRAM for selected mode: ~{totalVramGb} GB (core 2 GB + encoder {encoderVramGb} GB).",
                MessageType.Info);

            if (KimodoBridgeController.TryGetModelMissingSetupMinutes(
                runtimeRoot,
                selectedVramMode == KimodoBridgeVramMode.High,
                selectedModel,
                ResolveModelsRootForServer(),
                out int minutes))
            {
                EditorGUILayout.HelpBox($"Model missing detected, update required, approximately {minutes} minutes.", MessageType.None);
            }

            EditorGUILayout.LabelField("Setup Profile", setupProfile, EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawServerSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            bool showDetectHint = EditorApplication.timeSinceStartup < detectHintUntilTime;
            bool compileGate = EditorCompilationStateGate.IsCompilingOrReloading;
            if (compileGate)
            {
                EditorGUILayout.HelpBox("compiling...", MessageType.None);
            }
            else if (showDetectHint)
            {
                EditorGUILayout.HelpBox("detect...", MessageType.None);
            }
            else if (serverState == ServerState.Enabled)
            {
                EditorGUILayout.HelpBox($"Running at {serverHost}:{serverPort}", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Server is not running.", MessageType.None);
                ServerStatusSnapshot staleSnapshot = KimodoBridgeController.GetServerStatusSnapshot();
                if (staleSnapshot.HasPort)
                {
                    EditorGUILayout.HelpBox("Detected stale endpoint file (serverport). Process is not alive.", MessageType.None);
                }
            }
            if (compileGate)
            {
                EditorGUILayout.LabelField("Status", "detect/compiling", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("Status", serverState == ServerState.Enabled ? "enable" : "disable", EditorStyles.miniLabel);
            }

            bool inMaintenance = KimodoBridgeController.IsRuntimeMaintenanceInProgress;
            bool stopMode = serverState == ServerState.Enabled;
            string buttonLabel = (operationInProgress || inMaintenance) ? "Processing..." : (stopMode ? "Stop Server" : "Start Server");

            using (new EditorGUI.DisabledScope(operationInProgress || inMaintenance))
            {
                if (GUILayout.Button(new GUIContent(buttonLabel, "Start or stop Kimodo bridge server with current startup model and VRAM mode settings."), GUILayout.Width(140f)))
                {
                    if (stopMode)
                    {
                        EnqueueOrRun(PendingServerOperation.Stop, () => DispatchBridgeCommand(KimodoBridgeOperation.Stop));
                    }
                    else
                    {
                        EnqueueOrRun(PendingServerOperation.Start, () => DispatchBridgeCommand(KimodoBridgeOperation.Start));
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawModelSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Detected Models", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.LabelField("Source", string.IsNullOrWhiteSpace(resolvedModelsRoot) ? "<none>" : resolvedModelsRoot, EditorStyles.wordWrappedMiniLabel);
            if (usingCustomModelsPath)
            {
                EditorGUILayout.HelpBox("Custom models path is active. Delete is disabled.", MessageType.None);
            }

            if (models.Count == 0)
            {
                EditorGUILayout.LabelField("No model folder detected.");
            }
            else
            {
                float viewportHeight = Mathf.Clamp(models.Count * ModelListRowHeight, ModelListMinHeight, ModelListMaxHeight);
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(viewportHeight));

                int firstVisible = Mathf.Clamp(Mathf.FloorToInt(scroll.y / ModelListRowHeight), 0, models.Count - 1);
                int visibleCount = Mathf.CeilToInt(viewportHeight / ModelListRowHeight) + 2;
                int lastVisibleExclusive = Mathf.Min(models.Count, firstVisible + visibleCount);

                if (firstVisible > 0)
                {
                    GUILayout.Space(firstVisible * ModelListRowHeight);
                }

                for (int i = firstVisible; i < lastVisibleExclusive; i++)
                {
                    InstalledModelInfoView model = models[i];
                    Rect rowRect = EditorGUILayout.GetControlRect(false, ModelListRowHeight);
                    Rect nameRect = new Rect(
                        rowRect.x,
                        rowRect.y,
                        Mathf.Max(10f, rowRect.width - ModelDeleteButtonWidth - 8f),
                        rowRect.height);
                    Rect deleteRect = new Rect(
                        rowRect.xMax - ModelDeleteButtonWidth,
                        rowRect.y,
                        ModelDeleteButtonWidth,
                        rowRect.height - 1f);

                    EditorGUI.LabelField(nameRect, model.Name);

                    using (new EditorGUI.DisabledScope(usingCustomModelsPath || operationInProgress))
                    {
                        if (GUI.Button(deleteRect, new GUIContent("Delete", "Delete this detected model directory from disk.")))
                        {
                            TryDeleteModelDirectory(model.DirectoryPath, model.Name);
                            RefreshModelList();
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                int remaining = models.Count - lastVisibleExclusive;
                if (remaining > 0)
                {
                    GUILayout.Space(remaining * ModelListRowHeight);
                }

                EditorGUILayout.EndScrollView();
            }

            if (!string.IsNullOrWhiteSpace(modelError))
            {
                EditorGUILayout.HelpBox(modelError, MessageType.Error);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActionsSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUI.DisabledScope(operationInProgress))
            {
                if (GUILayout.Button(new GUIContent("Try Fix (delete and reconfigure)", "Run bridge self-repair flow: clean broken parts and reconfigure runtime."), GUILayout.Height(24f)))
                {
                    EnqueueOrRun(PendingServerOperation.TryFix, () => DispatchBridgeCommand(KimodoBridgeOperation.TryFix));
                }

                if (GUILayout.Button(new GUIContent("Delete All Data", "Delete the full Kimodo runtime folder including downloaded models and cache."), GUILayout.Height(24f)))
                {
                    if (EditorUtility.DisplayDialog("Delete All Data", "Delete entire Kimodo runtime folder? This cannot be undone.", "Delete", "Cancel"))
                    {
                        EnqueueOrRun(PendingServerOperation.DeleteAllData, () => DispatchBridgeCommand(KimodoBridgeOperation.DeleteAllData));
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private Task DispatchBridgeCommand(KimodoBridgeOperation operation)
        {
            if (operationInProgress && operation != KimodoBridgeOperation.RefreshStatus)
            {
                return Task.CompletedTask;
            }

            operationInProgress = operation != KimodoBridgeOperation.RefreshStatus;
            lastError = string.Empty;
            operationStatus = operation switch
            {
                KimodoBridgeOperation.Start => "Starting server...",
                KimodoBridgeOperation.Stop => "Stopping server...",
                KimodoBridgeOperation.TryFix => "TryFix running...",
                KimodoBridgeOperation.DeleteAllData => "Deleting runtime data...",
                KimodoBridgeOperation.EnsureRuntimeRoot => "Creating runtime root...",
                _ => "Refreshing..."
            };

            string modelsRoot = ResolveModelsRootForServer();
            bool accepted = KimodoEditorCommandManager.Dispatch(
                new BridgeControlCommand(
                    operation,
                    runtimeRoot,
                    selectedModel,
                    selectedVramMode,
                    modelsRoot));

            if (!accepted && operation != KimodoBridgeOperation.RefreshStatus)
            {
                operationInProgress = false;
                operationStatus = "Command rejected.";
            }

            return Task.CompletedTask;
        }

        private void TryDeleteModelDirectory(string path, string modelName)
        {
            modelError = string.Empty;
            if (usingCustomModelsPath)
            {
                modelError = "Delete is disabled for custom models path.";
                return;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    modelError = $"Model path not found: {modelName}";
                    return;
                }

                Directory.Delete(path, recursive: true);
                operationStatus = $"Model deleted: {modelName}";
            }
            catch (Exception e)
            {
                modelError = $"Delete model failed ({modelName}): {e.Message}";
            }
        }

        private void TryClearUnreferencedClipCaches()
        {
            const string title = "Clear Clip Cache";
            const string message =
                "This will scan Kimodo animation clip caches and delete cache clips that have no scene reference and no object/asset reference.\n\n" +
                "This operation may take a while on large projects. Continue?";

            if (!EditorUtility.DisplayDialog(title, message, "Clear", "Cancel"))
            {
                return;
            }

            lastError = string.Empty;
            operationStatus = "Scanning Kimodo clip cache references...";

            if (KimodoEditorClipWritebackService.TryDeleteUnreferencedNamedClipCaches(
                out KimodoEditorClipWritebackService.NamedClipCacheCleanupSummary summary,
                out string error))
            {
                operationStatus =
                    $"Clip cache cleanup complete. Scanned {summary.CandidateCount} cache clip(s), kept {summary.ReferencedCount}, deleted {summary.DeletedCount}.";
                if (summary.FailedCount > 0)
                {
                    lastError = $"Some cache clips could not be deleted ({summary.FailedCount}).";
                }
            }
            else
            {
                operationStatus = string.IsNullOrWhiteSpace(error)
                    ? "Clip cache cleanup finished with errors."
                    : error;
                lastError = string.IsNullOrWhiteSpace(error)
                    ? "Clear clip cache failed."
                    : error;
            }
        }

        private void Refresh()
        {
            runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            runtimeExists = Directory.Exists(runtimeRoot);
            setupProfile = "unknown";
            if (runtimeExists && KimodoServerRuntimeUtil.TryReadSetupProfile(runtimeRoot, out string profile))
            {
                setupProfile = string.IsNullOrWhiteSpace(profile) ? "unknown" : profile;
            }
            RefreshModelList();

            serverHost = "127.0.0.1";
            serverPort = -1;
            serverState = ServerState.Disabled;
        }

        private void RefreshModelList()
        {
            models = new List<InstalledModelInfoView>();
            modelError = string.Empty;

            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string customPath = settings.LocalModelsPath?.Trim() ?? string.Empty;
            usingCustomModelsPath = !string.IsNullOrWhiteSpace(customPath);

            if (usingCustomModelsPath)
            {
                resolvedModelsRoot = customPath;
                if (!Directory.Exists(resolvedModelsRoot))
                {
                    modelError = "Custom models path does not exist.";
                    return;
                }
            }
            else
            {
                resolvedModelsRoot = Path.Combine(runtimeRoot ?? string.Empty, "models");
                if (!Directory.Exists(resolvedModelsRoot))
                {
                    return;
                }
            }

            try
            {
                List<ModelDirectoryInfo> source =
                    KimodoBridgeController.QueryDisplayableModelDirectories(resolvedModelsRoot);
                for (int i = 0; i < source.Count; i++)
                {
                    ModelDirectoryInfo item = source[i];
                    models.Add(new InstalledModelInfoView
                    {
                        Name = item.Name,
                        DirectoryPath = item.DirectoryPath
                    });
                }
            }
            catch (Exception e)
            {
                modelError = "Scan models failed: " + e.Message;
            }
        }

        private string ResolveModelsRootForServer()
        {
            string customPath = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(customPath))
            {
                return string.Empty;
            }

            return Path.GetFullPath(customPath);
        }

        private void PullServerStatusFromController(bool forceRefresh)
        {
            if (!runtimeExists)
            {
                serverState = ServerState.Disabled;
                return;
            }

            _ = forceRefresh;
            ServerStatusSnapshot snapshot = KimodoBridgeController.GetServerStatusSnapshot();
            serverHost = snapshot.Host;
            serverPort = snapshot.Port;
            serverState = snapshot.Running ? ServerState.Enabled : ServerState.Disabled;
        }

        private void EnqueueOrRun(PendingServerOperation op, Func<Task> action)
        {
            if (EditorCompilationStateGate.IsCompilingOrReloading)
            {
                pendingOperation = op;
                operationStatus = $"Queued '{op}' until compile completes.";
                Debug.Log($"[Kimodo][CompileGate] queued operation: {op}");
                return;
            }

            _ = action();
        }

        private void TryRunPendingOperationAfterCompile()
        {
            if (pendingOperation == PendingServerOperation.None)
            {
                return;
            }

            if (EditorCompilationStateGate.IsCompilingOrReloading || operationInProgress)
            {
                return;
            }

            PendingServerOperation op = pendingOperation;
            pendingOperation = PendingServerOperation.None;
            Debug.Log($"[Kimodo][CompileGate] running queued operation: {op}");
            switch (op)
            {
                case PendingServerOperation.Start:
                    _ = DispatchBridgeCommand(KimodoBridgeOperation.Start);
                    break;
                case PendingServerOperation.Stop:
                    _ = DispatchBridgeCommand(KimodoBridgeOperation.Stop);
                    break;
                case PendingServerOperation.TryFix:
                    _ = DispatchBridgeCommand(KimodoBridgeOperation.TryFix);
                    break;
                case PendingServerOperation.DeleteAllData:
                    _ = DispatchBridgeCommand(KimodoBridgeOperation.DeleteAllData);
                    break;
            }
        }

        private void SubscribeManagerEvents()
        {
            if (managerSubscribed)
            {
                return;
            }

            KimodoEditorCommandManager.CommandProgress += OnManagerCommandProgress;
            KimodoEditorCommandManager.CommandCompleted += OnManagerCommandCompleted;
            KimodoEditorCommandManager.CommandFailed += OnManagerCommandFailed;
            KimodoEditorCommandManager.CommandCanceled += OnManagerCommandCanceled;
            managerSubscribed = true;
        }

        private void UnsubscribeManagerEvents()
        {
            if (!managerSubscribed)
            {
                return;
            }

            KimodoEditorCommandManager.CommandProgress -= OnManagerCommandProgress;
            KimodoEditorCommandManager.CommandCompleted -= OnManagerCommandCompleted;
            KimodoEditorCommandManager.CommandFailed -= OnManagerCommandFailed;
            KimodoEditorCommandManager.CommandCanceled -= OnManagerCommandCanceled;
            managerSubscribed = false;
        }

        private static bool IsBridgeCommand(IKimodoEditorCommand command)
        {
            if (command == null)
            {
                return false;
            }

            return command.Kind == KimodoEditorCommandKind.BridgeStartServer
                || command.Kind == KimodoEditorCommandKind.BridgeStopServer
                || command.Kind == KimodoEditorCommandKind.BridgeTryFix
                || command.Kind == KimodoEditorCommandKind.BridgeDeleteAllData
                || command.Kind == KimodoEditorCommandKind.BridgeRefreshStatus
                || command.Kind == KimodoEditorCommandKind.BridgeEnsureRuntimeRoot;
        }

        private void OnManagerCommandProgress(KimodoEditorCommandProgressEvent evt)
        {
            if (!IsBridgeCommand(evt.Command))
            {
                return;
            }

            operationStatus = evt.Message;
        }

        private void OnManagerCommandCompleted(KimodoEditorCommandCompletedEvent evt)
        {
            if (!IsBridgeCommand(evt.Command))
            {
                return;
            }

            operationInProgress = false;
            lastError = string.Empty;

            if (evt.Payload is KimodoEditorBridgeOperationResult result)
            {
                serverHost = result.Host;
                serverPort = result.Port;
                serverState = result.Running ? ServerState.Enabled : ServerState.Disabled;
                operationStatus = string.IsNullOrWhiteSpace(result.Status) ? "Done." : result.Status;
            }
            else
            {
                operationStatus = "Done.";
            }

            Refresh();
            PullServerStatusFromController(forceRefresh: true);
        }

        private void OnManagerCommandFailed(KimodoEditorCommandFailedEvent evt)
        {
            if (!IsBridgeCommand(evt.Command))
            {
                return;
            }

            operationInProgress = false;
            lastError = evt.Message;
            operationStatus = "Failed.";
            PullServerStatusFromController(forceRefresh: true);
        }

        private void OnManagerCommandCanceled(KimodoEditorCommandCanceledEvent evt)
        {
            if (!IsBridgeCommand(evt.Command))
            {
                return;
            }

            operationInProgress = false;
            operationStatus = "Canceled.";
        }
    }
}


