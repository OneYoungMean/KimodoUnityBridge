using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoSetupWizardWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Kimodo Setup Wizard";
        private const string ServerSettingsPath = "Project/Kimodo Server Manager";
        private const string PypiUrl = "https://pypi.org";
        private const string AliyunPypiUrl = "https://mirrors.aliyun.com/pypi/";
        private const string HuggingFaceUrl = "https://huggingface.co";
        private const string ModelScopeUrl = "https://modelscope.cn";
        private const long DiskErrorThresholdBytes = 10L * 1024L * 1024L * 1024L;
        private const long DiskWarningThresholdBytes = 20L * 1024L * 1024L * 1024L;

        private sealed class EndpointProbeStatus
        {
            public string Label = string.Empty;
            public string Url = string.Empty;
            public bool Reachable;
            public string Error = string.Empty;
        }

        private sealed class ConnectivityStatus
        {
            public EndpointProbeStatus Pypi = new EndpointProbeStatus();
            public EndpointProbeStatus AliyunPypi = new EndpointProbeStatus();
            public EndpointProbeStatus HuggingFace = new EndpointProbeStatus();
            public EndpointProbeStatus ModelScope = new EndpointProbeStatus();
        }

        [InitializeOnLoadMethod]
        private static void ScheduleFirstLaunchWizard()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            EditorApplication.delayCall += TryOpenOnFirstLaunch;
        }

        [MenuItem(MenuPath, priority = 105)]
        private static void OpenWindowFromMenu()
        {
            KimodoSetupWizardWindow window = GetWindow<KimodoSetupWizardWindow>(true, "Kimodo Setup Wizard");
            window.minSize = new Vector2(640f, 560f);
            window.Show();
            window.Focus();
        }

        [MenuItem("Kimodo/Server Settings", priority = 106)]
        private static void OpenServerSettings()
        {
            SettingsService.OpenProjectSettings(ServerSettingsPath);
        }

        private static void TryOpenOnFirstLaunch()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            if (settings == null || settings.SetupWizardCompleted)
            {
                return;
            }

            if (HasOpenInstances<KimodoSetupWizardWindow>())
            {
                return;
            }

            OpenWindowFromMenu();
        }

        private Task<ConnectivityStatus> connectivityTask;
        private ConnectivityStatus connectivityStatus;
        private string operationStatus = string.Empty;
        private string lastError = string.Empty;
        private bool operationInProgress;
        private Vector2 scroll;
        private string modelPathDraft = string.Empty;
        private string gpuName = string.Empty;
        private string cpuName = string.Empty;
        private int systemMemoryMb;
        private int graphicsMemoryMb;
        private bool cudaLikelySupported;

        private void OnEnable()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            modelPathDraft = settings != null ? settings.LocalModelsPath : string.Empty;
            gpuName = SystemInfo.graphicsDeviceName ?? string.Empty;
            cpuName = SystemInfo.processorType ?? string.Empty;
            systemMemoryMb = SystemInfo.systemMemorySize;
            graphicsMemoryMb = SystemInfo.graphicsMemorySize;
            cudaLikelySupported = IsLikelyCudaSupported();
            StartConnectivityCheck();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            MarkWizardCompleted();
        }

        private void OnEditorUpdate()
        {
            if (connectivityTask == null || !connectivityTask.IsCompleted)
            {
                return;
            }

            if (connectivityStatus == null)
            {
                if (connectivityTask.IsFaulted)
                {
                    string error = connectivityTask.Exception?.GetBaseException().Message ?? "unknown error";
                    connectivityStatus = BuildFailedConnectivityStatus(error);
                }
                else
                {
                    connectivityStatus = connectivityTask.Result;
                }

                Repaint();
            }
        }

        private void OnGUI()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            if (settings == null)
            {
                EditorGUILayout.HelpBox("Kimodo settings are unavailable.", MessageType.Error);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Kimodo Setup Wizard", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox("This wizard configures the shared Kimodo editor defaults and server runtime for this project.", MessageType.Info);

            DrawSystemDiagnostics();
            DrawStorageAndModelSection(settings);
            DrawConnectivityDiagnostics();
            DrawActionSection();
            DrawDefaultParametersSection(settings);

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }

            if (!string.IsNullOrWhiteSpace(operationStatus))
            {
                EditorGUILayout.HelpBox(operationStatus, MessageType.Info);
            }

            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Open Server Setting", "Open the Kimodo Server Manager project settings page."), GUILayout.Height(26f)))
                {
                    MarkWizardCompleted();
                    SettingsService.OpenProjectSettings(ServerSettingsPath);
                }

                if (GUILayout.Button(new GUIContent("Close", "Close this setup wizard."), GUILayout.Height(26f)))
                {
                    MarkWizardCompleted();
                    Close();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSystemDiagnostics()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("System", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(
                "GPU",
                $"{(string.IsNullOrWhiteSpace(gpuName) ? "(unknown)" : gpuName)} ({Mathf.Max(0, graphicsMemoryMb)} MB)",
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("CPU", string.IsNullOrWhiteSpace(cpuName) ? "(unknown)" : cpuName, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("Memory", $"{Mathf.Max(0, systemMemoryMb)} MB", EditorStyles.wordWrappedMiniLabel);
            }

            if (cudaLikelySupported)
            {
                EditorGUILayout.HelpBox("Detected GPU is likely CUDA-capable. Kimodo can use GPU execution when the runtime environment supports it.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Detected GPU is not likely supported by PyTorch CUDA. Kimodo will fall back to CPU execution.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawStorageAndModelSection(KimodoPlayableClipGenerationSettings settings)
        {
            string pathForDiskCheck = ResolveDiskCheckPath();
            long freeBytes = TryGetAvailableFreeBytes(pathForDiskCheck, out string diskError);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Storage & Model Directory", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Check Path", pathForDiskCheck, EditorStyles.wordWrappedMiniLabel);

            if (!string.IsNullOrWhiteSpace(diskError))
            {
                EditorGUILayout.HelpBox($"Disk space check failed: {diskError}", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField("Free Space", FormatBytes(freeBytes), EditorStyles.wordWrappedMiniLabel);
                if (freeBytes < DiskErrorThresholdBytes)
                {
                    EditorGUILayout.HelpBox("Available disk space is critically low. Less than 10 GB is free.", MessageType.Error);
                }
                else if (freeBytes < DiskWarningThresholdBytes)
                {
                    EditorGUILayout.HelpBox("Available disk space may be insufficient. Free space is between 10 GB and 20 GB.", MessageType.Warning);
                }
            }

            EditorGUI.BeginChangeCheck();
            modelPathDraft = EditorGUILayout.DelayedTextField(
                new GUIContent("Models Path", "Optional custom model directory. Leave empty to use the runtime default models folder."),
                modelPathDraft ?? string.Empty);
            bool changed = EditorGUI.EndChangeCheck();

            if (GUILayout.Button(new GUIContent("Browse...", "Pick a custom models directory."), GUILayout.Width(100f)))
            {
                string startDir = string.IsNullOrWhiteSpace(modelPathDraft)
                    ? KimodoBridgeServerTool.GetRuntimeRootPath()
                    : modelPathDraft;
                string selected = EditorUtility.OpenFolderPanel("Select Models Folder", startDir, string.Empty);
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    modelPathDraft = selected;
                    changed = true;
                }
            }

            if (changed)
            {
                settings.LocalModelsPath = modelPathDraft?.Trim() ?? string.Empty;
                settings.SaveSettings();
            }

            EditorGUILayout.HelpBox("This path is shared with Project Settings and is used as the default model directory override.", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void DrawConnectivityDiagnostics()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Network", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (connectivityStatus == null)
            {
                EditorGUILayout.HelpBox("Checking package and model endpoints...", MessageType.Info);
            }
            else
            {
                DrawNetworkRow("PyPI", connectivityStatus.Pypi, connectivityStatus.AliyunPypi);
                DrawNetworkRow("Model", connectivityStatus.HuggingFace, connectivityStatus.ModelScope);

                bool pypiAvailable = connectivityStatus.Pypi.Reachable || connectivityStatus.AliyunPypi.Reachable;
                bool modelAvailable = connectivityStatus.HuggingFace.Reachable || connectivityStatus.ModelScope.Reachable;

                if (!pypiAvailable || !modelAvailable)
                {
                    EditorGUILayout.HelpBox("Network connectivity check failed for one or more required groups. Please inspect the endpoint status above.", MessageType.Error);
                }
                else
                {
                    EditorGUILayout.HelpBox("Network connectivity looks usable. At least one package source and one model source are reachable.", MessageType.Info);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawDefaultParametersSection(KimodoPlayableClipGenerationSettings settings)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Default Parameters", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            string[] options = KimodoBridgeServerTool.SupportedModelNames;
            string selectedModel = settings.DefaultBridgeModelName;
            int selectedIndex = Array.IndexOf(options, selectedModel);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUILayout.Popup(
                new GUIContent("Default Model", "Default Kimodo model used by editor flows that do not explicitly override the model."),
                selectedIndex,
                options);
            KimodoTextEncoderMode newEncoderMode = (KimodoTextEncoderMode)EditorGUILayout.EnumPopup(
                new GUIContent("Default Text Encoder Mode", "Default precision/performance preference. Device placement is automatic."),
                settings.DefaultTextEncoderMode);
            bool modelChanged = newIndex != selectedIndex;
            if (EditorGUI.EndChangeCheck())
            {
                settings.DefaultBridgeModelName = options[Mathf.Clamp(newIndex, 0, options.Length - 1)];
                if (modelChanged)
                {
                    newEncoderMode = KimodoGenerationInspectorGui.IsArdy(settings.DefaultBridgeModelName)
                        ? KimodoTextEncoderMode.HighPrecision
                        : KimodoTextEncoderMode.HighPerformance;
                }
                settings.DefaultTextEncoderMode = newEncoderMode;
                settings.SaveSettings();
            }

            KimodoGenerationInspectorGui.DrawArdyTextEncoderWarning(
                KimodoGenerationInspectorGui.IsArdy(settings.DefaultBridgeModelName),
                settings.DefaultTextEncoderMode);

            EditorGUILayout.HelpBox("These values share the same backing settings as Project Settings and now represent default generation parameters.", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void DrawActionSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Install Server", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            string runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
            bool runtimeExists = Directory.Exists(runtimeRoot);
            string installButtonLabel = operationInProgress
                ? "Processing..."
                : (runtimeExists ? "Start Server" : "Install / Start Server");

            using (new EditorGUI.DisabledScope(operationInProgress || EditorCompilationStateGate.IsCompilingOrReloading))
            {
                if (GUILayout.Button(new GUIContent(installButtonLabel, "Install the runtime if needed, then start the shared Kimodo server."), GUILayout.Height(28f)))
                {
                    _ = InstallAndStartServerAsync(runtimeExists);
                }
            }

            EditorGUILayout.LabelField("Runtime Root", runtimeRoot, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        private async Task InstallAndStartServerAsync(bool runtimeExists)
        {
            if (operationInProgress)
            {
                return;
            }

            operationInProgress = true;
            lastError = string.Empty;
            operationStatus = runtimeExists ? "Starting server..." : "Installing runtime...";

            try
            {
                using (KimodoBridgeServerTool.EnterRuntimeMaintenanceScope())
                {
                    if (!runtimeExists)
                    {
                        await KimodoBridgeService.Shared.StopAsync(CancellationToken.None);
                        if (!KimodoBridgeServerTool.BootstrapRuntimeRootIfMissing())
                        {
                            throw new InvalidOperationException("Failed to install Kimodo runtime.");
                        }
                    }
                }

                await KimodoBridgeService.Shared.WarmupAsync(
                    progress =>
                    {
                        if (!string.IsNullOrWhiteSpace(progress))
                        {
                            operationStatus = progress;
                        }
                    },
                    CancellationToken.None);

                operationStatus = "Server connected.";
                MarkWizardCompleted();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                lastError = ex.Message;
                operationStatus = "Failed.";
            }
            finally
            {
                operationInProgress = false;
                Repaint();
            }
        }

        private void StartConnectivityCheck()
        {
            connectivityStatus = null;
            connectivityTask = Task.Run(BuildConnectivityStatus);
        }

        private string ResolveDiskCheckPath()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string modelsPath = settings != null ? settings.LocalModelsPath?.Trim() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(modelsPath))
            {
                return modelsPath;
            }

            string runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
            if (!string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return runtimeRoot;
            }

            return Directory.GetCurrentDirectory();
        }

        private static long TryGetAvailableFreeBytes(string path, out string error)
        {
            error = string.Empty;
            try
            {
                string fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : path);
                string root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    error = "drive root not found";
                    return 0L;
                }

                var drive = new DriveInfo(root);
                return drive.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return 0L;
            }
        }

        private static bool TryCheckUrlReachable(string url, out string error)
        {
            error = string.Empty;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                request.AllowAutoRedirect = true;
                request.UserAgent = "KimodoUnityBridge";
                using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                int code = (int)response.StatusCode;
                return code >= 200 && code < 400;
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse response && response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                return TryCheckUrlReachableWithGet(url, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryCheckUrlReachableWithGet(string url, out string error)
        {
            error = string.Empty;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                request.AllowAutoRedirect = true;
                request.UserAgent = "KimodoUnityBridge";
                using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                int code = (int)response.StatusCode;
                return code >= 200 && code < 400;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string FormatBytes(long bytes)
        {
            const double gib = 1024d * 1024d * 1024d;
            if (bytes <= 0)
            {
                return "0 GB";
            }

            return $"{bytes / gib:F2} GB";
        }

        private static bool IsLikelyCudaSupported()
        {
            string vendor = SystemInfo.graphicsDeviceVendor ?? string.Empty;
            return vendor.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawNetworkRow(string groupLabel, EndpointProbeStatus primary, EndpointProbeStatus secondary)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(groupLabel, GUILayout.Width(48f));
                DrawEndpointStatus(primary);
                GUILayout.Space(10f);
                DrawEndpointStatus(secondary);
            }
        }

        private void DrawEndpointStatus(EndpointProbeStatus status)
        {
            GUIContent icon = EditorGUIUtility.IconContent(status != null && status.Reachable ? "TestPassed" : "console.erroricon");
            string label = status == null
                ? "(unknown)"
                : $"{status.Label}";

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.Width(90f));
                GUILayout.Label(icon, GUILayout.Width(18f), GUILayout.Height(18f));
            }
        }

        private static ConnectivityStatus BuildConnectivityStatus()
        {
            return new ConnectivityStatus
            {
                Pypi = ProbeEndpoint("PyPI", PypiUrl),
                AliyunPypi = ProbeEndpoint("Aliyun", AliyunPypiUrl),
                HuggingFace = ProbeEndpoint("HuggingFace", HuggingFaceUrl),
                ModelScope = ProbeEndpoint("ModelScope", ModelScopeUrl)
            };
        }

        private static ConnectivityStatus BuildFailedConnectivityStatus(string error)
        {
            return new ConnectivityStatus
            {
                Pypi = new EndpointProbeStatus { Label = "PyPI", Url = PypiUrl, Reachable = false, Error = error },
                AliyunPypi = new EndpointProbeStatus { Label = "Aliyun", Url = AliyunPypiUrl, Reachable = false, Error = error },
                HuggingFace = new EndpointProbeStatus { Label = "HuggingFace", Url = HuggingFaceUrl, Reachable = false, Error = error },
                ModelScope = new EndpointProbeStatus { Label = "ModelScope", Url = ModelScopeUrl, Reachable = false, Error = error }
            };
        }

        private static EndpointProbeStatus ProbeEndpoint(string label, string url)
        {
            bool reachable = TryCheckUrlReachable(url, out string error);
            return new EndpointProbeStatus
            {
                Label = label,
                Url = url,
                Reachable = reachable,
                Error = error
            };
        }

        private static void MarkWizardCompleted()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            if (settings == null || settings.SetupWizardCompleted)
            {
                return;
            }

            settings.SetupWizardCompleted = true;
            settings.SaveSettings();
        }
    }
}
