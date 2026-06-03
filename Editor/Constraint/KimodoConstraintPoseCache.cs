using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal readonly struct PoseCacheRenderContext
    {
        public readonly int ClipId;
        public readonly int AnimatorId;
        public readonly string ModelName;
        public readonly KimodoConstraintRigType RigType;

        public PoseCacheRenderContext(int clipId, int animatorId, string modelName, KimodoConstraintRigType rigType)
        {
            ClipId = clipId;
            AnimatorId = animatorId;
            ModelName = string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim();
            RigType = rigType;
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

        private const float NonConstraintAlpha = 0.7f;
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
                SetGroupState(context, visible: false, selectable: false);
                return true;
            }

            string contextKey = BuildContextKey(context.ClipId, context.AnimatorId);
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
                SetGroupState(context, visible: false, selectable: false);
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
                    return false;
                }

                var highlightedJoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectHighlightedJointsFromItem(item, context.ModelName, highlightedJoints);
                ApplyConstraintColoring(entry, highlightedJoints);
                SetEntryVisible(entry, true);
            }

            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                if (!kv.Key.StartsWith(contextKey + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!desiredKeys.Contains(kv.Key))
                {
                    ApplyEntryState(kv.Value, visible: false, selectable: false);
                }
            }
            SceneView.RepaintAll();
            return true;
        }

        internal static void SetGroupState(PoseCacheRenderContext context, bool visible, bool selectable)
        {
            string contextKey = BuildContextKey(context.ClipId, context.AnimatorId);
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

        internal static void DestroyEntriesForItemId(string entryId, PoseCacheRenderContext? keepContext = null)
        {
            if (string.IsNullOrWhiteSpace(entryId) || Entries.Count == 0)
            {
                return;
            }

            string normalizedEntryId = entryId.Trim();
            string keepContextKey = keepContext.HasValue
                ? BuildContextKey(keepContext.Value.ClipId, keepContext.Value.AnimatorId)
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

            string contextKey = BuildContextKey(context.ClipId, context.AnimatorId);
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
                ? HideFlags.DontSave
                : (HideFlags.HideInHierarchy | HideFlags.DontSave);
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
            error = string.Empty;
            if (sample == null || entry == null || entry.Root == null || entry.NameMap == null)
            {
                error = "invalid sample or pose cache entry";
                return false;
            }

            string[] modelJointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            if (modelJointNames == null || modelJointNames.Length == 0)
            {
                error = $"model joint layout not found for '{modelName}'";
                return false;
            }

            int count = sample.localAxisAngles != null ? sample.localAxisAngles.Count : 0;
            int applyCount = Mathf.Min(modelJointNames.Length, count);
            for (int i = 0; i < applyCount; i++)
            {
                string jointName = modelJointNames[i];
                if (!entry.NameMap.TryGetValue(jointName, out Transform t) || t == null)
                {
                    error = $"joint '{jointName}' missing on pose rig";
                    return false;
                }

                t.localRotation = AxisAngleToQuaternion(sample.localAxisAngles[i]);
            }

            string rootJointName = KimodoRigProfileDatabase.GetRootJointNameForModel(modelName);
            if (!string.IsNullOrWhiteSpace(rootJointName) && entry.NameMap.TryGetValue(rootJointName, out Transform rootJoint) && rootJoint != null)
            {
                rootJoint.position = sample.rootPosition;
            }
            else
            {
                entry.Root.position = sample.rootPosition;
            }

            return true;
        }

        private static Quaternion AxisAngleToQuaternion(Vector3 axisAngle)
        {
            float angleRad = axisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Quaternion.identity;
            }

            Vector3 axis = axisAngle / angleRad;
            return Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
        }

        private static string BuildContextKey(int clipId, int animatorId)
        {
            return clipId.ToString() + ":" + animatorId.ToString();
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

            bool configuredTransparentMode = false;
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                configuredTransparentMode = true;
            }

            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 3f);
                configuredTransparentMode = true;
            }

            if (mat.HasProperty("_AlphaClip"))
            {
                mat.SetFloat("_AlphaClip", 0f);
            }

            if (mat.HasProperty("_SrcBlend"))
            {
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            }

            if (mat.HasProperty("_DstBlend"))
            {
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            }

            if (mat.HasProperty("_ZWrite"))
            {
                mat.SetInt("_ZWrite", 0);
            }

            if (configuredTransparentMode)
            {
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)RenderQueue.Transparent;
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_ALPHABLEND_ON");
            }
            else
            {
                mat.renderQueue = -1;
            }
        }

    }
}
