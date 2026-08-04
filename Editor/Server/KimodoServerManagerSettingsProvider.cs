using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
        private bool advancedSettingsFoldout;
        private string setupProfile = "unknown";

        private ServerState serverState = ServerState.Disabled;

        private bool operationInProgress;
        private string operationStatus = string.Empty;
        private string lastError = string.Empty;
        private string modelError = string.Empty;

        private KimodoServerManagerSettingsProvider(string path, SettingsScope scope) : base(path, scope) { }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new KimodoServerManagerSettingsProvider("Project/Kimodo Server Manager", SettingsScope.Project)
            {
                keywords = new HashSet<string>(new[] { "Kimodo", "Server", "Model", "Bridge", "VRAM", "Cache", "Runtime", "Debug", "Resample", "Writeback" })
            };
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            Refresh();
        }

        public override void OnDeactivate()
        {
            base.OnDeactivate();
        }

        public override void OnGUI(string searchContext)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
            }

            PullServerStatusFromController();

            EditorGUILayout.LabelField("Kimodo Server Manager", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            DrawQuickServerDirectory();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Refresh", "Rescan runtime/model folders and request latest bridge server status."), GUILayout.Width(100f)))
            {
                Refresh();
                PullServerStatusFromController();
            }

            if (GUILayout.Button(
                new GUIContent(
                    runtimeExists ? "Reinstall Kimodo Server" : "Create Kimodo Server",
                    runtimeExists
                        ? "Delete the current Kimodo runtime folder and reinstall it from the packaged template."
                        : "Bootstrap Kimodo runtime folder and required server files."),
                GUILayout.Width(180f)))
            {
                _ = EnsureRuntimeRootAsync();
            }
            EditorGUILayout.EndHorizontal();

            DrawStatusMessages();

            if (!runtimeExists)
            {
                EditorGUILayout.HelpBox("Directory does not exist. Click 'Create Kimodo Server' to bootstrap runtime.", MessageType.Warning);
                return;
            }

            DrawDefaultParametersSection();
            DrawServerSection();
            DrawModelSection();
            DrawActionsSection();
        }

        private void DrawQuickServerDirectory()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string path = runtimeRoot;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            path = EditorGUILayout.DelayedTextField(
                new GUIContent("QuickServer Directory", "Directory containing run_server and package.json."),
                path);
            bool changed = EditorGUI.EndChangeCheck();
            if (GUILayout.Button("Browse...", GUILayout.Width(90f)))
            {
                string selected = EditorUtility.OpenFolderPanel("Select QuickServer Directory", path, string.Empty);
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    path = selected;
                    changed = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (changed)
            {
                settings.QuickServerPath = path;
                settings.SaveSettings();
                GUI.FocusControl(null);
                Refresh();
            }

            EditorGUILayout.LabelField("Server Version", KimodoServerRuntimeUtil.ReadQuickServerVersion(runtimeRoot));
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

        private void DrawDefaultParametersSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Default Parameters", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string[] options = KimodoBridgeServerTool.SupportedModelNames;
            string selectedModel = settings.DefaultBridgeModelName;
            KimodoTextEncoderMode selectedEncoderMode = settings.DefaultTextEncoderMode;
            int idx = Array.IndexOf(options, selectedModel);
            if (idx < 0)
            {
                idx = 0;
            }

            EditorGUI.BeginChangeCheck();
            int newIdx = EditorGUILayout.Popup(new GUIContent("Default Model", "Default Kimodo model used by editor flows that do not explicitly override the model."), idx, options);
            KimodoTextEncoderMode newEncoderMode = (KimodoTextEncoderMode)EditorGUILayout.EnumPopup(
                new GUIContent("Default Text Encoder Mode", "Default precision/performance preference. Device placement is automatic."),
                selectedEncoderMode);
            bool modelChanged = newIdx != idx;
            if (EditorGUI.EndChangeCheck())
            {
                settings.DefaultBridgeModelName = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
                if (modelChanged)
                {
                    newEncoderMode = KimodoGenerationInspectorGui.IsArdy(settings.DefaultBridgeModelName)
                        ? KimodoTextEncoderMode.HighPrecision
                        : KimodoTextEncoderMode.HighPerformance;
                }
                settings.DefaultTextEncoderMode = newEncoderMode;
                settings.SaveSettings();
                selectedModel = settings.DefaultBridgeModelName;
                selectedEncoderMode = settings.DefaultTextEncoderMode;
            }

            KimodoGenerationInspectorGui.DrawArdyTextEncoderWarning(
                KimodoGenerationInspectorGui.IsArdy(selectedModel),
                selectedEncoderMode);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                int newLimit = EditorGUILayout.IntSlider(
                    new GUIContent("Max Cached Clip", "Maximum number of cache clip assets kept under Assets/KimodoGeneratedClips/Cache. Range: 1-1000."),
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
                        new GUIContent("Clear Clip Cache", "Delete unreferenced Kimodo cache/raw clips under Assets/KimodoGeneratedClips/Cache."),
                        GUILayout.Width(ClearClipCacheButtonWidth)))
                    {
                        TryClearUnreferencedClipCaches();
                    }
                }
            }

            EditorGUI.BeginChangeCheck();
            int timelineCacheTimeFrames = EditorGUILayout.IntSlider(
                new GUIContent(
                    "Timeline Constraint Cache Time",
                    "Number of 30 FPS Timeline frames in each fixed constraint-sampling cache interval."),
                settings.TimelineConstraintCacheTimeFrames,
                KimodoPlayableClipGenerationSettings.MinTimelineConstraintCacheTimeFrames,
                KimodoPlayableClipGenerationSettings.MaxTimelineConstraintCacheTimeFrames);
            if (EditorGUI.EndChangeCheck())
            {
                settings.TimelineConstraintCacheTimeFrames = timelineCacheTimeFrames;
                settings.SaveSettings();
            }

            advancedSettingsFoldout = EditorGUILayout.Foldout(
                advancedSettingsFoldout,
                new GUIContent("Advanced"),
                true);
            if (advancedSettingsFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                bool enableDebugLog = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Enable Debug Log",
                        "Show diagnostic logs outside the main generation path, such as retarget, sampling, constraint export, and Timeline offset details."),
                    settings.EnableDebugLog);
                if (EditorGUI.EndChangeCheck())
                {
                    settings.EnableDebugLog = enableDebugLog;
                    settings.SaveSettings();
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
                bool keepCpuForceExperimental = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Force CPU",
                        "Send simulate_free_vram_gb=0 so Kimodo and the text encoder both run on CPU."),
                    settings.KeepCpuForceExperimental);
                if (EditorGUI.EndChangeCheck())
                {
                    settings.KeepCpuForceExperimental = keepCpuForceExperimental;
                    settings.SaveSettings();
                }

                EditorGUI.BeginChangeCheck();
                bool writeResampledTimelineCacheClips = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Write Resampled Clips",
                        "Persist RawBone, Muscle and target Bone intermediates plus Timeline resample diagnostics under Assets/KimodoGeneratedClips/Cache. Off keeps intermediates transient."),
                    settings.WriteResampledTimelineCacheClips);
                if (EditorGUI.EndChangeCheck())
                {
                    settings.WriteResampledTimelineCacheClips = writeResampledTimelineCacheClips;
                    settings.SaveSettings();
                }

                EditorGUILayout.HelpBox(
                    "These are advanced generation parameters. QuickServer switches runtime behavior per request; starting the server does not depend on these values.",
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }

            string localModelsPath = ResolveDisplayedModelsPath(settings.LocalModelsPath, runtimeRoot);
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
                settings.LocalModelsPath = string.IsNullOrWhiteSpace(localModelsPath)
                    ? string.Empty
                    : localModelsPath;
                settings.SaveSettings();
                GUI.FocusControl(null);
                RefreshModelList();
            }

            EditorGUILayout.HelpBox(
                selectedEncoderMode == KimodoTextEncoderMode.HighPrecision
                    ? "High Precision: FP16 uses the accelerator at 18 GB effective VRAM; otherwise the encoder runs on CPU."
                    : "High Performance: NF4 uses the accelerator at 6 GB when supported; otherwise INT8 uses it at 8 GB or runs on CPU.",
                MessageType.Info);

            if (KimodoBridgeServerTool.TryGetModelMissingSetupMinutes(
                runtimeRoot,
                selectedEncoderMode,
                selectedModel,
                ResolveModelsRootForServer(),
                out int minutes))
            {
                EditorGUILayout.HelpBox($"Model missing detected, update required, approximately {minutes} minutes.", MessageType.None);
            }

            EditorGUILayout.LabelField("Setup Profile", setupProfile, EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        internal static string ResolveDisplayedModelsPath(string configuredPath, string runtimeRootPath)
        {
            return string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(runtimeRootPath ?? string.Empty, "models")
                : configuredPath;
        }

        private void DrawServerSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            bool compileGate = EditorCompilationStateGate.IsCompilingOrReloading;
            if (compileGate)
            {
                EditorGUILayout.HelpBox("compiling...", MessageType.None);
            }
            else if (serverState == ServerState.Enabled)
            {
                EditorGUILayout.HelpBox("Server is connected.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Server is not running.", MessageType.None);
            }
            if (compileGate)
            {
                EditorGUILayout.LabelField("Status", "detect/compiling", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("Status", serverState == ServerState.Enabled ? "connected" : "disconnected", EditorStyles.miniLabel);
            }

            bool inMaintenance = KimodoBridgeServerTool.IsRuntimeMaintenanceInProgress;
            bool stopMode = serverState == ServerState.Enabled;
            string buttonLabel = (operationInProgress || inMaintenance) ? "Processing..." : (stopMode ? "Stop Server" : "Start Server");

            using (new EditorGUI.DisabledScope(operationInProgress || inMaintenance || compileGate))
            {
                if (GUILayout.Button(new GUIContent(buttonLabel, "Start or stop the shared Kimodo bridge service. Runtime switching remains request-driven on the QuickServer side."), GUILayout.Width(140f)))
                {
                    if (stopMode)
                    {
                        _ = StopServerAsync();
                    }
                    else
                    {
                        _ = StartServerAsync();
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

            using (new EditorGUI.DisabledScope(operationInProgress || EditorCompilationStateGate.IsCompilingOrReloading))
            {
                if (GUILayout.Button(new GUIContent("Open Folder", "Open the Kimodo runtime root folder in Explorer/Finder."), GUILayout.Height(24f)))
                {
                    TryOpenRuntimeRootFolder();
                }

                Color previousColor = GUI.color;
                GUI.color = new Color(0.9f, 0.3f, 0.3f, 1f);
                bool deleteAllDataClicked = GUILayout.Button(new GUIContent("Delete All Data", "Delete the full Kimodo runtime folder including downloaded models and cache."), GUILayout.Height(24f));
                GUI.color = previousColor;
                if (deleteAllDataClicked)
                {
                    if (EditorUtility.DisplayDialog("Delete All Data", "Delete entire Kimodo runtime folder? This cannot be undone.", "Delete", "Cancel"))
                    {
                        _ = DeleteAllDataAsync();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void TryOpenRuntimeRootFolder()
        {
            lastError = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(runtimeRoot))
                {
                    lastError = "Runtime root is empty.";
                    return;
                }

                if (!Directory.Exists(runtimeRoot))
                {
                    lastError = $"Runtime root not found: {runtimeRoot}";
                    return;
                }

                EditorUtility.RevealInFinder(runtimeRoot);
            }
            catch (Exception e)
            {
                lastError = $"Open runtime root failed: {e.Message}";
            }
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
                "This will scan Kimodo animation cache/raw clips under Assets/KimodoGeneratedClips/Cache and delete clips that have no scene reference and no object/asset reference.\n\n" +
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
                    $"Clip cache cleanup complete. Scanned {summary.CandidateCount} cache/raw clip(s), kept {summary.ReferencedCount}, deleted {summary.DeletedCount}.";
                if (summary.FailedCount > 0)
                {
                    lastError = $"Some cache/raw clips could not be deleted ({summary.FailedCount}).";
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
            runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
            runtimeExists = Directory.Exists(runtimeRoot);
            setupProfile = "unknown";
            if (runtimeExists && KimodoServerRuntimeUtil.TryReadSetupProfile(runtimeRoot, out string profile))
            {
                setupProfile = string.IsNullOrWhiteSpace(profile) ? "unknown" : profile;
            }
            RefreshModelList();

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
                    KimodoBridgeServerTool.QueryDisplayableModelDirectories(resolvedModelsRoot);
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

        private void PullServerStatusFromController()
        {
            if (!runtimeExists)
            {
                serverState = ServerState.Disabled;
                return;
            }

            serverState = KimodoBridgeService.Shared.IsConnected ? ServerState.Enabled : ServerState.Disabled;
        }

        private Task EnsureRuntimeRootAsync()
        {
            return RunOperationAsync(
                initialStatus: runtimeExists ? "Reinstalling runtime root (keep models)..." : "Creating runtime root...",
                successStatus: runtimeExists ? "Runtime root reinstalled (models preserved)." : "Runtime root ready.",
                async _ =>
                {
                    using (KimodoBridgeServerTool.EnterRuntimeMaintenanceScope())
                    {
                        await KimodoBridgeService.Shared.StopAsync(CancellationToken.None);

                        if (!KimodoBridgeServerTool.ReinstallRuntimeRoot())
                        {
                            throw new InvalidOperationException("Failed to reinstall runtime root from package template.");
                        }
                    }

                    await Task.CompletedTask;
                });
        }

        private Task StartServerAsync()
        {
            return RunOperationAsync(
                initialStatus: "Starting server...",
                successStatus: null,
                progress => StartServerConnectionAsync(progress),
                onSuccess: () =>
                {
                    PullServerStatusFromController();
                    operationStatus = serverState == ServerState.Enabled ? "Server connected." : "Server start completed.";
                });
        }

        private Task StopServerAsync()
        {
            return RunOperationAsync(
                initialStatus: "Stopping server...",
                successStatus: "Server stopped.",
                async _ => await KimodoBridgeService.Shared.StopAsync(CancellationToken.None));
        }

        private Task DeleteAllDataAsync()
        {
            string runtimeRootPath = runtimeRoot;
            return RunOperationAsync(
                initialStatus: "Deleting runtime data...",
                successStatus: "Runtime data deleted.",
                async _ =>
                {
                    using (KimodoBridgeServerTool.EnterRuntimeMaintenanceScope())
                    {
                        await KimodoBridgeService.Shared.StopAsync(CancellationToken.None);
                        if (!Directory.Exists(runtimeRootPath))
                        {
                            throw new DirectoryNotFoundException($"Runtime root not found: {runtimeRootPath}");
                        }

                        Directory.Delete(runtimeRootPath, recursive: true);
                    }
                });
        }

        private async Task RunOperationAsync(
            string initialStatus,
            string successStatus,
            Func<Action<string>, Task> action,
            Action onSuccess = null)
        {
            if (operationInProgress)
            {
                lastError = "Another server operation is already running.";
                operationStatus = "Failed.";
                return;
            }

            if (EditorCompilationStateGate.IsCompilingOrReloading)
            {
                lastError = "Editor is compiling or reloading scripts.";
                operationStatus = "Failed.";
                return;
            }

            operationInProgress = true;
            lastError = string.Empty;
            operationStatus = initialStatus ?? string.Empty;

            try
            {
                await action(message =>
                {
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        operationStatus = message;
                    }
                });

                Refresh();
                PullServerStatusFromController();
                onSuccess?.Invoke();
                if (!string.IsNullOrWhiteSpace(successStatus))
                {
                    operationStatus = successStatus;
                }
                else if (string.IsNullOrWhiteSpace(operationStatus))
                {
                    operationStatus = "Done.";
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                lastError = ex.Message;
                operationStatus = "Failed.";
                Refresh();
                PullServerStatusFromController();
            }
            finally
            {
                operationInProgress = false;
            }
        }

        private static Task StartServerConnectionAsync(Action<string> progress)
        {
            return KimodoBridgeService.Shared.WarmupAsync(progress, CancellationToken.None);
        }

        private static string SummarizeForUi(string message, int maxLength = 320)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            string normalized = string.Join(" ", message.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized.Substring(0, maxLength) + "...";
        }

    }
}
