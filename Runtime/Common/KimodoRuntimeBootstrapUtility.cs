using System;
using System.IO;
using System.Reflection;

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
                    if (TryBootstrapRuntimeRootByReflection() &&
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

#if UNITY_EDITOR
        private static bool TryBootstrapRuntimeRootByReflection()
        {
            const string TypeName = "KimodoBridge.Editor.KimodoBridgeRuntimeInstallFacade";
            const string MethodName = "BootstrapRuntimeRootIfMissing";

            Type facadeType = Type.GetType($"{TypeName}, KimodoTool.Editor");
            if (facadeType == null)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    facadeType = assemblies[i].GetType(TypeName, throwOnError: false);
                    if (facadeType != null)
                    {
                        break;
                    }
                }
            }

            if (facadeType == null)
            {
                return false;
            }

            MethodInfo bootstrapMethod = facadeType.GetMethod(
                MethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (bootstrapMethod == null || bootstrapMethod.ReturnType != typeof(bool))
            {
                return false;
            }

            object result = bootstrapMethod.Invoke(null, null);
            return result is bool ok && ok;
        }
#endif
    }
}
