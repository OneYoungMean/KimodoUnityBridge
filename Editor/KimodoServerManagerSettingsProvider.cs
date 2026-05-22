using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KimodoUnityMotionTools.ProjectEditor
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

        private const double ServerStatusQueryCooldownSeconds = 2.0;

        private string runtimeRoot = string.Empty;
        private string resolvedModelsRoot = string.Empty;
        private Vector2 scroll;
        private List<InstalledModelInfoView> models = new List<InstalledModelInfoView>();
        private bool runtimeExists;
        private bool usingCustomModelsPath;

        private ServerState serverState = ServerState.Disabled;
        private bool serverStatusQueryInFlight;
        private int serverStatusQueryVersion;
        private double nextServerStatusQueryAt;
        private double detectHintUntilTime;
        private string serverHost = "127.0.0.1";
        private int serverPort = -1;

        private bool operationInProgress;
        private string operationStatus = string.Empty;
        private string lastError = string.Empty;
        private string modelError = string.Empty;

        private string selectedModel = "Kimodo-SOMA-RP-v1";
        private KimodoBridgeVramMode selectedVramMode = KimodoBridgeVramMode.Low;

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
            Refresh();
            detectHintUntilTime = EditorApplication.timeSinceStartup + 2.0;
            ScheduleServerStateQuery(force: true);
        }

        public override void OnGUI(string searchContext)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            }

            ScheduleServerStateQuery(force: false);

            EditorGUILayout.LabelField("Kimodo Server Manager", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Runtime Root", runtimeRoot, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GUILayout.Width(100f)))
            {
                Refresh();
                ScheduleServerStateQuery(force: true);
            }

            if (!runtimeExists)
            {
                if (GUILayout.Button("Create Kimodo Server", GUILayout.Width(180f)))
                {
                    try
                    {
                        bool ok = KimodoBridgeController.EnsureRuntimeRootExists();
                        if (!ok)
                        {
                            throw new InvalidOperationException("Failed to create runtime root from package template.");
                        }

                        operationStatus = "Runtime root created.";
                        lastError = string.Empty;
                    }
                    catch (Exception e)
                    {
                        lastError = e.Message;
                    }

                    Refresh();
                    ScheduleServerStateQuery(force: true);
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

            int newIdx = EditorGUILayout.Popup("Model", idx, options);
            selectedModel = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
            selectedVramMode = (KimodoBridgeVramMode)EditorGUILayout.EnumPopup(
                new GUIContent("VRAM Mode", "Low: quantized encoder (~4G). High: full model stack (~16G)."),
                selectedVramMode);

            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;

            EditorGUI.BeginChangeCheck();
            int newLimit = EditorGUILayout.IntSlider(
                new GUIContent("Max Cached Clip", "Range: 1-1000"),
                settings.MaxGeneratedClips,
                KimodoPlayableClipGenerationSettings.MinGeneratedClipsLimit,
                KimodoPlayableClipGenerationSettings.MaxGeneratedClipsLimit);
            if (EditorGUI.EndChangeCheck())
            {
                settings.MaxGeneratedClips = newLimit;
                settings.SaveSettings();
            }

            string localModelsPath = settings.LocalModelsPath;
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            localModelsPath = EditorGUILayout.TextField(
                new GUIContent("Local Models Path", "Optional. Use this path for model detection list only."),
                localModelsPath);
            bool textChanged = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("Browse...", GUILayout.Width(90f)))
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

            EditorGUILayout.EndVertical();
        }

        private void DrawServerSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            bool showDetectHint = EditorApplication.timeSinceStartup < detectHintUntilTime;
            if (showDetectHint)
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
            }
            EditorGUILayout.LabelField("Status", serverState == ServerState.Enabled ? "enable" : "disable", EditorStyles.miniLabel);

            bool inMaintenance = KimodoBridgeController.IsRuntimeMaintenanceInProgress;
            bool stopMode = serverState == ServerState.Enabled;
            string buttonLabel = (operationInProgress || inMaintenance) ? "Processing..." : (stopMode ? "Stop Server" : "Start Server");

            using (new EditorGUI.DisabledScope(operationInProgress || inMaintenance))
            {
                if (GUILayout.Button(buttonLabel, GUILayout.Width(140f)))
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
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(120f), GUILayout.MaxHeight(260f));
                for (int i = 0; i < models.Count; i++)
                {
                    InstalledModelInfoView model = models[i];
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(model.Name, GUILayout.MinWidth(260f));

                    using (new EditorGUI.DisabledScope(usingCustomModelsPath || operationInProgress))
                    {
                        if (GUILayout.Button("Delete", GUILayout.Width(70f)))
                        {
                            TryDeleteModelDirectory(model.DirectoryPath, model.Name);
                            RefreshModelList();
                            GUIUtility.ExitGUI();
                        }
                    }

                    EditorGUILayout.EndHorizontal();
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
                if (GUILayout.Button("Try Fix (delete and reconfigure)", GUILayout.Height(24f)))
                {
                    TryFix();
                }

                if (GUILayout.Button("Delete All Data", GUILayout.Height(24f)))
                {
                    if (EditorUtility.DisplayDialog("Delete All Data", "Delete entire Kimodo runtime folder? This cannot be undone.", "Delete", "Cancel"))
                    {
                        _ = DeleteAllDataAsync();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private async Task StartServerAsync()
        {
            if (operationInProgress || KimodoBridgeController.IsRuntimeMaintenanceInProgress)
            {
                return;
            }

            operationInProgress = true;
            operationStatus = "Starting server...";
            lastError = string.Empty;
            try
            {
                string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(runtimeRoot);
                string modelsRootForServer = ResolveModelsRootForServer();
                string ready = await KimodoBridgeController.StartServerAsync(
                    launcherPath,
                    selectedModel,
                    selectedVramMode == KimodoBridgeVramMode.High,
                    runtimeRoot,
                    modelsRootForServer,
                    forceSetup: false,
                    progress =>
                    {
                        operationStatus = progress;
                        if (!string.IsNullOrWhiteSpace(progress))
                        {
                            Debug.Log("[Kimodo] " + progress);
                        }
                    },
                    CancellationToken.None);

                operationStatus = string.IsNullOrWhiteSpace(ready) ? "Server started." : ready;
            }
            catch (Exception e)
            {
                lastError = "Start failed: " + e.Message;
            }
            finally
            {
                operationInProgress = false;
                Refresh();
                ScheduleServerStateQuery(force: true);
            }
        }

        private async Task StopServerAsync()
        {
            if (operationInProgress || KimodoBridgeController.IsRuntimeMaintenanceInProgress)
            {
                return;
            }

            operationInProgress = true;
            operationStatus = "Stopping server...";
            lastError = string.Empty;
            try
            {
                await KimodoBridgeController.CloseServerAsync();
                operationStatus = "Server stop requested.";
            }
            catch (Exception e)
            {
                lastError = "Stop failed: " + e.Message;
            }
            finally
            {
                operationInProgress = false;
                Refresh();
                ScheduleServerStateQuery(force: true);
            }
        }

        private async Task DeleteAllDataAsync()
        {
            if (operationInProgress)
            {
                return;
            }

            operationInProgress = true;
            operationStatus = "Deleting runtime data...";
            lastError = string.Empty;
            try
            {
                await KimodoBridgeController.CloseServerAsync();
                if (Directory.Exists(runtimeRoot))
                {
                    Directory.Delete(runtimeRoot, recursive: true);
                    operationStatus = "Runtime data deleted.";
                }
                else
                {
                    operationStatus = "Runtime root not found.";
                }
            }
            catch (Exception e)
            {
                lastError = "Delete failed: " + e.Message;
            }
            finally
            {
                operationInProgress = false;
                Refresh();
                ScheduleServerStateQuery(force: true);
            }
        }

        private void TryFix()
        {
            _ = TryFixAsync();
        }

        private async Task TryFixAsync()
        {
            if (operationInProgress)
            {
                return;
            }

            operationInProgress = true;
            operationStatus = "TryFix running...";
            lastError = string.Empty;
            using IDisposable _maintenanceScope = KimodoBridgeController.EnterRuntimeMaintenanceScope();

            try
            {
                await KimodoBridgeController.CloseServerAsync();

                if (Directory.Exists(runtimeRoot))
                {
                    try
                    {
                        Directory.Delete(runtimeRoot, recursive: true);
                    }
                    catch (Exception e)
                    {
                        throw new InvalidOperationException(
                            "Runtime folder is currently in use. Please reboot and retry TryFix. " + e.Message,
                            e);
                    }
                }

                bool ok = KimodoBridgeController.EnsureRuntimeRootExists();
                if (!ok)
                {
                    throw new InvalidOperationException("TryFix failed: cannot bootstrap runtime.");
                }

                string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(runtimeRoot);
                string modelName = selectedModel;
                bool highVram = selectedVramMode == KimodoBridgeVramMode.High;
                string modelsRootForServer = ResolveModelsRootForServer();
                await KimodoBridgeController.StartServerAsync(
                    launcherPath,
                    modelName,
                    highVram,
                    runtimeRoot,
                    modelsRootForServer,
                    forceSetup: true,
                    progress =>
                    {
                        operationStatus = progress;
                        if (!string.IsNullOrWhiteSpace(progress))
                        {
                            Debug.Log("[Kimodo] " + progress);
                        }
                    },
                    CancellationToken.None);

                operationStatus = "TryFix complete: runtime reset and server started.";
            }
            catch (Exception e)
            {
                lastError = e.Message;
            }
            finally
            {
                operationInProgress = false;
                Refresh();
                ScheduleServerStateQuery(force: true);
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

        private void Refresh()
        {
            runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            runtimeExists = Directory.Exists(runtimeRoot);
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
                string[] dirs = Directory.GetDirectories(resolvedModelsRoot);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string dir = dirs[i];
                    string name = Path.GetFileName(dir);
                    if (!ShouldDisplayModelDirectory(name))
                    {
                        continue;
                    }

                    models.Add(new InstalledModelInfoView
                    {
                        Name = name,
                        DirectoryPath = dir
                    });
                }
            }
            catch (Exception e)
            {
                modelError = "Scan models failed: " + e.Message;
            }
        }

        private static bool ShouldDisplayModelDirectory(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return
                name.StartsWith("Kimodo-", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("kimodo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("llama", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("llm2vec", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private void ScheduleServerStateQuery(bool force)
        {
            if (!runtimeExists)
            {
                serverState = ServerState.Disabled;
                serverStatusQueryInFlight = false;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (!force && (serverStatusQueryInFlight || now < nextServerStatusQueryAt))
            {
                return;
            }

            serverStatusQueryInFlight = true;
            nextServerStatusQueryAt = now + ServerStatusQueryCooldownSeconds;
            int queryVersion = ++serverStatusQueryVersion;
            string root = runtimeRoot;

            _ = QueryServerStateAsync(root, queryVersion);
        }

        private async Task QueryServerStateAsync(string root, int queryVersion)
        {
            bool running = false;
            string host = "127.0.0.1";
            int port = -1;

            try
            {
                await Task.Run(() =>
                {
                    if (!Directory.Exists(root))
                    {
                        return;
                    }

                    if (!KimodoBridgeController.TryReadServerPort(root, out string readHost, out int readPort))
                    {
                        return;
                    }

                    if (KimodoBridgeController.IsServerResponsive(readHost, readPort))
                    {
                        running = true;
                        host = readHost;
                        port = readPort;
                    }
                });
            }
            catch
            {
                running = false;
            }

            if (queryVersion != serverStatusQueryVersion)
            {
                return;
            }

            serverStatusQueryInFlight = false;
            serverHost = host;
            serverPort = port;
            serverState = running ? ServerState.Enabled : ServerState.Disabled;
        }
    }
}


