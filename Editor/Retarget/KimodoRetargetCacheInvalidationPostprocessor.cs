#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoRetargetCacheInvalidationPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var sourceClipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectAnimNames(importedAssets, sourceClipNames);
            //CollectAnimNames(movedAssets, sourceClipNames);
            //CollectAnimNames(movedFromAssetPaths, sourceClipNames);
            CollectAnimNames(deletedAssets, sourceClipNames);

            foreach (string sourceClipName in sourceClipNames)
            {
                InvalidateCachesContaining(sourceClipName);
            }
        }

        private static void CollectAnimNames(IEnumerable<string> assetPaths, HashSet<string> names)
        {
            if (assetPaths == null || names == null)
            {
                return;
            }

            foreach (string assetPath in assetPaths)
            {
                if (string.IsNullOrWhiteSpace(assetPath) ||
                    !assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                    assetPath.StartsWith(KimodoEditorClipWritebackService.GeneratedClipFolder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string clipName = Path.GetFileNameWithoutExtension(assetPath);
                if (!string.IsNullOrWhiteSpace(clipName))
                {
                    names.Add(clipName);
                }
            }
        }

        private static void InvalidateCachesContaining(string sourceClipName)
        {
            if (string.IsNullOrWhiteSpace(sourceClipName) || !AssetDatabase.IsValidFolder(KimodoEditorClipWritebackService.CacheClipFolder))
            {
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { KimodoEditorClipWritebackService.CacheClipFolder });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (clip == null ||
                    string.IsNullOrWhiteSpace(clip.name) ||
                    clip.name.StartsWith(KimodoEditorClipWritebackService.InvalidCachePrefix, StringComparison.OrdinalIgnoreCase) ||
                    clip.name.IndexOf(sourceClipName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                _ = KimodoEditorClipWritebackService.TryInvalidateNamedClipCache(clip.name, out _);
            }
        }
    }
}
#endif
