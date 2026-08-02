using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal readonly struct PoseCacheRenderContext
    {
        public readonly int ClipId;
        public readonly int AnimatorId;
        public readonly int TrackId;
        public readonly string ModelName;
        public readonly KimodoConstraintRigType RigType;
        public readonly Avatar SourceAvatar;
        public readonly string ContextKey;

        public PoseCacheRenderContext(
            int clipId,
            int animatorId,
            int trackId,
            string modelName,
            KimodoConstraintRigType rigType,
            Avatar sourceAvatar = null)
        {
            ClipId = clipId;
            AnimatorId = animatorId;
            TrackId = trackId;
            ModelName = string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim();
            RigType = rigType;
            SourceAvatar = sourceAvatar;
            ContextKey = KimodoConstraintMarkerEditorUtility.GetCachedIntString(clipId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(animatorId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(sourceAvatar != null ? sourceAvatar.GetInstanceID() : 0);
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

    internal static class KimodoConstraintSpaceConverter
    {
        internal static bool TryApplyToTargetAvatar(
            KimodoMarkerSampleResult sample,
            string modelName,
            float frameRate,
            SkeletonCache profileCache,
            SkeletonCache targetCache,
            out string error)
        {
            error = string.Empty;
            if (sample == null || profileCache == null || targetCache == null)
            {
                error = "Constraint target/profile Avatar cache is unavailable.";
                return false;
            }

            KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(profileCache);
            if (!KimodoRetargetAvatarUtility.TryApplyMarkerSampleToTransformMap(
                    sample,
                    modelName,
                    profileCache.skeletonRoot,
                    profileCache.uniqueNameMap,
                    out error) ||
                !KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    profileCache,
                    out MuscleSample profileSample,
                    out error) ||
                !KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                    profileSample,
                    frameRate,
                    targetCache,
                    out BoneSample targetSample,
                    out _,
                    out error))
            {
                return false;
            }

            return KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(
                targetSample,
                targetCache,
                out error);
        }

        internal static bool TryResolveHumanBonePair(
            SkeletonCache sourceCache,
            SkeletonCache targetCache,
            HumanBodyBones bone,
            out Transform sourceBone,
            out Transform targetBone)
        {
            sourceBone = bone != HumanBodyBones.LastBone
                ? KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(sourceCache, bone)
                : null;
            targetBone = bone != HumanBodyBones.LastBone
                ? KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(targetCache, bone)
                : null;
            return sourceBone != null && targetBone != null;
        }

        internal static bool TryMapHumanBonePoint(
            SkeletonCache sourceCache,
            SkeletonCache targetCache,
            HumanBodyBones bone,
            Vector3 sourcePoint,
            out Vector3 targetPoint)
        {
            targetPoint = Vector3.zero;
            if (!TryResolveHumanBonePair(
                    sourceCache,
                    targetCache,
                    bone,
                    out Transform sourceBone,
                    out Transform targetBone))
            {
                return false;
            }

            targetPoint = MapPoint(
                sourceBone,
                sourceCache.humanScale,
                targetBone,
                targetCache.humanScale,
                sourcePoint);
            return true;
        }

        internal static Vector3 MapPoint(
            Transform sourceBone,
            float sourceHumanScale,
            Transform targetBone,
            float targetHumanScale,
            Vector3 sourcePoint)
        {
            float scale = Mathf.Max(1e-6f, targetHumanScale) / Mathf.Max(1e-6f, sourceHumanScale);
            Vector3 sourceLocal = Quaternion.Inverse(sourceBone.rotation) * (sourcePoint - sourceBone.position);
            return targetBone.position + targetBone.rotation * (sourceLocal * scale);
        }
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
            public int TrackId;
            public KimodoConstraintRigType RigType;
            public Transform Root;
            public SkeletonCache TargetCache;
            public SkeletonCache ProfileCache;
            public List<Material> GeneratedMaterials;
            public GameObject EndEffectorMarker;
            public bool PickingEnabled;
            public bool HasRenderSignature;
            public int RenderSignature;
        }

        private static readonly Dictionary<string, PoseCacheEntry> Entries = new Dictionary<string, PoseCacheEntry>(StringComparer.Ordinal);
        private static bool invalidContextCleanupQueued;

        private const float NonConstraintAlpha = 1.0f;
        private const float HighlightAlpha = 1.0f;
        private static readonly Color NonConstraintColor = new Color(1f, 1f, 1f, NonConstraintAlpha);
        private static readonly Color HighlightColor = new Color(1f, 0f, 0f, HighlightAlpha);

        static KimodoConstraintPoseCache()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += DestroyAll;
            Selection.selectionChanged += ScheduleInvalidContextCleanup;
            Undo.undoRedoPerformed += ScheduleInvalidContextCleanup;
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
            bool changed = false;
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

                int renderSignature = ComputeRenderSignature(item, context.ModelName);
                if (!entry.HasRenderSignature || entry.RenderSignature != renderSignature)
                {
                    if (!ApplySampleToRig(item.SampleData, context.ModelName, entry, out error))
                    {
                        int localAxisCount = item.SampleData.localAxisAngles != null
                            ? item.SampleData.localAxisAngles.Count
                            : 0;
                        error = $"pose cache render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}, localAxisAngles={localAxisCount}): {error}";
                        return false;
                    }

                    var highlightedJoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectHighlightedJointsFromItem(item, context.ModelName, highlightedJoints);
                    ApplyConstraintColoring(entry, highlightedJoints);
                    UpdateEndEffectorMarker(entry, item.ConstraintType, item.SampleData);
                    entry.RenderSignature = renderSignature;
                    entry.HasRenderSignature = true;
                    changed = true;
                }

                changed |= SetEntryVisible(entry, true);
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
                    changed = true;
                }
            }

            if (keysToRemove != null)
            {
                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    Entries.Remove(keysToRemove[i]);
                }
            }
            if (changed)
            {
                SceneView.RepaintAll();
            }
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

        internal static bool HasAnyTransformChanges(PoseCacheRenderContext context, string entryId = null)
        {
            if (!TryGetEntryForContext(context, entryId, out PoseCacheEntry entry) || entry?.Root == null)
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

        internal static bool HasEndEffectorTargetTransformChanges(
            PoseCacheRenderContext context,
            string entryId)
        {
            return TryGetEntryForContext(context, entryId, out PoseCacheEntry entry) &&
                entry?.EndEffectorMarker != null &&
                entry.EndEffectorMarker.transform.hasChanged;
        }

        internal static void ClearTransformChanges(PoseCacheRenderContext context, string entryId = null)
        {
            if (!TryGetEntryForContext(context, entryId, out PoseCacheEntry entry) || entry?.Root == null)
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

        internal static bool TryGetRootBone(PoseCacheRenderContext context, string entryId, out Transform rootBone)
        {
            rootBone = null;
            if (!TryGetEntryForContext(context, entryId, out PoseCacheEntry entry) || entry?.Root == null)
            {
                return false;
            }

            if (entry.TargetCache?.animator != null)
            {
                rootBone = entry.TargetCache.animator.GetBoneTransform(HumanBodyBones.Hips);
                if (rootBone != null)
                {
                    return true;
                }
            }

            rootBone = entry.Root;
            return rootBone != null;
        }

        internal static bool TryGetEndEffectorTarget(
            PoseCacheRenderContext context,
            string entryId,
            out GameObject target)
        {
            target = null;
            if (!TryGetEntryForContext(context, entryId, out PoseCacheEntry entry) ||
                entry?.EndEffectorMarker == null)
            {
                return false;
            }

            target = entry.EndEffectorMarker;
            return true;
        }

        internal static bool TryUpdateEndEffectorTarget(
            PoseCacheRenderContext context,
            string entryId,
            string constraintType,
            KimodoMarkerSampleResult sample)
        {
            if (!TryGetEntryForContext(context, entryId, out PoseCacheEntry entry) ||
                entry?.EndEffectorMarker == null)
            {
                return false;
            }

            UpdateEndEffectorMarker(entry, constraintType, sample);
            SceneView.RepaintAll();
            return true;
        }

        internal static bool TryCaptureDragMuscleSamples(
            PoseCacheRenderContext context,
            string entryId,
            out MuscleSample virtualSkeleton,
            out float virtualSkeletonScale,
            out MuscleSample targetCharacter,
            out float targetCharacterScale,
            out string error)
        {
            virtualSkeleton = null;
            virtualSkeletonScale = 0f;
            targetCharacter = null;
            targetCharacterScale = 0f;
            error = string.Empty;
            if (!TryGetEntryForContext(context, entryId, out PoseCacheEntry entry) ||
                entry?.ProfileCache == null ||
                entry.TargetCache == null)
            {
                error = "pose cache context has no active profile/target entry.";
                return false;
            }

            bool profileWasActive = entry.ProfileCache.root.activeSelf;
            bool targetWasActive = entry.TargetCache.root.activeSelf;
            entry.ProfileCache.root.SetActive(true);
            entry.TargetCache.root.SetActive(true);
            try
            {
                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        entry.ProfileCache,
                        out virtualSkeleton,
                        out error) ||
                    !KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        entry.TargetCache,
                        out targetCharacter,
                        out error))
                {
                    return false;
                }

                virtualSkeletonScale = entry.ProfileCache.humanScale;
                targetCharacterScale = entry.TargetCache.humanScale;
                return true;
            }
            finally
            {
                entry.ProfileCache.root.SetActive(profileWasActive);
                entry.TargetCache.root.SetActive(targetWasActive);
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

            return TryBuildSampleFromTargetAvatar(
                entry,
                context.ModelName,
                markerType,
                sampleTime,
                out sample,
                out error);
        }

        internal static bool TryResolveTargetHipsPose(
            PoseCacheRenderContext context,
            KimodoMarkerSampleResult sample,
            out Vector3 position,
            out Quaternion rotation,
            out string error,
            Action<Animator, Transform> onPoseResolved = null)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            error = string.Empty;
            if (sample == null)
            {
                error = "Constraint anchor sample is null.";
                return false;
            }

            PoseCacheEntry transient = null;
            try
            {
                if (!KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                        context.ModelName,
                        context.ClipId,
                        context.AnimatorId,
                        context.SourceAvatar,
                        out KimodoConstraintPoseRigFactory.PoseRigInstance rig,
                        out error))
                {
                    return false;
                }

                transient = new PoseCacheEntry
                {
                    Root = rig.Root != null ? rig.Root.transform : null,
                    TargetCache = rig.TargetCache,
                    ProfileCache = rig.ProfileCache,
                    GeneratedMaterials = rig.GeneratedMaterials
                };
                if (!ApplySampleToRig(sample, context.ModelName, transient, out error))
                {
                    return false;
                }

                Transform hips = transient.TargetCache?.animator != null
                    ? transient.TargetCache.animator.GetBoneTransform(HumanBodyBones.Hips)
                    : null;
                if (hips == null)
                {
                    error = "Target Avatar Hips is unavailable.";
                    return false;
                }

                position = hips.position;
                rotation = hips.rotation;
                if (onPoseResolved != null)
                {
                    try
                    {
                        onPoseResolved(transient.TargetCache.animator, transient.TargetCache.skeletonRoot);
                    }
                    catch (Exception diagnosticException)
                    {
                        Debug.LogWarning(
                            "[Kimodo][TimelineFirstFrameConstraintDiag] target pose capture failed: " +
                            diagnosticException.Message);
                    }
                }
                return true;
            }
            finally
            {
                DestroyEntry(transient);
            }
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

        internal static bool IsClipStillOnTrack(int clipId, int trackId)
        {
            TrackAsset track = EditorUtility.InstanceIDToObject(trackId) as TrackAsset;
            if (clipId == 0 || track == null || track.timelineAsset == null)
            {
                return false;
            }

            foreach (TimelineClip timelineClip in track.GetClips())
            {
                UnityEngine.Object asset = timelineClip?.asset as UnityEngine.Object;
                if (asset != null && asset.GetInstanceID() == clipId)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ScheduleInvalidContextCleanup()
        {
            if (invalidContextCleanupQueued || Entries.Count == 0)
            {
                return;
            }

            invalidContextCleanupQueued = true;
            EditorApplication.delayCall += DestroyInvalidContexts;
        }

        private static void DestroyInvalidContexts()
        {
            invalidContextCleanupQueued = false;
            if (Entries.Count == 0)
            {
                return;
            }

            var keysToRemove = new List<string>();
            foreach (KeyValuePair<string, PoseCacheEntry> kv in Entries)
            {
                PoseCacheEntry entry = kv.Value;
                if (entry == null || !IsClipStillOnTrack(entry.ClipId, entry.TrackId))
                {
                    keysToRemove.Add(kv.Key);
                }
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
            if (!KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                    context.ModelName,
                    context.ClipId,
                    context.AnimatorId,
                    context.SourceAvatar,
                    out KimodoConstraintPoseRigFactory.PoseRigInstance rigInstance,
                    out error))
            {
                return false;
            }

            entry = new PoseCacheEntry
            {
                Key = key,
                ContextKey = contextKey,
                ClipId = context.ClipId,
                AnimatorId = context.AnimatorId,
                TrackId = context.TrackId,
                RigType = rigType,
                Root = rigInstance.Root != null ? rigInstance.Root.transform : null,
                TargetCache = rigInstance.TargetCache,
                ProfileCache = rigInstance.ProfileCache,
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

        private static bool TryGetEntryForContext(
            PoseCacheRenderContext context,
            string entryId,
            out PoseCacheEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return TryGetFirstEntryForContext(context, out entry);
            }

            string key = BuildEntryKey(context.ContextKey, entryId.Trim());
            return Entries.TryGetValue(key, out entry) && entry?.Root != null;
        }

        private static void DestroyEntry(PoseCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.EndEffectorMarker != null)
            {
                UnityEngine.Object.DestroyImmediate(entry.EndEffectorMarker);
                entry.EndEffectorMarker = null;
            }

            SkeletonCache targetCache = entry.TargetCache;
            entry.TargetCache = null;
            targetCache?.Dispose();
            SkeletonCache profileCache = entry.ProfileCache;
            entry.ProfileCache = null;
            profileCache?.Dispose();

            if (targetCache == null && entry.Root != null && entry.Root.gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(entry.Root.gameObject);
            }
            entry.Root = null;

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

        private static bool SetEntryVisible(PoseCacheEntry entry, bool visible)
        {
            if (entry?.Root == null || entry.Root.gameObject == null)
            {
                return false;
            }

            if (entry.Root.gameObject.activeSelf != visible)
            {
                entry.Root.gameObject.SetActive(visible);
                return true;
            }
            return false;
        }

        private static void SetEntrySelectable(PoseCacheEntry entry, bool selectable)
        {
            if (entry?.Root == null || entry.Root.gameObject == null)
            {
                return;
            }

            if (entry.PickingEnabled == selectable)
            {
                SetEndEffectorMarkerSelectable(entry, selectable);
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
            SetEndEffectorMarkerSelectable(entry, selectable);
        }

        private static void SetEndEffectorMarkerSelectable(PoseCacheEntry entry, bool selectable)
        {
            if (entry?.EndEffectorMarker == null)
            {
                return;
            }

            entry.EndEffectorMarker.hideFlags = selectable
                ? HideFlags.DontSave
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
                if (renderer == null || IsAuxiliaryTransform(entry, renderer.transform))
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

        internal static int ComputeRenderSignature(PoseCacheRenderItem item, string modelName)
        {
            unchecked
            {
                int hash = 17;
                AddHash(ref hash, modelName);
                AddHash(ref hash, item?.ConstraintType);
                AddHash(ref hash, item != null && item.Visible ? 1 : 0);
                KimodoMarkerSampleResult sample = item?.SampleData;
                if (sample != null)
                {
                    AddHash(ref hash, sample.constraintType);
                    AddHash(ref hash, sample.sampleTime.GetHashCode());
                    AddHash(ref hash, (int)sample.rigType);
                    AddHash(ref hash, sample.hasRootHeading ? 1 : 0);
                    AddHash(ref hash, sample.kimodoRootPosition.GetHashCode());
                    AddHash(ref hash, sample.rootHeading.GetHashCode());
                    AddHash(ref hash, sample.unityRootPos.GetHashCode());
                    AddHash(ref hash, sample.unityRootRot.GetHashCode());
                    AddHash(ref hash, sample.hasUnityHipsPose ? 1 : 0);
                    AddHash(ref hash, sample.unityHipsPos.GetHashCode());
                    AddHash(ref hash, sample.unityHipsRot.GetHashCode());
                    AddHash(ref hash, sample.hasEndEffectorTargetPosition ? 1 : 0);
                    AddHash(ref hash, sample.endEffectorTargetPositionRootLocal.GetHashCode());
                    AddHash(ref hash, sample.jointNames);
                    AddHash(ref hash, sample.localAxisAngles);
                    AddHash(ref hash, sample.sampledJointIndices);
                }
                AddHash(ref hash, item?.HighlightJoints);
                return hash;
            }
        }

        private static void AddHash(ref int hash, int value)
        {
            unchecked
            {
                hash = hash * 31 + value;
            }
        }

        private static void AddHash(ref int hash, string value)
        {
            AddHash(ref hash, StringComparer.Ordinal.GetHashCode(value ?? string.Empty));
        }

        private static void AddHash(ref int hash, IReadOnlyList<string> values)
        {
            int count = values != null ? values.Count : 0;
            AddHash(ref hash, count);
            for (int i = 0; i < count; i++)
            {
                AddHash(ref hash, values[i]);
            }
        }

        private static void AddHash(ref int hash, IReadOnlyList<Vector3> values)
        {
            int count = values != null ? values.Count : 0;
            AddHash(ref hash, count);
            for (int i = 0; i < count; i++)
            {
                AddHash(ref hash, values[i].GetHashCode());
            }
        }

        private static void AddHash(ref int hash, IReadOnlyList<int> values)
        {
            int count = values != null ? values.Count : 0;
            AddHash(ref hash, count);
            for (int i = 0; i < count; i++)
            {
                AddHash(ref hash, values[i]);
            }
        }

        private static bool ApplySampleToRig(KimodoMarkerSampleResult sample, string modelName, PoseCacheEntry entry, out string error)
        {
            error = string.Empty;
            if (sample == null || entry?.TargetCache == null || entry.ProfileCache == null)
            {
                error = "Constraint target/profile Avatar cache is unavailable.";
                return false;
            }

            bool wasActive = entry.TargetCache.root.activeSelf;
            entry.TargetCache.root.SetActive(true);
            try
            {
                return KimodoConstraintSpaceConverter.TryApplyToTargetAvatar(
                    sample,
                    modelName,
                    ResolveRetargetFrameRate(modelName),
                    entry.ProfileCache,
                    entry.TargetCache,
                    out error);
            }
            finally
            {
                if (entry.TargetCache.animator != null)
                {
                    entry.TargetCache.animator.enabled = false;
                }
                entry.TargetCache.root.SetActive(wasActive);
            }
        }

        private static bool TryBuildSampleFromTargetAvatar(
            PoseCacheEntry entry,
            string modelName,
            string markerType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (entry?.TargetCache == null || entry.ProfileCache == null)
            {
                error = "Constraint target/profile Avatar cache is unavailable.";
                return false;
            }

            bool wasActive = entry.TargetCache.root.activeSelf;
            entry.TargetCache.root.SetActive(true);
            try
            {
                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        entry.TargetCache,
                        out MuscleSample targetSample,
                        out error))
                {
                    return false;
                }

                KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(entry.ProfileCache);
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        targetSample,
                        ResolveRetargetFrameRate(modelName),
                        entry.ProfileCache,
                        out BoneSample profileSample,
                        out _,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        profileSample,
                        entry.ProfileCache,
                        modelName,
                        markerType,
                        sampleTime,
                        out sample,
                        out error))
                {
                    return false;
                }

                sample.unityRootPos = entry.TargetCache.skeletonRoot.position;
                sample.unityRootRot = entry.TargetCache.skeletonRoot.rotation;
                CaptureEndEffectorTargetPosition(entry, markerType, sample);
                return true;
            }
            finally
            {
                entry.TargetCache.root.SetActive(wasActive);
            }
        }

        private static float ResolveRetargetFrameRate(string modelName)
        {
            return KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile)
                ? Mathf.Max(1f, profile.SourceFps)
                : KimodoPlayableClip.FIXED_FRAME_RATE;
        }

        private static void UpdateEndEffectorMarker(
            PoseCacheEntry entry,
            string constraintType,
            KimodoMarkerSampleResult sample)
        {
            HumanBodyBones bone = ResolveEndEffectorBone(constraintType);
            if (!KimodoConstraintSpaceConverter.TryResolveHumanBonePair(
                    entry?.ProfileCache,
                    entry?.TargetCache,
                    bone,
                    out _,
                    out Transform target))
            {
                if (entry?.EndEffectorMarker != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.EndEffectorMarker);
                    entry.EndEffectorMarker = null;
                }
                return;
            }

            if (entry.EndEffectorMarker == null)
            {
                entry.EndEffectorMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                entry.EndEffectorMarker.name = "__KimodoEndConstraintTarget";
                entry.EndEffectorMarker.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
                Collider collider = entry.EndEffectorMarker.GetComponent<Collider>();
                if (collider != null)
                {
                    UnityEngine.Object.DestroyImmediate(collider);
                }

                Renderer renderer = entry.EndEffectorMarker.GetComponent<Renderer>();
                Material material = CreateEndEffectorMaterial();
                if (renderer != null && material != null)
                {
                    renderer.sharedMaterial = material;
                    entry.GeneratedMaterials ??= new List<Material>();
                    entry.GeneratedMaterials.Add(material);
                }
            }

            Transform marker = entry.EndEffectorMarker.transform;
            if (TryResolveTargetAvatarPoint(entry, bone, sample, out Vector3 targetPosition))
            {
                marker.SetParent(entry.Root, true);
                marker.position = targetPosition;
                marker.rotation = target.rotation;
            }
            else
            {
                marker.SetParent(target, false);
                marker.localPosition = Vector3.zero;
                marker.localRotation = Quaternion.identity;
            }

            Vector3 scale = marker.parent != null ? marker.parent.lossyScale : Vector3.one;
            marker.localScale = new Vector3(
                0.1f / Mathf.Max(1e-6f, Mathf.Abs(scale.x)),
                0.1f / Mathf.Max(1e-6f, Mathf.Abs(scale.y)),
                0.1f / Mathf.Max(1e-6f, Mathf.Abs(scale.z)));
            SetEndEffectorMarkerSelectable(entry, entry.PickingEnabled);
            entry.EndEffectorMarker.SetActive(true);
        }

        private static void CaptureEndEffectorTargetPosition(
            PoseCacheEntry entry,
            string constraintType,
            KimodoMarkerSampleResult sample)
        {
            HumanBodyBones bone = ResolveEndEffectorBone(constraintType);
            if (bone == HumanBodyBones.LastBone ||
                entry?.EndEffectorMarker == null ||
                sample == null)
            {
                return;
            }

            if (!TryResolveProfileAvatarPoint(
                    entry,
                    bone,
                    entry.EndEffectorMarker.transform.position,
                    out Vector3 profilePoint))
            {
                return;
            }

            Quaternion rootRotation = ResolveSampleRootRotation(sample);
            sample.hasEndEffectorTargetPosition = true;
            sample.endEffectorTargetPositionRootLocal = Quaternion.Inverse(rootRotation) *
                (profilePoint - sample.kimodoRootPosition);
        }

        private static bool TryResolveTargetAvatarPoint(
            PoseCacheEntry entry,
            HumanBodyBones bone,
            KimodoMarkerSampleResult sample,
            out Vector3 position)
        {
            position = Vector3.zero;
            if (sample == null ||
                !sample.hasEndEffectorTargetPosition ||
                !KimodoConstraintSpaceConverter.TryMapHumanBonePoint(
                    entry?.ProfileCache,
                    entry?.TargetCache,
                    bone,
                    sample.kimodoRootPosition +
                        ResolveSampleRootRotation(sample) * sample.endEffectorTargetPositionRootLocal,
                    out position))
            {
                return false;
            }
            return true;
        }

        private static bool TryResolveProfileAvatarPoint(
            PoseCacheEntry entry,
            HumanBodyBones bone,
            Vector3 targetPoint,
            out Vector3 position)
        {
            position = Vector3.zero;
            return KimodoConstraintSpaceConverter.TryMapHumanBonePoint(
                    entry?.TargetCache,
                    entry?.ProfileCache,
                    bone,
                    targetPoint,
                    out position);
        }

        private static Quaternion ResolveSampleRootRotation(KimodoMarkerSampleResult sample)
        {
            if (sample?.localAxisAngles == null || sample.localAxisAngles.Count == 0)
            {
                return Quaternion.identity;
            }

            Vector3 axisAngle = sample.localAxisAngles[0];
            float radians = axisAngle.magnitude;
            return radians <= 1e-8f
                ? Quaternion.identity
                : Quaternion.AngleAxis(radians * Mathf.Rad2Deg, axisAngle / radians);
        }

        private static HumanBodyBones ResolveEndEffectorBone(string constraintType)
        {
            switch ((constraintType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "left-hand":
                    return HumanBodyBones.LeftHand;
                case "right-hand":
                    return HumanBodyBones.RightHand;
                case "left-foot":
                    return HumanBodyBones.LeftFoot;
                case "right-foot":
                    return HumanBodyBones.RightFoot;
                default:
                    return HumanBodyBones.LastBone;
            }
        }

        private static bool IsAuxiliaryTransform(PoseCacheEntry entry, Transform transform)
        {
            Transform marker = entry?.EndEffectorMarker != null
                ? entry.EndEffectorMarker.transform
                : null;
            return marker != null && (transform == marker || transform.IsChildOf(marker));
        }

        private static Material CreateEndEffectorMaterial()
        {
            Shader shader = Shader.Find("HDRP/Lit") ??
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("Standard");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "__KimodoEndConstraintRed"
            };
            SetMaterialColor(material, Color.red, 1f);
            return material;
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
