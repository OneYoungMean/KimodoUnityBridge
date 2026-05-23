using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [InitializeOnLoad]
    internal static class KimodoRetargetDebugDefineBootstrap
    {
        private const string MasterDefine = "KIMODO_RETARGET_DEBUG";
        private static readonly string[] DefinesToEnsure =
        {
            "KIMODO_RETARGET_DEBUG",
            "KIMODO_RETARGET_DEBUG_SCENE",
            "KIMODO_RETARGET_DEBUG_LOG"
        };

        static KimodoRetargetDebugDefineBootstrap()
        {
            EditorApplication.delayCall += EnsureDefinesForSelectedBuildTarget;
        }

        private static void EnsureDefinesForSelectedBuildTarget()
        {
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown)
            {
                return;
            }

            string current = GetSymbols(group);
            var symbols = new HashSet<string>(
                (current ?? string.Empty)
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.Ordinal);

            bool changed = false;
            for (int i = 0; i < DefinesToEnsure.Length; i++)
            {
                if (symbols.Add(DefinesToEnsure[i]))
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            string merged = string.Join(";", symbols.OrderBy(s => s, StringComparer.Ordinal));
            SetSymbols(group, merged);
            UnityEngine.Debug.Log($"[Kimodo] Added scripting define symbols: {string.Join('.', DefinesToEnsure)} ({group})");
        }

        private static string GetSymbols(BuildTargetGroup group)
        {
#pragma warning disable CS0618
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
#pragma warning restore CS0618
        }

        private static void SetSymbols(BuildTargetGroup group, string defines)
        {
#pragma warning disable CS0618
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, defines);
#pragma warning restore CS0618
        }
    }
}
