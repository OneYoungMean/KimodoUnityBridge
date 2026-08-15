using System;
using System.Linq;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorOutputPathUtility
    {
        internal static string NormalizeOutputFolder(string value)
        {
            string folder = string.IsNullOrWhiteSpace(value)
                ? KimodoEditorClipWritebackService.GeneratedClipFolder
                : value.Trim().Replace('\\', '/').TrimEnd('/');
            if (!folder.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
                !folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("output_folder must be under Assets.");
            }
            if (folder.Split('/').Any(part => part == ".." || part == "." || string.IsNullOrWhiteSpace(part)))
            {
                throw new InvalidOperationException("output_folder contains an invalid path segment.");
            }
            return folder;
        }
    }
}
