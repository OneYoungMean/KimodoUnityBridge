using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using KimodoUnityMotionTools.Bridge;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoServerRuntimeUtil
    {
        internal static string RuntimeRootOverrideForTests { get; set; }

        internal sealed class InstalledModelInfo
        {
            public string Name;
            public string DirectoryPath;
            public long SizeBytes;
        }

        internal static readonly string[] SupportedModelNames =
        {
            "Kimodo-SOMA-RP-v1",
            "Kimodo-G1-RP-v1",
            "Kimodo-SMPLX-RP-v1",
            "Kimodo-SOMA-SEED-v1",
            "Kimodo-G1-SEED-v1"
        };

        internal static string ResolveProjectRoot()
        {
            string cwd = Path.GetFullPath(Environment.CurrentDirectory);
            string probe = cwd;
            for (int i = 0; i < 8; i++)
            {
                if (IsUnityProjectRoot(probe))
                {
                    return probe;
                }

                DirectoryInfo parent = Directory.GetParent(probe);
                if (parent == null)
                {
                    break;
                }

                probe = parent.FullName;
            }

            return cwd;
        }

        internal static string GetRuntimeRootPath()
        {
            if (!string.IsNullOrWhiteSpace(RuntimeRootOverrideForTests))
            {
                return Path.GetFullPath(RuntimeRootOverrideForTests);
            }
            return Path.Combine(ResolveProjectRoot(), "NvlabKimodoQuickServer");
        }

        internal static bool EnsureRuntimeRootExists()
        {
            string runtimeRoot = GetRuntimeRootPath();
            if (Directory.Exists(runtimeRoot))
            {
                return true;
            }

            return TryBootstrapRuntimeRootFromPackage(ResolveProjectRoot(), runtimeRoot);
        }

        internal static bool TryBootstrapRuntimeRootFromPackage(string projectRoot, string runtimeRoot)
        {
            string packageResolvedPath = string.Empty;
            try
            {
                PackageInfo info = PackageInfo.FindForAssembly(typeof(KimodoServerRuntimeUtil).Assembly);
                if (info != null && !string.IsNullOrWhiteSpace(info.resolvedPath))
                {
                    packageResolvedPath = info.resolvedPath;
                }
            }
            catch
            {
                // ignore
            }

            string candidate1 = string.IsNullOrWhiteSpace(packageResolvedPath)
                ? string.Empty
                : Path.GetFullPath(Path.Combine(packageResolvedPath, "NvlabKimodoQuickServer~"));
            string candidate2 = Path.GetFullPath(Path.Combine(projectRoot, "Library", "PackageCache", "com.unity.kimodo_unity_motion_tools", "NvlabKimodoQuickServer~"));
            string candidate3 = Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "KimodoUnityBridge", "NvlabKimodoQuickServer~"));
            string templateRoot = Directory.Exists(candidate1)
                ? candidate1
                : (Directory.Exists(candidate2) ? candidate2 : candidate3);
            if (!Directory.Exists(templateRoot))
            {
                return false;
            }

            Directory.CreateDirectory(runtimeRoot);
            CopyDirectoryRecursive(templateRoot, runtimeRoot);
            return true;
        }

        internal static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(dir);
                if (string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string destSubDir = Path.Combine(destinationDir, Path.GetFileName(dir));
                CopyDirectoryRecursive(dir, destSubDir);
            }
        }

        internal static bool TryReadServerPort(string runtimeRoot, out string host, out int port)
        {
            string portFile = Path.Combine(runtimeRoot, "serverport");
            return BridgeEndpointResolver.TryReadServerEndpointFromFile(
                portFile,
                "127.0.0.1",
                out host,
                out port,
                out _);
        }

        internal static bool IsServerResponsive(string host, int port)
        {
            try
            {
                const int connectTimeoutMs = 1500;
                const int ioTimeoutMs = 1200;
                using var client = new TcpClient();
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(connectTimeoutMs)))
                {
                    return false;
                }
                client.EndConnect(ar);
                client.ReceiveTimeout = ioTimeoutMs;
                client.SendTimeout = ioTimeoutMs;
                using NetworkStream stream = client.GetStream();
                stream.ReadTimeout = ioTimeoutMs;
                stream.WriteTimeout = ioTimeoutMs;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                writer.WriteLine("{\"cmd\":\"ping\"}");
                string line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    return false;
                }

                JObject obj = JObject.Parse(line);
                string status = obj.Value<string>("status") ?? string.Empty;
                return string.Equals(status, "pong", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TrySendQuit(string host, int port)
        {
            try
            {
                const int ioTimeoutMs = 1200;
                using var client = new TcpClient(host, port);
                client.ReceiveTimeout = ioTimeoutMs;
                client.SendTimeout = ioTimeoutMs;
                using NetworkStream stream = client.GetStream();
                stream.ReadTimeout = ioTimeoutMs;
                stream.WriteTimeout = ioTimeoutMs;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                writer.WriteLine("{\"cmd\":\"quit\"}");
                _ = reader.ReadLine();
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static List<InstalledModelInfo> GetInstalledModels(string runtimeRoot)
        {
            var result = new List<InstalledModelInfo>();
            string modelsRoot = Path.Combine(runtimeRoot, "models");
            if (!Directory.Exists(modelsRoot))
            {
                return result;
            }

            foreach (string dir in Directory.GetDirectories(modelsRoot))
            {
                string name = Path.GetFileName(dir);
                string required = Path.Combine(dir, "model.safetensors");
                string requiredIndex = Path.Combine(dir, "model.safetensors.index.json");
                string adapter = Path.Combine(dir, "adapter_model.safetensors");
                if (!File.Exists(required) && !File.Exists(requiredIndex) && !File.Exists(adapter))
                {
                    continue;
                }

                result.Add(new InstalledModelInfo
                {
                    Name = name,
                    DirectoryPath = dir,
                    SizeBytes = GetDirectorySizeSafe(dir)
                });
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        internal static long GetDirectorySizeSafe(string root)
        {
            if (!Directory.Exists(root))
            {
                return 0L;
            }

            long total = 0L;
            try
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore
            }

            return total;
        }

        internal static string FormatBytes(long bytes)
        {
            double v = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int idx = 0;
            while (v >= 1024.0 && idx < units.Length - 1)
            {
                v /= 1024.0;
                idx++;
            }
            return $"{v:0.##} {units[idx]}";
        }

        internal static bool IsSelectedBridgeModelInstalled(string runtimeRoot, string modelName)
        {
            return IsSelectedBridgeModelInstalled(runtimeRoot, modelName, null);
        }

        internal static bool IsSelectedBridgeModelInstalled(string runtimeRoot, string modelName, string modelsRootOverride)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = "Kimodo-SOMA-RP-v1";
            }
            string modelsRoot = string.IsNullOrWhiteSpace(modelsRootOverride)
                ? Path.Combine(runtimeRoot, "models")
                : Path.GetFullPath(modelsRootOverride.Trim());
            string modelDir = Path.Combine(modelsRoot, modelName.Trim());
            return File.Exists(Path.Combine(modelDir, "model.safetensors"));
        }

        internal static bool IsTextEncoderInstalled(string runtimeRoot, bool highVram)
        {
            return IsTextEncoderInstalled(runtimeRoot, highVram, null);
        }

        internal static bool IsTextEncoderInstalled(string runtimeRoot, bool highVram, string modelsRootOverride)
        {
            string modelsRoot = string.IsNullOrWhiteSpace(modelsRootOverride)
                ? Path.Combine(runtimeRoot, "models")
                : Path.GetFullPath(modelsRootOverride.Trim());

            if (highVram)
            {
                string fullDir = Path.Combine(modelsRoot, "Meta-Llama-3-8B-Instruct");
                string peftDir = Path.Combine(modelsRoot, "LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised");
                bool fullOk = File.Exists(Path.Combine(fullDir, "model.safetensors.index.json")) || File.Exists(Path.Combine(fullDir, "model.safetensors"));
                bool peftOk = File.Exists(Path.Combine(peftDir, "adapter_model.safetensors")) || File.Exists(Path.Combine(peftDir, "model.safetensors"));
                return fullOk && peftOk;
            }

            string nf4Dir = Path.Combine(modelsRoot, "KIMODO-Meta3_llm2vec_NF4");
            return File.Exists(Path.Combine(nf4Dir, "model.safetensors"));
        }

        internal static int EstimateMissingConfigPoints(string runtimeRoot, bool highVram, string selectedModel)
        {
            return EstimateMissingConfigPoints(runtimeRoot, highVram, selectedModel, null);
        }

        internal static int EstimateMissingConfigPoints(string runtimeRoot, bool highVram, string selectedModel, string modelsRootOverride)
        {
            int points = 0;
            bool firstSetup = !File.Exists(Path.Combine(runtimeRoot, ".setup.complete"));
            if (firstSetup)
            {
                points += 5;
            }

            if (!IsSelectedBridgeModelInstalled(runtimeRoot, selectedModel, modelsRootOverride))
            {
                points += 2; // Kimodo base model estimate
            }

            if (!IsTextEncoderInstalled(runtimeRoot, highVram, modelsRootOverride))
            {
                points += highVram ? 16 : 4;
            }

            return points;
        }

        internal static string ResolveStartScript(string runtimeRoot)
        {
            EnsureNoLegacyScripts(runtimeRoot);

            string s1 = Path.Combine(runtimeRoot, "run_server.bat");
            if (File.Exists(s1))
            {
                return s1;
            }
            string s2 = Path.Combine(runtimeRoot, "bash", "start_server.bat");
            if (File.Exists(s2))
            {
                return s2;
            }
            string s2Root = Path.Combine(runtimeRoot, "start_server.bat");
            if (File.Exists(s2Root))
            {
                return s2Root;
            }

            string s2Sh = Path.Combine(runtimeRoot, "bash", "start_server.sh");
            if (File.Exists(s2Sh))
            {
                return s2Sh;
            }
            string s2RootSh = Path.Combine(runtimeRoot, "start_server.sh");
            if (File.Exists(s2RootSh))
            {
                return s2RootSh;
            }
            return string.Empty;
        }

        private static bool IsUnityProjectRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return
                Directory.Exists(Path.Combine(path, "Assets")) &&
                Directory.Exists(Path.Combine(path, "ProjectSettings"));
        }

        internal static string ResolveSetupScript(string runtimeRoot)
        {
            EnsureNoLegacyScripts(runtimeRoot);

            string s1 = Path.Combine(runtimeRoot, "bash", "setup.bat");
            if (File.Exists(s1))
            {
                return s1;
            }
            string s1Root = Path.Combine(runtimeRoot, "setup.bat");
            if (File.Exists(s1Root))
            {
                return s1Root;
            }

            string s1Sh = Path.Combine(runtimeRoot, "bash", "setup.sh");
            if (File.Exists(s1Sh))
            {
                return s1Sh;
            }
            string s1RootSh = Path.Combine(runtimeRoot, "setup.sh");
            if (File.Exists(s1RootSh))
            {
                return s1RootSh;
            }
            return string.Empty;
        }

        private static void EnsureNoLegacyScripts(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return;
            }

            string[] legacyScripts =
            {
                Path.Combine(runtimeRoot, "start_kimodo_bridge_offline.bat"),
                Path.Combine(runtimeRoot, "start_kimodo_bridge_offline.sh"),
                Path.Combine(runtimeRoot, "setup_kimodo_offline.bat"),
                Path.Combine(runtimeRoot, "setup_kimodo_offline.sh")
            };

            foreach (string legacyScript in legacyScripts)
            {
                if (File.Exists(legacyScript))
                {
                    throw new InvalidOperationException(
                        $"Legacy script detected and not supported: {legacyScript}");
                }
            }
        }
    }
}


