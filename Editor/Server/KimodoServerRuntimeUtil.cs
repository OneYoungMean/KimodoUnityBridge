using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
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

        internal static readonly string[] SupportedModelNames = KimodoMotionModelProfiles.AllModelNames;

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

            string configuredPath = KimodoPlayableClipGenerationSettings.instance.QuickServerPath;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                try
                {
                    return Path.GetFullPath(configuredPath);
                }
                catch
                {
                    return configuredPath;
                }
            }
            return Path.Combine(ResolveProjectRoot(), "NvlabKimodoQuickServer~");
        }

        internal static string ReadQuickServerVersion(string runtimeRoot)
        {
            try
            {
                string packagePath = Path.Combine(runtimeRoot ?? string.Empty, "package.json");
                string version = JObject.Parse(File.ReadAllText(packagePath)).Value<string>("version");
                return string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
            }
            catch
            {
                return "unknown";
            }
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

        internal static bool ReinstallRuntimeRoot()
        {
            string runtimeRoot = GetRuntimeRootPath();
            string projectRoot = ResolveProjectRoot();

            if (Directory.Exists(runtimeRoot))
            {
                ClearRuntimeRootExceptModels(runtimeRoot);
            }
            return TryBootstrapRuntimeRootFromPackage(projectRoot, runtimeRoot);
        }

        internal static bool RefreshRuntimeRoot()
        {
            return TryBootstrapRuntimeRootFromPackage(ResolveProjectRoot(), GetRuntimeRootPath());
        }

        internal static bool IsRuntimeSyncRequired(string runtimeRoot)
        {
            if (!KimodoPlayableClipGenerationSettings.instance.AutoSyncQuickServer)
            {
                return false;
            }

            if (!TryGetSyncVersions(runtimeRoot, out Version packagedVersion, out Version runtimeVersion))
            {
                return false;
            }

            return packagedVersion.CompareTo(runtimeVersion) > 0;
        }

        internal static bool TrySyncRuntimeRootIfNeeded(string runtimeRoot, out string message)
        {
            message = string.Empty;
            if (!KimodoPlayableClipGenerationSettings.instance.AutoSyncQuickServer)
            {
                message = "QuickServer auto sync is disabled.";
                return true;
            }

            if (!TryGetSyncVersions(runtimeRoot, out Version packagedVersion, out Version runtimeVersion))
            {
                message = "QuickServer sync skipped because either the packaged or installed version is unavailable.";
                return true;
            }

            if (packagedVersion.CompareTo(runtimeVersion) <= 0)
            {
                message = $"QuickServer {runtimeVersion} is current (packaged: {packagedVersion}).";
                return true;
            }

            string templateRoot = ResolvePackagedRuntimeRoot(ResolveProjectRoot());
            if (!Directory.Exists(templateRoot))
            {
                message = "QuickServer sync failed because the packaged runtime template is unavailable.";
                return false;
            }

            string resolvedRuntimeRoot = Path.GetFullPath(runtimeRoot ?? string.Empty);
            if (string.Equals(resolvedRuntimeRoot, Path.GetFullPath(templateRoot), StringComparison.OrdinalIgnoreCase))
            {
                message = "QuickServer sync refused because the configured runtime is the packaged template itself.";
                return false;
            }

            bool keepModels = packagedVersion.Major == runtimeVersion.Major;
            bool keepVenv = keepModels && packagedVersion.Minor == runtimeVersion.Minor;
            if (keepVenv)
            {
                MigrateLegacyVenvToRoot(resolvedRuntimeRoot);
            }

            ClearRuntimeRootPreserving(resolvedRuntimeRoot, keepModels, keepVenv);
            Directory.CreateDirectory(resolvedRuntimeRoot);
            CopyDirectoryRecursive(
                templateRoot,
                resolvedRuntimeRoot,
                skipTopLevelDirectoryName: keepModels ? "models" : null);

            string syncedVersion = ReadQuickServerVersion(resolvedRuntimeRoot);
            if (!string.Equals(syncedVersion, packagedVersion.ToString(3), StringComparison.OrdinalIgnoreCase))
            {
                message = $"QuickServer sync completed with an unexpected installed version: {syncedVersion}.";
                return false;
            }

            string preserved = keepVenv
                ? "models and root .venv"
                : (keepModels ? "models" : "nothing");
            message = $"Synchronized QuickServer {runtimeVersion} -> {packagedVersion}; preserved {preserved}.";
            return true;
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

            string templateRoot = ResolvePackagedRuntimeRoot(projectRoot, packageResolvedPath);
            if (!Directory.Exists(templateRoot))
            {
                // The package template is unavailable (e.g. the "NvlabKimodoQuickServer~" folder
                // was not shipped with the package). Fall back to downloading it from GitHub, and
                // if that fails, leave a placeholder folder with manual download instructions.
                return TryBootstrapRuntimeRootFromGitHub(runtimeRoot);
            }

            Directory.CreateDirectory(runtimeRoot);
            CopyDirectoryRecursive(
                templateRoot,
                runtimeRoot,
                skipTopLevelDirectoryName: Directory.Exists(Path.Combine(runtimeRoot, "models")) ? "models" : null);
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
                CopyDirectoryRecursive(
                    extractedRoot,
                    runtimeRoot,
                    skipTopLevelDirectoryName: Directory.Exists(Path.Combine(runtimeRoot, "models")) ? "models" : null);
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

        private static void ClearRuntimeRootExceptModels(string runtimeRoot)
        {
            ClearRuntimeRootPreserving(runtimeRoot, keepModels: true, keepVenv: false);
        }

        private static void ClearRuntimeRootPreserving(string runtimeRoot, bool keepModels, bool keepVenv)
        {
            if (!Directory.Exists(runtimeRoot))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(runtimeRoot))
            {
                File.Delete(file);
            }

            foreach (string dir in Directory.GetDirectories(runtimeRoot))
            {
                string dirName = Path.GetFileName(dir);
                if ((keepModels && string.Equals(dirName, "models", StringComparison.OrdinalIgnoreCase)) ||
                    (keepVenv && string.Equals(dirName, ".venv", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Directory.Delete(dir, recursive: true);
            }
        }

        private static string ResolvePackagedRuntimeRoot(string projectRoot, string packageResolvedPath = null)
        {
            string resolvedPackagePath = packageResolvedPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedPackagePath))
            {
                try
                {
                    PackageInfo info = PackageInfo.FindForAssembly(typeof(KimodoServerRuntimeUtil).Assembly);
                    resolvedPackagePath = info?.resolvedPath ?? string.Empty;
                }
                catch
                {
                    resolvedPackagePath = string.Empty;
                }
            }

            string candidate1 = string.IsNullOrWhiteSpace(resolvedPackagePath)
                ? string.Empty
                : Path.GetFullPath(Path.Combine(resolvedPackagePath, "NvlabKimodoQuickServer~"));
            string candidate2 = Path.GetFullPath(Path.Combine(projectRoot, "Library", "PackageCache", "com.unity.kimodo_unity_motion_tools", "NvlabKimodoQuickServer~"));
            string candidate3 = Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "KimodoUnityBridge", "NvlabKimodoQuickServer~"));
            return Directory.Exists(candidate1)
                ? candidate1
                : (Directory.Exists(candidate2) ? candidate2 : candidate3);
        }

        private static bool TryGetSyncVersions(string runtimeRoot, out Version packagedVersion, out Version runtimeVersion)
        {
            packagedVersion = null;
            runtimeVersion = null;
            string templateRoot = ResolvePackagedRuntimeRoot(ResolveProjectRoot());
            if (!Directory.Exists(templateRoot))
            {
                return false;
            }

            if (!TryParseRuntimeVersion(ReadQuickServerVersion(templateRoot), out packagedVersion) ||
                !TryParseRuntimeVersion(ReadQuickServerVersion(runtimeRoot), out runtimeVersion))
            {
                packagedVersion = null;
                runtimeVersion = null;
                return false;
            }

            return true;
        }

        private static bool TryParseRuntimeVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            Match match = Regex.Match(text.Trim(), @"^(\d+)\.(\d+)\.(\d+)");
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out int major) ||
                !int.TryParse(match.Groups[2].Value, out int minor) ||
                !int.TryParse(match.Groups[3].Value, out int patch))
            {
                return false;
            }

            version = new Version(major, minor, patch);
            return true;
        }

        private static void MigrateLegacyVenvToRoot(string runtimeRoot)
        {
            string target = Path.Combine(runtimeRoot, ".venv");
            string legacy = Path.Combine(runtimeRoot, "kimodo", ".venv");
            if (Directory.Exists(target) || !Directory.Exists(legacy))
            {
                return;
            }

            Directory.Move(legacy, target);
        }

        internal static void CopyDirectoryRecursive(string sourceDir, string destinationDir, string skipTopLevelDirectoryName = null)
        {
            var ignoreRules = GitIgnoreRuleSet.LoadFromRoot(sourceDir);
            CopyDirectoryRecursiveCore(
                sourceDir,
                destinationDir,
                sourceDir,
                ignoreRules,
                skipTopLevelDirectoryName,
                isRoot: true);
        }

        private static void CopyDirectoryRecursiveCore(
            string sourceDir,
            string destinationDir,
            string rootSourceDir,
            GitIgnoreRuleSet ignoreRules,
            string skipTopLevelDirectoryName,
            bool isRoot)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                if (ignoreRules.IsIgnored(file, isDirectory: false))
                {
                    continue;
                }

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

                if (isRoot &&
                    !string.IsNullOrWhiteSpace(skipTopLevelDirectoryName) &&
                    string.Equals(dirName, skipTopLevelDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ignoreRules.IsIgnored(dir, isDirectory: true))
                {
                    continue;
                }

                string destSubDir = Path.Combine(destinationDir, dirName);
                CopyDirectoryRecursiveCore(
                    dir,
                    destSubDir,
                    rootSourceDir,
                    ignoreRules.WithDirectoryRules(dir),
                    skipTopLevelDirectoryName,
                    isRoot: false);
            }
        }

        private sealed class GitIgnoreRuleSet
        {
            private readonly List<GitIgnorePattern> patterns;

            private GitIgnoreRuleSet(List<GitIgnorePattern> patterns)
            {
                this.patterns = patterns;
            }

            internal static GitIgnoreRuleSet LoadFromRoot(string rootDir)
            {
                var patterns = new List<GitIgnorePattern>();
                AddDirectoryRules(patterns, rootDir);
                return new GitIgnoreRuleSet(patterns);
            }

            internal GitIgnoreRuleSet WithDirectoryRules(string directory)
            {
                var next = new List<GitIgnorePattern>(patterns);
                AddDirectoryRules(next, directory);
                return new GitIgnoreRuleSet(next);
            }

            internal bool IsIgnored(string path, bool isDirectory)
            {
                bool ignored = false;
                foreach (GitIgnorePattern pattern in patterns)
                {
                    if (!pattern.Matches(path, isDirectory))
                    {
                        continue;
                    }

                    ignored = !pattern.IsNegation;
                }

                return ignored;
            }

            private static void AddDirectoryRules(List<GitIgnorePattern> patterns, string rulesDirectory)
            {
                string gitIgnorePath = Path.Combine(rulesDirectory, ".gitignore");
                if (!File.Exists(gitIgnorePath))
                {
                    return;
                }

                foreach (string rawLine in File.ReadAllLines(gitIgnorePath))
                {
                    GitIgnorePattern pattern = GitIgnorePattern.TryParse(rawLine, rulesDirectory);
                    if (pattern != null)
                    {
                        patterns.Add(pattern);
                    }
                }
            }
        }

        private sealed class GitIgnorePattern
        {
            private readonly Regex regex;

            private GitIgnorePattern(string baseDirectory, bool isNegation, bool directoryOnly, Regex regex)
            {
                BaseDirectory = baseDirectory;
                IsNegation = isNegation;
                DirectoryOnly = directoryOnly;
                this.regex = regex;
            }

            internal string BaseDirectory { get; }
            internal bool IsNegation { get; }
            internal bool DirectoryOnly { get; }

            internal static GitIgnorePattern TryParse(string rawLine, string baseDirectory)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    return null;
                }

                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    return null;
                }

                bool isNegation = line.StartsWith("!", StringComparison.Ordinal);
                if (isNegation)
                {
                    line = line.Substring(1).Trim();
                    if (line.Length == 0)
                    {
                        return null;
                    }
                }

                bool directoryOnly = line.EndsWith("/", StringComparison.Ordinal);
                if (directoryOnly)
                {
                    line = line.TrimEnd('/');
                    if (line.Length == 0)
                    {
                        return null;
                    }
                }

                bool anchored = line.StartsWith("/", StringComparison.Ordinal);
                if (anchored)
                {
                    line = line.Substring(1);
                }

                string normalized = line.Replace('\\', '/');
                string regexPattern = BuildRegexPattern(normalized, anchored, normalized.Contains("/"));
                return new GitIgnorePattern(
                    baseDirectory,
                    isNegation,
                    directoryOnly,
                    new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }

            internal bool Matches(string path, bool isDirectory)
            {
                if (DirectoryOnly && !isDirectory)
                {
                    return false;
                }

                string relative = GetRelativePath(BaseDirectory, path);
                if (string.IsNullOrEmpty(relative))
                {
                    return false;
                }

                return regex.IsMatch(relative.Replace('\\', '/'));
            }

            private static string BuildRegexPattern(string pattern, bool anchored, bool containsSlash)
            {
                string converted = Regex.Escape(pattern)
                    .Replace(@"\*\*", "___DOUBLESTAR___")
                    .Replace(@"\*", @"[^/]*")
                    .Replace(@"\?", @"[^/]")
                    .Replace("___DOUBLESTAR___", @".*");

                if (!containsSlash)
                {
                    return @"(^|.*/)" + converted + @"(/.*)?$";
                }

                if (anchored)
                {
                    return "^" + converted + @"(/.*)?$";
                }

                return @"(^|.*/)" + converted + @"(/.*)?$";
            }

            private static string GetRelativePath(string baseDirectory, string targetPath)
            {
                string basePath = Path.GetFullPath(baseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullPath = Path.GetFullPath(targetPath);
                if (string.Equals(basePath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !fullPath.StartsWith(basePath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return fullPath.Substring(basePath.Length + 1);
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
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                return File.Exists(Path.Combine(modelDir, "config.yaml")) &&
                    File.Exists(Path.Combine(modelDir, "tokenizer.safetensors")) &&
                    File.Exists(Path.Combine(modelDir, "denoiser.safetensors")) &&
                    File.Exists(Path.Combine(modelDir, "stats", "motion", "mean.npy")) &&
                    File.Exists(Path.Combine(modelDir, "stats", "motion", "std.npy"));
            }

            return File.Exists(Path.Combine(modelDir, "model.safetensors"));
        }

        internal static bool IsTextEncoderInstalled(string runtimeRoot, KimodoTextEncoderMode mode)
        {
            return IsTextEncoderInstalled(runtimeRoot, mode, IsKeepCpuForceEnabled(), null);
        }

        internal static bool IsTextEncoderInstalled(string runtimeRoot, KimodoTextEncoderMode mode, string modelsRootOverride)
        {
            return IsTextEncoderInstalled(runtimeRoot, mode, IsKeepCpuForceEnabled(), modelsRootOverride);
        }

        internal static bool IsTextEncoderInstalled(string runtimeRoot, KimodoTextEncoderMode mode, bool forceCpu, string modelsRootOverride)
        {
            string modelsRoot = string.IsNullOrWhiteSpace(modelsRootOverride)
                ? Path.Combine(runtimeRoot, "models")
                : Path.GetFullPath(modelsRootOverride.Trim());

            if (mode == KimodoTextEncoderMode.HighPrecision)
            {
                string singleDir = Path.Combine(modelsRoot, "KIMODO-Meta3_llm2vec_FP16");
                if (File.Exists(Path.Combine(singleDir, "model.safetensors")))
                {
                    return true;
                }

                string fullDir = Path.Combine(modelsRoot, "Meta-Llama-3-8B-Instruct");
                string peftDir = Path.Combine(modelsRoot, "LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised");
                bool fullOk = File.Exists(Path.Combine(fullDir, "model.safetensors.index.json")) || File.Exists(Path.Combine(fullDir, "model.safetensors"));
                bool peftOk = File.Exists(Path.Combine(peftDir, "adapter_model.safetensors")) || File.Exists(Path.Combine(peftDir, "model.safetensors"));
                return fullOk && peftOk;
            }

            string int8Dir = Path.Combine(modelsRoot, "KIMODO-Meta3_llm2vec_INT8");
            bool int8Ok = File.Exists(Path.Combine(int8Dir, "quantized_state_dict.pt"));
            if (forceCpu)
            {
                return int8Ok;
            }

            string nf4Dir = Path.Combine(modelsRoot, "KIMODO-Meta3_llm2vec_NF4");
            return File.Exists(Path.Combine(nf4Dir, "model.safetensors")) || int8Ok;
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
                string torchRuntime = string.Empty;
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

                    if (key.Equals("torch_runtime", StringComparison.OrdinalIgnoreCase))
                    {
                        torchRuntime = value;
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

                if (!string.IsNullOrWhiteSpace(torchRuntime))
                {
                    profile = torchRuntime;
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


