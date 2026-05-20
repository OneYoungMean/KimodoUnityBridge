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
        private sealed class InstalledModelInfoView
        {
            public string Name;
            public string DirectoryPath;
            public long SizeBytes;
        }

        private string runtimeRoot;
        private Vector2 scroll;
        private List<InstalledModelInfoView> models = new List<InstalledModelInfoView>();
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
                runtimeRoot = KimodoServerLifecycleManager.GetRuntimeRootPath();
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
                    bool ok = KimodoServerLifecycleManager.EnsureRuntimeRootExists();
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

            string[] options = KimodoServerLifecycleManager.SupportedModelNames;
            int idx = Array.IndexOf(options, selectedModel);
            if (idx < 0) idx = 0;
            int newIdx = EditorGUILayout.Popup("Model", idx, options);
            selectedModel = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
            selectedVramMode = (KimodoBridgeVramMode)EditorGUILayout.EnumPopup(
                new GUIContent("VRAM Mode", "Low: quantized encoder (~4G). High: full model stack (~16G)."),
                selectedVramMode);
            KimodoPlayableClipGenerationSettings lifecycleSettings = KimodoPlayableClipGenerationSettings.instance;
            EditorGUI.BeginChangeCheck();
            bool closeOnPlay = EditorGUILayout.Toggle(
                new GUIContent("Close On Enter Play Mode", "When enabled, entering Play Mode will close Kimodo bridge server."),
                lifecycleSettings.CloseBridgeServerOnEnterPlayMode);
            if (EditorGUI.EndChangeCheck())
            {
                lifecycleSettings.CloseBridgeServerOnEnterPlayMode = closeOnPlay;
                lifecycleSettings.SaveSettings();
            }

            int encoderVramGb = selectedVramMode == KimodoBridgeVramMode.High ? 16 : 4;
            int totalVramGb = 2 + encoderVramGb;
            EditorGUILayout.HelpBox(
                $"Estimated VRAM for selected mode: ~{totalVramGb} GB (core 2 GB + encoder {encoderVramGb} GB).",
                MessageType.Info);
            if (KimodoServerLifecycleManager.TryGetModelMissingSetupMinutes(runtimeRoot, selectedVramMode == KimodoBridgeVramMode.High, selectedModel, out int minutes))
            {
                EditorGUILayout.HelpBox($"Model missing detected, update required, approximately {minutes} minutes.", MessageType.None);
            }

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
                    bool ok = KimodoServerLifecycleManager.TrySendQuit(serverHost, serverPort);
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
            long size = KimodoServerLifecycleManager.GetDirectorySizeSafe(runtimeRoot);
            EditorGUILayout.LabelField("Kimodo Data Size", KimodoServerLifecycleManager.FormatBytes(size));
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
                    EditorGUILayout.LabelField(KimodoServerLifecycleManager.FormatBytes(model.SizeBytes), GUILayout.Width(90f));
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
                    KimodoServerLifecycleManager.TrySendQuit(serverHost, serverPort);
                }

                if (Directory.Exists(runtimeRoot))
                {
                    string broken = runtimeRoot + ".broken." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(runtimeRoot, broken);
                }

                bool ok = KimodoServerLifecycleManager.EnsureRuntimeRootExists();
                if (!ok)
                {
                    lastOpStatus = "TryFix failed: cannot bootstrap runtime.";
                    return;
                }

                string setup = KimodoServerLifecycleManager.ResolveSetupScriptOrThrow(runtimeRoot);
                int rc = KimodoServerLifecycleManager.RunScriptBlocking(setup, "--output console");
                lastOpStatus = rc == 0
                    ? "TryFix complete: runtime reset and setup finished."
                    : $"TryFix partial: setup exited with code {rc}.";
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
                string startScript = KimodoServerLifecycleManager.ResolveStartScriptOrThrow(runtimeRoot);

                string args = $" --model \"{selectedModel}\"";
                if (selectedVramMode == KimodoBridgeVramMode.High)
                {
                    args += " --highvram";
                }
                args += " --output console";

                var psi = KimodoServerLifecycleManager.BuildScriptStartInfo(startScript, args, keepWindowOpen: true, useShellExecute: true);
                DiagnosticsProcess.Start(psi);
                lastOpStatus = $"Start requested: model={selectedModel}, vram={selectedVramMode}.";
            }
            catch (Exception e)
            {
                lastOpStatus = $"Start failed: {e.Message}";
            }
        }

        private void TryDeleteAllData()
        {
            try
            {
                if (serverRunning)
                {
                    KimodoServerLifecycleManager.TrySendQuit(serverHost, serverPort);
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
            runtimeRoot = KimodoServerLifecycleManager.GetRuntimeRootPath();
            runtimeExists = Directory.Exists(runtimeRoot);

            serverRunning = false;
            serverHost = "127.0.0.1";
            serverPort = -1;
            if (runtimeExists && KimodoServerLifecycleManager.TryReadServerPort(runtimeRoot, out string host, out int port))
            {
                if (KimodoServerLifecycleManager.IsServerResponsive(host, port))
                {
                    serverRunning = true;
                    serverHost = host;
                    serverPort = port;
                }
            }

            models = runtimeExists
                ? ConvertInstalledModels(KimodoServerLifecycleManager.GetInstalledModels(runtimeRoot))
                : new List<InstalledModelInfoView>();
        }

        private static List<InstalledModelInfoView> ConvertInstalledModels(IReadOnlyList<KimodoServerLifecycleManager.InstalledModelInfo> source)
        {
            var result = new List<InstalledModelInfoView>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                KimodoServerLifecycleManager.InstalledModelInfo model = source[i];
                result.Add(new InstalledModelInfoView
                {
                    Name = model.Name,
                    DirectoryPath = model.DirectoryPath,
                    SizeBytes = model.SizeBytes
                });
            }

            return result;
        }
    }
}

