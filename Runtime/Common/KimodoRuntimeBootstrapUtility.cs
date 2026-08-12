using System;
using System.IO;

namespace KimodoBridge
{
    internal static class KimodoRuntimeBootstrapUtility
    {
        internal static string EnsureRuntimeRootForCurrentMode(string runtimeRoot)
        {
            string resolvedRuntimeRoot = string.IsNullOrWhiteSpace(runtimeRoot)
                ? string.Empty
                : Path.GetFullPath(runtimeRoot);

#if UNITY_EDITOR
            if (!string.IsNullOrWhiteSpace(resolvedRuntimeRoot) && !Directory.Exists(resolvedRuntimeRoot))
            {
                try
                {
                    if (KimodoEditorRuntimeHooks.TryBootstrapRuntimeRoot() &&
                        Directory.Exists(resolvedRuntimeRoot))
                    {
                        return resolvedRuntimeRoot;
                    }
                }
                catch
                {
                    // Keep runtime validation behavior unchanged; caller will report the missing path.
                }
            }
#endif

            return resolvedRuntimeRoot;
        }

    }
}
