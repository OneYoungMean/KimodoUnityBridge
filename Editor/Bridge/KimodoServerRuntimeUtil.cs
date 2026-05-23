using System;
using System.IO;
using KimodoUnityMotionTools.Bridge;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoServerRuntimeUtil
    {
        internal static string RuntimeRootOverrideForTests { get; set; }

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
    }
}


