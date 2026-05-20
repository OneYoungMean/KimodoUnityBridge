using System;
using System.Collections.Generic;
using DiagnosticsProcess = System.Diagnostics.Process;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using KimodoUnityMotionTools;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal sealed class KimodoServerManagerSettingsProvider : SettingsProvider
    {
        private string runtimeRoot;
        private Vector2 scroll;
        private List<KimodoServerRuntimeUtil.InstalledModelInfo> models = new List<KimodoServerRuntimeUtil.InstalledModelInfo>();
        private bool runtimeExists;
        private bool serverRunning;
        private string serverHost = "127.0.0.1";
        private int serverPort = -1;
        private string lastOpStatus = string.Empty;
        private string selectedModel = "Kimodo-SOMA-RP-v1";
        private KimodoBridgeVramMode selectedVramMode = KimodoBridgeVramMode.Low;

        private KimodoServerManagerSettingsProvider(string path, SettingsScope scope) : base(path, scope) { }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new KimodoServerManagerSettingsProvider("Project/Kimodo Server Manager", SettingsScope.Project)
            {
                keywords = new HashSet<string>(new[] { "Kimodo", "Server", "Model", "Bridge", "VRAM" })
            };
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            Refresh();
        }

        public override void OnGUI(string searchContext)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                runtimeRoot = KimodoServerRuntimeUtil.GetRuntimeRootPath();
            }

            EditorGUILayout.LabelField("Kimodo Server Manager", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Runtime Root", runtimeRoot, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GUILayout.Width(100f)))
            {
                Refresh();
            }
            if (!runtimeExists)
            {
                if (GUILayout.Button("Create Kimodo Server", GUILayout.Width(180f)))
                {
                    bool ok = KimodoServerRuntimeUtil.EnsureRuntimeRootExists();
                    lastOpStatus = ok ? "Runtime root created." : "Failed to create runtime root from package template.";
                    Refresh();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(lastOpStatus))
            {
                EditorGUILayout.HelpBox(lastOpStatus, MessageType.Info);
            }

            if (!runtimeExists)
            {
                EditorGUILayout.HelpBox("Directory does not exist. Click 'Create Kimodo Server' to bootstrap runtime.", MessageType.Warning);
                return;
            }

            DrawStartupSection();
            DrawServerSection();
            DrawStorageSection();
            DrawModelSection();
            DrawActionsSection();
        }

        private void DrawStartupSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Startup", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            string[] options = KimodoServerRuntimeUtil.SupportedModelNames;
            int idx = Array.IndexOf(options, selectedModel);
            if (idx < 0) idx = 0;
            int newIdx = EditorGUILayout.Popup("Model", idx, options);
            selectedModel = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
            selectedVramMode = (KimodoBridgeVramMode)EditorGUILayout.EnumPopup(
                new GUIContent("VRAM Mode", "Low: quantized encoder (~4G). High: full model stack (~16G)."),
                selectedVramMode);

            EditorGUILayout.HelpBox("显存说明：Kimodo 本体约 2G；低显存量化模型约 4G；高显存完整模型约 16G。", MessageType.Info);
            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(runtimeRoot, selectedVramMode == KimodoBridgeVramMode.High, selectedModel);
            int minutes = Mathf.Max(3, points * 3);
            EditorGUILayout.HelpBox($"预计配置时间：约 {minutes} 分钟（按规则：(缺失模型容量 + 首次配置5) * 3 分钟）。", MessageType.None);

            if (GUILayout.Button("Start Server", GUILayout.Width(140f)))
            {
                StartServerWithSelectedOptions();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawServerSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (serverRunning)
            {
                EditorGUILayout.HelpBox($"Running at {serverHost}:{serverPort}", MessageType.Info);
                if (GUILayout.Button("Stop Server", GUILayout.Width(120f)))
                {
                    bool ok = KimodoServerRuntimeUtil.TrySendQuit(serverHost, serverPort);
                    lastOpStatus = ok ? "Quit signal sent." : "Failed to send quit command.";
                    Refresh();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Server is not running.", MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawStorageSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Storage", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            long size = KimodoServerRuntimeUtil.GetDirectorySizeSafe(runtimeRoot);
            EditorGUILayout.LabelField("Kimodo Data Size", KimodoServerRuntimeUtil.FormatBytes(size));
            EditorGUILayout.EndVertical();
        }

        private void DrawModelSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Installed Models", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (models.Count == 0)
            {
                EditorGUILayout.LabelField("No model detected.");
            }
            else
            {
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(120f), GUILayout.MaxHeight(220f));
                foreach (var model in models)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(model.Name, GUILayout.MinWidth(250f));
                    EditorGUILayout.LabelField(KimodoServerRuntimeUtil.FormatBytes(model.SizeBytes), GUILayout.Width(90f));
                    if (GUILayout.Button("Clean", GUILayout.Width(70f)))
                    {
                        TryMoveToTrash(model.DirectoryPath);
                        Refresh();
                        GUIUtility.ExitGUI();
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActionsSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (GUILayout.Button("Try Fix (delete and reconfigure)", GUILayout.Height(24f)))
            {
                TryFix();
                Refresh();
            }

            if (GUILayout.Button("Delete All Data", GUILayout.Height(24f)))
            {
                if (EditorUtility.DisplayDialog("Delete All Data", "Delete entire Kimodo runtime folder? This cannot be undone.", "Delete", "Cancel"))
                {
                    TryDeleteAllData();
                    Refresh();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void TryFix()
        {
            try
            {
                if (serverRunning)
                {
                    KimodoServerRuntimeUtil.TrySendQuit(serverHost, serverPort);
                }

                if (Directory.Exists(runtimeRoot))
                {
                    string broken = runtimeRoot + ".broken." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(runtimeRoot, broken);
                }

                bool ok = KimodoServerRuntimeUtil.EnsureRuntimeRootExists();
                if (!ok)
                {
                    lastOpStatus = "TryFix failed: cannot bootstrap runtime.";
                    return;
                }

                string setup = KimodoServerRuntimeUtil.ResolveSetupScript(runtimeRoot);
                if (!string.IsNullOrWhiteSpace(setup))
                {
                    int rc = RunBatchBlocking(setup, "--output console");
                    lastOpStatus = rc == 0
                        ? "TryFix complete: runtime reset and setup finished."
                        : $"TryFix partial: setup exited with code {rc}.";
                }
                else
                {
                    lastOpStatus = "TryFix complete: runtime reset (setup script not found).";
                }
            }
            catch (Exception e)
            {
                lastOpStatus = $"TryFix failed: {e.Message}";
            }
        }

        private void StartServerWithSelectedOptions()
        {
            try
            {
                string startScript = KimodoServerRuntimeUtil.ResolveStartScript(runtimeRoot);
                if (string.IsNullOrWhiteSpace(startScript) || !File.Exists(startScript))
                {
                    lastOpStatus = "Start script not found (expected start_server.bat/run_server.bat).";
                    return;
                }

                string args = $" --model \"{selectedModel}\"";
                if (selectedVramMode == KimodoBridgeVramMode.High)
                {
                    args += " --highvram";
                }
                args += " --output console";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /k \"\"{startScript}\"{args}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = runtimeRoot
                };
                DiagnosticsProcess.Start(psi);
                lastOpStatus = $"Start requested: model={selectedModel}, vram={selectedVramMode}.";
            }
            catch (Exception e)
            {
                lastOpStatus = $"Start failed: {e.Message}";
            }
        }

        private int RunBatchBlocking(string scriptPath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                return -1;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c \"\"{scriptPath}\" {arguments}\"",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };
            using var proc = DiagnosticsProcess.Start(psi);
            if (proc == null)
            {
                return -1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }

        private void TryDeleteAllData()
        {
            try
            {
                if (serverRunning)
                {
                    KimodoServerRuntimeUtil.TrySendQuit(serverHost, serverPort);
                }

                if (Directory.Exists(runtimeRoot))
                {
                    string deleted = runtimeRoot + ".deleted." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(runtimeRoot, deleted);
                    lastOpStatus = $"Deleted runtime data by moving to: {deleted}";
                }
                else
                {
                    lastOpStatus = "Runtime root not found.";
                }
            }
            catch (Exception e)
            {
                lastOpStatus = $"DeleteAllData failed: {e.Message}";
            }
        }

        private void TryMoveToTrash(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    lastOpStatus = $"Model path not found: {path}";
                    return;
                }

                string dst = path + ".removed." + DateTime.Now.ToString("yyyyMMddHHmmss");
                Directory.Move(path, dst);
                lastOpStatus = $"Model moved out: {Path.GetFileName(path)}";
            }
            catch (Exception e)
            {
                lastOpStatus = $"Model clean failed: {e.Message}";
            }
        }

        private void Refresh()
        {
            runtimeRoot = KimodoServerRuntimeUtil.GetRuntimeRootPath();
            runtimeExists = Directory.Exists(runtimeRoot);

            serverRunning = false;
            serverHost = "127.0.0.1";
            serverPort = -1;
            if (runtimeExists && KimodoServerRuntimeUtil.TryReadServerPort(runtimeRoot, out string host, out int port))
            {
                if (KimodoServerRuntimeUtil.IsServerResponsive(host, port))
                {
                    serverRunning = true;
                    serverHost = host;
                    serverPort = port;
                }
            }

            models = runtimeExists
                ? KimodoServerRuntimeUtil.GetInstalledModels(runtimeRoot)
                : new List<KimodoServerRuntimeUtil.InstalledModelInfo>();
        }
    }
}
