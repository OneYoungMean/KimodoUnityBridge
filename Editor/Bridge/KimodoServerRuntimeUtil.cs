using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KimodoBridge.Editor
{
    internal static class KimodoServerRuntimeUtil
    {
        internal static string RuntimeRootOverrideForTests { get; set; }

        // GitHub source used to bootstrap the runtime when the local package template is missing.
        private const string RuntimeRepoUrl = "https://github.com/OneYoungMean/NvlabKimodoQuickServer";
        private const string RuntimeRepoArchiveUrl = "https://github.com/OneYoungMean/NvlabKimodoQuickServer/archive/refs/heads/main.zip";
        private const string ManualDownloadFileName = "下载说明_DOWNLOAD_REQUIRED.txt";

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
            return Path.Combine(ResolveProjectRoot(), "NvlabKimodoQuickServer~");
        }

        internal static bool BootstrapRuntimeRootIfMissing()
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
                // The package template is unavailable (e.g. the "NvlabKimodoQuickServer~" folder
                // was not shipped with the package). Fall back to downloading it from GitHub, and
                // if that fails, leave a placeholder folder with manual download instructions.
                return TryBootstrapRuntimeRootFromGitHub(runtimeRoot);
            }

            Directory.CreateDirectory(runtimeRoot);
            CopyDirectoryRecursive(templateRoot, runtimeRoot);
            return true;
        }

        internal static bool TryBootstrapRuntimeRootFromGitHub(string runtimeRoot)
        {
            if (TryDownloadRuntimeRootFromGitHub(runtimeRoot, out string downloadError))
            {
                Debug.Log($"[Kimodo] Downloaded runtime from {RuntimeRepoUrl} to '{runtimeRoot}'.");
                return true;
            }

            // Download failed: create an empty folder with an instruction file and reveal it so the
            // user knows to download the runtime manually and place it here.
            Debug.LogError($"[Kimodo] Failed to download runtime from {RuntimeRepoUrl}: {downloadError}");
            CreateManualDownloadPlaceholder(runtimeRoot, downloadError);
            return false;
        }

        private static bool TryDownloadRuntimeRootFromGitHub(string runtimeRoot, out string error)
        {
            error = string.Empty;
            string tempZip = Path.Combine(Path.GetTempPath(), "NvlabKimodoQuickServer-" + Guid.NewGuid().ToString("N") + ".zip");
            string tempExtract = Path.Combine(Path.GetTempPath(), "NvlabKimodoQuickServer-" + Guid.NewGuid().ToString("N"));
            try
            {
                EditorUtility.DisplayProgressBar("Kimodo", $"Downloading runtime from GitHub...\n{RuntimeRepoArchiveUrl}", 0.3f);

                // GitHub archive endpoints require TLS 1.2 and redirect to codeload.github.com.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "KimodoUnityBridge");
                    client.DownloadFile(RuntimeRepoArchiveUrl, tempZip);
                }

                EditorUtility.DisplayProgressBar("Kimodo", "Extracting runtime...", 0.7f);
                if (Directory.Exists(tempExtract))
                {
                    Directory.Delete(tempExtract, recursive: true);
                }
                ZipFile.ExtractToDirectory(tempZip, tempExtract);

                // The GitHub archive wraps everything in a single top-level "<repo>-<branch>" folder.
                string extractedRoot = ResolveArchiveContentRoot(tempExtract);
                if (extractedRoot == null)
                {
                    error = "Downloaded archive did not contain the expected content.";
                    return false;
                }

                Directory.CreateDirectory(runtimeRoot);
                CopyDirectoryRecursive(extractedRoot, runtimeRoot);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                // Avoid leaving a half-written runtime folder around on failure.
                TryDeleteDirectoryQuiet(runtimeRoot);
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                TryDeleteFileQuiet(tempZip);
                TryDeleteDirectoryQuiet(tempExtract);
            }
        }

        private static string ResolveArchiveContentRoot(string extractRoot)
        {
            if (!Directory.Exists(extractRoot))
            {
                return null;
            }

            string[] topDirs = Directory.GetDirectories(extractRoot);
            string[] topFiles = Directory.GetFiles(extractRoot);
            if (topDirs.Length == 1 && topFiles.Length == 0)
            {
                return topDirs[0];
            }

            return extractRoot;
        }

        private static void CreateManualDownloadPlaceholder(string runtimeRoot, string downloadError)
        {
            try
            {
                Directory.CreateDirectory(runtimeRoot);
                string instructionPath = Path.Combine(runtimeRoot, ManualDownloadFileName);
                string content =
                    "Kimodo 运行时缺失 / Kimodo runtime is missing\r\n" +
                    "==============================================\r\n\r\n" +
                    "自动下载失败 / Automatic download failed:\r\n" +
                    "    " + downloadError + "\r\n\r\n" +
                    "请手动从下面的仓库下载，并将其内容（不含顶层文件夹）放到此目录：\r\n" +
                    "Please manually download from the repository below and place its contents\r\n" +
                    "(without the top-level folder) into this directory:\r\n\r\n" +
                    "    " + RuntimeRepoUrl + "\r\n" +
                    "    " + RuntimeRepoArchiveUrl + "\r\n\r\n" +
                    "目标目录 / Target directory:\r\n" +
                    "    " + runtimeRoot + "\r\n\r\n" +
                    "完成后请删除本说明文件并重试。\r\n" +
                    "After placing the files, delete this file and try again.\r\n";
                File.WriteAllText(instructionPath, content);

                EditorUtility.DisplayDialog(
                    "Kimodo 运行时下载失败 / Runtime download failed",
                    "无法自动下载 Kimodo 运行时。\r\n\r\n" +
                    "Could not download the Kimodo runtime automatically.\r\n\r\n" +
                    "请前往 GitHub 手动下载并放入已打开的目录：\r\n" +
                    "Please download it from GitHub manually and place it into the opened folder:\r\n\r\n" +
                    RuntimeRepoUrl + "\r\n\r\n" +
                    runtimeRoot,
                    "OK");

                EditorUtility.RevealInFinder(runtimeRoot);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kimodo] Failed to create manual download placeholder at '{runtimeRoot}': {ex.Message}");
            }
        }

        private static void TryDeleteFileQuiet(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }

        private static void TryDeleteDirectoryQuiet(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
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
            return IsTextEncoderInstalled(runtimeRoot, highVram, IsKeepCpuForceEnabled(), null);
        }

        internal static bool IsTextEncoderInstalled(string runtimeRoot, bool highVram, string modelsRootOverride)
        {
            return IsTextEncoderInstalled(runtimeRoot, highVram, IsKeepCpuForceEnabled(), modelsRootOverride);
        }

        internal static bool IsTextEncoderInstalled(string runtimeRoot, bool highVram, bool forceCpu, string modelsRootOverride)
        {
            string modelsRoot = string.IsNullOrWhiteSpace(modelsRootOverride)
                ? Path.Combine(runtimeRoot, "models")
                : Path.GetFullPath(modelsRootOverride.Trim());

            if (forceCpu)
            {
                string ggufDir = Path.Combine(modelsRoot, "KIMODO-Meta3_llm2vec_FP16-Q6_K");
                return File.Exists(Path.Combine(ggufDir, "KIMODO-Meta3_llm2vec_FP16-Q6_K.gguf"));
            }

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
            return EstimateMissingConfigPoints(runtimeRoot, highVram, IsKeepCpuForceEnabled(), selectedModel, null);
        }

        internal static int EstimateMissingConfigPoints(string runtimeRoot, bool highVram, string selectedModel, string modelsRootOverride)
        {
            return EstimateMissingConfigPoints(runtimeRoot, highVram, IsKeepCpuForceEnabled(), selectedModel, modelsRootOverride);
        }

        internal static int EstimateMissingConfigPoints(string runtimeRoot, bool highVram, bool forceCpu, string selectedModel)
        {
            return EstimateMissingConfigPoints(runtimeRoot, highVram, forceCpu, selectedModel, null);
        }

        internal static int EstimateMissingConfigPoints(string runtimeRoot, bool highVram, bool forceCpu, string selectedModel, string modelsRootOverride)
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

            if (!IsTextEncoderInstalled(runtimeRoot, highVram, forceCpu, modelsRootOverride))
            {
                points += (forceCpu || !highVram) ? 4 : 16;
            }

            return points;
        }

        internal static bool TryReadSetupProfile(string runtimeRoot, out string profile)
        {
            profile = string.Empty;
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return false;
            }

            string sentinel = Path.Combine(runtimeRoot, ".setup.complete");
            if (!File.Exists(sentinel))
            {
                return false;
            }

            try
            {
                string setupDevice = string.Empty;
                foreach (string raw in File.ReadAllLines(sentinel))
                {
                    string line = raw?.Trim() ?? string.Empty;
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    int idx = line.IndexOf('=');
                    if (idx <= 0 || idx >= line.Length - 1)
                    {
                        continue;
                    }

                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();
                    if (key.Equals("setup_profile", StringComparison.OrdinalIgnoreCase))
                    {
                        profile = value;
                        return !string.IsNullOrWhiteSpace(profile);
                    }

                    if (key.Equals("setup_device", StringComparison.OrdinalIgnoreCase))
                    {
                        setupDevice = value;
                    }
                }

                if (string.Equals(setupDevice, "cpu", StringComparison.OrdinalIgnoreCase))
                {
                    profile = "cpu";
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(setupDevice))
                {
                    profile = "gpu";
                    return true;
                }
            }
            catch
            {
                // ignore read failures
            }

            return false;
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

        private static bool IsKeepCpuForceEnabled()
        {
            return KimodoPlayableClipGenerationSettings.instance != null &&
                   KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental;
        }
    }
}


