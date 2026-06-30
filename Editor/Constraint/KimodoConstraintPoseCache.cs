using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KimodoBridge.Editor
{
    internal readonly struct PoseCacheRenderContext
    {
        public readonly int ClipId;
        public readonly int AnimatorId;
        public readonly string ModelName;
        public readonly KimodoConstraintRigType RigType;
        public readonly string ContextKey;

        public PoseCacheRenderContext(int clipId, int animatorId, string modelName, KimodoConstraintRigType rigType)
        {
            ClipId = clipId;
            AnimatorId = animatorId;
            ModelName = string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim();
            RigType = rigType;
            ContextKey = KimodoConstraintMarkerEditorUtility.GetCachedIntString(clipId) + ":" + KimodoConstraintMarkerEditorUtility.GetCachedIntString(animatorId);
        }
    }

    internal sealed class PoseCacheRenderItem
    {
        public string EntryId;
        public KimodoMarkerSampleResult SampleData;
        public string ConstraintType;
        public List<string> HighlightJoints;
        public bool Visible = true;
    }

    [InitializeOnLoad]
    internal static class KimodoConstraintPoseCache
    {
        private sealed class PoseCacheEntry
        {
            public string Key;
            public string ContextKey;
            public int ClipId;
            public int AnimatorId;
            public KimodoConstraintRigType RigType;
            public Transform Root;
            public Dictionary<string, Transform> NameMap;
            public List<Material> GeneratedMaterials;
            public bool PickingEnabled;
        }

        private static readonly Dictionary<string, PoseCacheEntry> Entries = new Dictionary<string, PoseCacheEntry>(StringComparer.Ordinal);

        private const float NonConstraintAlpha = 1.0f;
        private const float HighlightAlpha = 1.0f;
        private static readonly Color NonConstraintColor = new Color(1f, 1f, 1f, NonConstraintAlpha);
        private static readonly Color HighlightColor = new Color(1f, 0f, 0f, HighlightAlpha);

        static KimodoConstraintPoseCache()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += DestroyAll;
        }

        internal static bool RenderBatch(PoseCacheRenderContext context, IReadOnlyList<PoseCacheRenderItem> items, out string error)
        {
            error = string.Empty;
            if (context.ClipId == 0 || context.AnimatorId == 0)
            {
                error = "invalid clip/animator context";
                return false;
            }

            if (items == null || items.Count == 0)
            {
                DestroyContext(context);
                return true;
            }

            string contextKey = context.ContextKey;
            bool hasVisible = false;
            for (int i = 0; i < items.Count; i++)
            {
                PoseCacheRenderItem item = items[i];
                if (item != null && item.Visible && item.SampleData != null)
                {
                    hasVisible = true;
                    break;
                }
            }

            if (!hasVisible)
            {
                DestroyContext(context);
                return true;
            }

            var desiredKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                PoseCacheRenderItem item = items[i];
                if (item == null || !item.Visible || item.SampleData == null)
                {
                    continue;
                }

                string entryId = string.IsNullOrWhiteSpace(item.EntryId) ? $"item_{i}" : item.EntryId.Trim();
                string entryKey = BuildEntryKey(contextKey, entryId);
                desiredKeys.Add(entryKey);

                if (!TryGetOrCreateEntry(context, entryId, out PoseCacheEntry entry, out error))
                {
                    return false;
                }

                if (!ApplySampleToRig(item.SampleData, context.ModelName, entry, out error))
                {
                    int localAxisCount = item.SampleData != null && item.SampleData.localAxisAngles != null
                        ? item.SampleData.localAxisAngles.Count
                        : 0;
                    error = $"pose cache render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}, localAxisAngles={localAxisCount}): {error}";
                    return false;
                }

                var highlightedJoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectHighlightedJointsFromItem(item, context.ModelName, highlightedJoints);
                ApplyConstraintColoring(entry, highlightedJoints);
                SetEntryVisible(entry, true);
            }

            List<string> keysToRemove = null;
            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                if (!kv.Key.StartsWith(contextKey + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!desiredKeys.Contains(kv.Key))
                {
                    DestroyEntry(kv.Value);
                    keysToRemove ??= new List<string>();
                    keysToRemove.Add(kv.Key);
                }
            }

            if (keysToRemove != null)
            {
                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    Entries.Remove(keysToRemove[i]);
                }
            }
            SceneView.RepaintAll();
            return true;
        }

        internal static void SetGroupState(PoseCacheRenderContext context, bool visible, bool selectable)
        {
            string contextKey = context.ContextKey;
            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                if (!kv.Key.StartsWith(contextKey + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                ApplyEntryState(kv.Value, visible, selectable);
            }

            SceneView.RepaintAll();
        }

        internal static bool HasAnyTransformChanges(PoseCacheRenderContext context)
        {
            if (!TryGetFirstEntryForContext(context, out PoseCacheEntry entry) || entry?.Root == null)
            {
                return false;
            }

            Transform[] transforms = entry.Root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t != null && t.hasChanged)
                {
                    return true;
                }
            }

            return false;
        }

        internal static void ClearTransformChanges(PoseCacheRenderContext context)
        {
            if (!TryGetFirstEntryForContext(context, out PoseCacheEntry entry) || entry?.Root == null)
            {
                return;
            }

            Transform[] transforms = entry.Root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t != null)
                {
                    t.hasChanged = false;
                }
            }
        }

        internal static bool TryBuildSampleFromContext(
            PoseCacheRenderContext context,
            string markerType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            return TryBuildSampleFromContext(context, null, markerType, sampleTime, out sample, out error);
        }

        internal static bool TryBuildSampleFromContext(
            PoseCacheRenderContext context,
            string entryId,
            string markerType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            PoseCacheEntry entry;
            if (!string.IsNullOrWhiteSpace(entryId))
            {
                string key = BuildEntryKey(context.ContextKey, entryId.Trim());
                Entries.TryGetValue(key, out entry);
            }
            else
            {
                TryGetFirstEntryForContext(context, out entry);
            }

            if (entry?.Root == null)
            {
                error = "pose cache context has no active entry.";
                return false;
            }

            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    context.ModelName,
                    entry.Root,
                    out string[] jointNames,
                    out int[] parentIndices,
                    out Transform[] jointTransforms,
                    out error))
            {
                return false;
            }

            return KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                animator: null,
                skeletonRoot: entry.Root,
                modelName: context.ModelName,
                globalTime: sampleTime,
                markerType: markerType,
                jointNamesOverride: jointNames,
                parentIndicesOverride: parentIndices,
                jointsOverride: jointTransforms,
                out sample,
                out error);
        }

        internal static void DestroyEntry(PoseCacheRenderContext context, string entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId) || Entries.Count == 0)
            {
                return;
            }

            string key = BuildEntryKey(context.ContextKey, entryId.Trim());
            if (!Entries.TryGetValue(key, out PoseCacheEntry entry))
            {
                return;
            }

            DestroyEntry(entry);
            Entries.Remove(key);
            SceneView.RepaintAll();
        }

        internal static void DestroyEntriesForItemId(string entryId, PoseCacheRenderContext? keepContext = null)
        {
            if (string.IsNullOrWhiteSpace(entryId) || Entries.Count == 0)
            {
                return;
            }

            string normalizedEntryId = entryId.Trim();
            string keepContextKey = keepContext.HasValue
                ? keepContext.Value.ContextKey
                : null;
            string entryKeySuffix = ":" + normalizedEntryId;
            var keysToRemove = new List<string>();

            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                if (!kv.Key.EndsWith(entryKeySuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(keepContextKey) &&
                    string.Equals(kv.Value != null ? kv.Value.ContextKey : null, keepContextKey, StringComparison.Ordinal))
                {
                    continue;
                }

                keysToRemove.Add(kv.Key);
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                string key = keysToRemove[i];
                if (!Entries.TryGetValue(key, out PoseCacheEntry entry))
                {
                    continue;
                }

                DestroyEntry(entry);
                Entries.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                SceneView.RepaintAll();
            }
        }

        internal static void DestroyEntriesForClipId(int clipId, PoseCacheRenderContext? keepContext = null)
        {
            if (clipId == 0 || Entries.Count == 0)
            {
                return;
            }

            string keepContextKey = keepContext.HasValue
                ? keepContext.Value.ContextKey
                : null;
            var keysToRemove = new List<string>();

            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                PoseCacheEntry entry = kv.Value;
                if (entry == null || entry.ClipId != clipId)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(keepContextKey) &&
                    string.Equals(entry.ContextKey, keepContextKey, StringComparison.Ordinal))
                {
                    continue;
                }

                keysToRemove.Add(kv.Key);
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                string key = keysToRemove[i];
                if (!Entries.TryGetValue(key, out PoseCacheEntry entry))
                {
                    continue;
                }

                DestroyEntry(entry);
                Entries.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                SceneView.RepaintAll();
            }
        }

        internal static void DestroyContext(PoseCacheRenderContext context)
        {
            if (Entries.Count == 0)
            {
                return;
            }

            string contextKey = context.ContextKey;
            var keysToRemove = new List<string>();
            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                if (!kv.Key.StartsWith(contextKey + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                keysToRemove.Add(kv.Key);
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                string key = keysToRemove[i];
                if (Entries.TryGetValue(key, out PoseCacheEntry entry))
                {
                    DestroyEntry(entry);
                    Entries.Remove(key);
                }
            }

            if (keysToRemove.Count > 0)
            {
                SceneView.RepaintAll();
            }
        }

        internal static void DestroyAll()
        {
            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                DestroyEntry(kv.Value);
            }

            Entries.Clear();
            SceneView.RepaintAll();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange _)
        {
            DestroyAll();
        }

        private static bool TryGetOrCreateEntry(PoseCacheRenderContext context, string entryId, out PoseCacheEntry entry, out string error)
        {
            entry = null;
            error = string.Empty;
            if (context.ClipId == 0 || context.AnimatorId == 0)
            {
                error = "invalid clip/animator id";
                return false;
            }

            string contextKey = context.ContextKey;
            string normalizedEntryId = string.IsNullOrWhiteSpace(entryId) ? "default" : entryId.Trim();
            string key = BuildEntryKey(contextKey, normalizedEntryId);
            if (Entries.TryGetValue(key, out entry) && entry != null && entry.Root != null && entry.Root.gameObject != null)
            {
                return true;
            }

            KimodoConstraintRigType rigType = context.RigType != KimodoConstraintRigType.Unknown
                ? context.RigType
                : KimodoRigProfileDatabase.ResolveRigTypeFromModelName(context.ModelName);
            if (!KimodoConstraintPoseRigFactory.TryCreatePoseRig(context.ModelName, context.ClipId, context.AnimatorId, out KimodoConstraintPoseRigFactory.PoseRigInstance rigInstance, out error))
            {
                return false;
            }

            entry = new PoseCacheEntry
            {
                Key = key,
                ContextKey = contextKey,
                ClipId = context.ClipId,
                AnimatorId = context.AnimatorId,
                RigType = rigType,
                Root = rigInstance.Root != null ? rigInstance.Root.transform : null,
                NameMap = rigInstance.NameMap,
                GeneratedMaterials = rigInstance.GeneratedMaterials,
                PickingEnabled = false
            };

            Entries[key] = entry;
            SetEntrySelectable(entry, false);
            return true;
        }

        private static bool TryGetFirstEntryForContext(PoseCacheRenderContext context, out PoseCacheEntry entry)
        {
            entry = null;
            string contextKey = context.ContextKey;
            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                if (!kv.Key.StartsWith(contextKey + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                if (kv.Value != null && kv.Value.Root != null)
                {
                    entry = kv.Value;
                    return true;
                }
            }

            return false;
        }

        private static void DestroyEntry(PoseCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.Root != null && entry.Root.gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(entry.Root.gameObject);
            }

            if (entry.GeneratedMaterials != null)
            {
                for (int i = 0; i < entry.GeneratedMaterials.Count; i++)
                {
                    Material m = entry.GeneratedMaterials[i];
                    if (m != null)
                    {
                        UnityEngine.Object.DestroyImmediate(m);
                    }
                }
            }
        }

        private static void SetEntryVisible(PoseCacheEntry entry, bool visible)
        {
            if (entry?.Root == null || entry.Root.gameObject == null)
            {
                return;
            }

            if (entry.Root.gameObject.activeSelf != visible)
            {
                entry.Root.gameObject.SetActive(visible);
            }
        }

        private static void SetEntrySelectable(PoseCacheEntry entry, bool selectable)
        {
            if (entry?.Root == null || entry.Root.gameObject == null)
            {
                return;
            }

            if (entry.PickingEnabled == selectable)
            {
                return;
            }

            entry.PickingEnabled = selectable;
            try
            {
                if (selectable)
                {
                    SceneVisibilityManager.instance.EnablePicking(entry.Root.gameObject, true);
                }
                else
                {
                    SceneVisibilityManager.instance.DisablePicking(entry.Root.gameObject, true);
                }
            }
            catch
            {
                // ignore scene visibility errors
            }

            entry.Root.gameObject.hideFlags = selectable
                ? HideFlags.NotEditable | HideFlags.DontSave
                : HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
        }

        private static void ApplyEntryState(PoseCacheEntry entry, bool visible, bool selectable)
        {
            if (entry == null)
            {
                return;
            }

            SetEntryVisible(entry, visible);
            SetEntrySelectable(entry, selectable);
        }

        private static void ApplyConstraintColoring(PoseCacheEntry entry, HashSet<string> highlightedJoints)
        {
            if (entry == null || entry.Root == null)
            {
                return;
            }

            Renderer[] renderers = entry.Root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                bool highlighted = IsTransformHighlighted(renderer.transform, highlightedJoints);
                Material[] mats = renderer.sharedMaterials;
                if (mats == null)
                {
                    continue;
                }

                for (int m = 0; m < mats.Length; m++)
                {
                    Material mat = mats[m];
                    if (mat == null)
                    {
                        continue;
                    }

                    if (highlighted)
                    {
                        SetMaterialColor(mat, HighlightColor, HighlightAlpha);
                    }
                    else
                    {
                        SetMaterialColor(mat, NonConstraintColor, NonConstraintAlpha);
                    }
                }
            }
        }

        private static bool IsTransformHighlighted(Transform transform, HashSet<string> highlightedJoints)
        {
            if (transform == null || highlightedJoints == null || highlightedJoints.Count == 0)
            {
                return false;
            }

            Transform cur = transform;
            while (cur != null)
            {
                if (highlightedJoints.Contains(cur.name))
                {
                    return true;
                }

                cur = cur.parent;
            }

            return false;
        }

        private static void CollectHighlightedJointsFromItem(PoseCacheRenderItem item, string modelName, HashSet<string> output)
        {
            if (item == null || output == null)
            {
                return;
            }

            List<string> names = item.HighlightJoints != null && item.HighlightJoints.Count > 0
                ? item.HighlightJoints
                : (item.SampleData != null ? item.SampleData.jointNames : null);
            List<string> highlighted = KimodoMarkerSamplingUtility.BuildHighlightJointsForConstraint(item.ConstraintType, names, modelName);
            for (int i = 0; i < highlighted.Count; i++)
            {
                string name = highlighted[i];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    output.Add(name.Trim());
                }
            }
        }

        private static bool ApplySampleToRig(KimodoMarkerSampleResult sample, string modelName, PoseCacheEntry entry, out string error)
        {
            return KimodoRetargetAvatarUtility.TryApplyMarkerSampleToTransformMap(
                sample,
                modelName,
                entry != null ? entry.Root : null,
                entry != null ? entry.NameMap : null,
                out error);
        }

        private static string BuildContextKey(int clipId, int animatorId)
        {
            return KimodoConstraintMarkerEditorUtility.GetCachedIntString(clipId) + ":" + KimodoConstraintMarkerEditorUtility.GetCachedIntString(animatorId);
        }

        private static string BuildEntryKey(string contextKey, string entryId)
        {
            return contextKey + ":" + entryId;
        }

        private static void SetMaterialColor(Material mat, Color color, float alpha)
        {
            if (mat == null)
            {
                return;
            }

            Color c = new Color(color.r, color.g, color.b, alpha);
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", c);
            }

            if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", c);
            }

            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 0f);
            }

            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 0f);
            }

            if (mat.HasProperty("_AlphaClip"))
            {
                mat.SetFloat("_AlphaClip", 0f);
            }

            if (mat.HasProperty("_SrcBlend"))
            {
                mat.SetInt("_SrcBlend", (int)BlendMode.One);
            }

            if (mat.HasProperty("_DstBlend"))
            {
                mat.SetInt("_DstBlend", (int)BlendMode.Zero);
            }

            if (mat.HasProperty("_ZWrite"))
            {
                mat.SetInt("_ZWrite", 1);
            }

            mat.SetOverrideTag("RenderType", "Opaque");
            mat.renderQueue = (int)RenderQueue.Geometry;
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHABLEND_ON");
        }

    }
}
