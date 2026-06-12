using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KimodoBridge.Editor
{
    internal static class KimodoRuntimeInstallerMenu
    {
        private const string MenuPath = "Kimodo/Install Kimodo Runtime To StreamingAssets";
        private const string RuntimeSourceFolderName = "NvlabKimodoQuickServer~";
        private const string RuntimeDestinationFolderName = "NvlabKimodoQuickServer";

        [MenuItem(MenuPath, priority = 112)]
        private static void InstallRuntimeToStreamingAssets()
        {
            try
            {
                string packageRoot = ResolvePackageRoot();
                string sourceRoot = Path.GetFullPath(Path.Combine(packageRoot, RuntimeSourceFolderName));
                if (!Directory.Exists(sourceRoot))
                {
                    throw new DirectoryNotFoundException($"Kimodo runtime source does not exist: {sourceRoot}");
                }

                string projectRoot = KimodoServerRuntimeUtil.ResolveProjectRoot();
                string streamingAssetsRoot = Path.Combine(projectRoot, "Assets", "StreamingAssets");
                string destinationRoot = Path.Combine(streamingAssetsRoot, RuntimeDestinationFolderName);

                Directory.CreateDirectory(streamingAssetsRoot);
                DeleteDestinationRoot(destinationRoot);
                KimodoServerRuntimeUtil.CopyDirectoryRecursive(sourceRoot, destinationRoot);
                AssetDatabase.Refresh();

                Debug.Log($"[KimodoRuntimeInstallerMenu] Installed Kimodo runtime to '{destinationRoot}'.");
                EditorUtility.DisplayDialog(
                    "Install Kimodo Runtime",
                    $"Kimodo runtime installed to:\n{destinationRoot}",
                    "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KimodoRuntimeInstallerMenu] Install failed: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Install Kimodo Runtime Failed",
                    ex.Message,
                    "OK");
            }
        }

        private static string ResolvePackageRoot()
        {
            PackageInfo info = PackageInfo.FindForAssembly(typeof(KimodoRuntimeInstallerMenu).Assembly);
            if (info == null || string.IsNullOrWhiteSpace(info.resolvedPath))
            {
                throw new InvalidOperationException("Cannot resolve Kimodo package root.");
            }

            return info.resolvedPath;
        }

        private static void DeleteDestinationRoot(string destinationRoot)
        {
            if (Directory.Exists(destinationRoot))
            {
                FileUtil.DeleteFileOrDirectory(destinationRoot);
            }

            string metaPath = destinationRoot + ".meta";
            if (File.Exists(metaPath))
            {
                FileUtil.DeleteFileOrDirectory(metaPath);
            }
        }
    }
}
