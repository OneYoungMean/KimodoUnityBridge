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
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(trackId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(KimodoUnityObjectIdUtility.IdHash(sourceAvatar));
        }
    }

    internal sealed class PoseCacheRenderItem
    {
        public string EntryId;
        public KimodoMarkerSampleResult SampleData;
        public string ConstraintType;
        public List<string> HighlightJoints;
        public Color PreviewColor = Color.white;
        public bool Visible = true;
    }

    internal sealed class ConstraintPosePreviewEntry
    {
        public string Key;
        public Transform Root;
        public SkeletonCache TargetCache;
        public SkeletonCache ProfileCache;
        public List<Material> GeneratedMaterials;
        public GameObject EndEffectorMarker;
        public bool PickingEnabled;
        public bool HasRenderSignature;
        public int RenderSignature;
    }

    internal sealed class ConstraintPosePreviewSession : IDisposable
    {
        internal readonly Dictionary<string, ConstraintPosePreviewEntry> Entries =
            new Dictionary<string, ConstraintPosePreviewEntry>(StringComparer.Ordinal);

        internal PoseCacheRenderContext Context { get; }
        internal bool IsDisposed { get; private set; }

        internal ConstraintPosePreviewSession(PoseCacheRenderContext context)
        {
            Context = context;
        }

        internal bool Matches(PoseCacheRenderContext context)
        {
            return !IsDisposed &&
                string.Equals(Context.ContextKey, context.ContextKey, StringComparison.Ordinal) &&
                string.Equals(Context.ModelName, context.ModelName, StringComparison.Ordinal) &&
                Context.RigType == context.RigType;
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                KimodoConstraintPoseCache.ReleaseSession(this);
            }
        }

        internal void MarkDisposed()
        {
            IsDisposed = true;
        }
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

            if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(
                    targetSample,
                    targetCache,
                    out error))
            {
                return false;
            }

            ApplyRoot2DHeadingToPreviewRoot(sample, targetCache.skeletonRoot);
            return true;
        }

        internal static void ApplyRoot2DHeadingToPreviewRoot(
            KimodoMarkerSampleResult sample,
            Transform previewRoot)
        {
            if (previewRoot == null ||
                sample == null ||
                !string.Equals(sample.constraintType, "root2d", StringComparison.OrdinalIgnoreCase) ||
                !sample.hasRootHeading)
            {
                return;
            }

            Vector3 forward = new Vector3(sample.rootHeading.x, 0f, sample.rootHeading.y);
            if (forward.sqrMagnitude > 1e-8f)
            {
                previewRoot.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            }
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
        private static readonly Dictionary<string, ConstraintPosePreviewSession> Sessions =
            new Dictionary<string, ConstraintPosePreviewSession>(StringComparer.Ordinal);
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

        internal static bool TryGetOrCreateSession(
            PoseCacheRenderContext context,
            out ConstraintPosePreviewSession session,
            out string error)
        {
            session = null;
            error = string.Empty;
            if (context.ClipId == 0 || context.AnimatorId == 0 || context.TrackId == 0)
            {
                error = "invalid clip/animator/track context";
                return false;
            }

            if (Sessions.TryGetValue(context.ContextKey, out ConstraintPosePreviewSession existing))
            {
                if (existing != null && existing.Matches(context))
                {
                    session = existing;
                    return true;
                }

                DestroySession(existing, repaint: false);
            }

            session = new ConstraintPosePreviewSession(context);
            Sessions[context.ContextKey] = session;
            return true;
        }

        internal static void ReleaseSession(ConstraintPosePreviewSession session)
        {
            DestroySession(session, repaint: true);
        }

        private static bool TryGetSession(
            PoseCacheRenderContext context,
            out ConstraintPosePreviewSession session)
        {
            if (Sessions.TryGetValue(context.ContextKey, out session))
            {
                if (session != null && session.Matches(context))
                {
                    return true;
                }

                DestroySession(session, repaint: false);
            }

            session = null;
            return false;
        }

        internal static bool RenderBatch(
            PoseCacheRenderContext context,
            IReadOnlyList<PoseCacheRenderItem> items,
            out string error,
            string entryPrefix = null)
        {
            error = string.Empty;
            if (!TryGetOrCreateSession(context, out ConstraintPosePreviewSession session, out error))
            {
                return false;
            }

            Dictionary<string, ConstraintPosePreviewEntry> entries = session.Entries;
            string normalizedPrefix = entryPrefix ?? string.Empty;
            if (items == null || items.Count == 0)
            {
                DestroyEntriesInScope(context, normalizedPrefix);
                return true;
            }

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
                DestroyEntriesInScope(context, normalizedPrefix);
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

                string entryId = normalizedPrefix +
                    (string.IsNullOrWhiteSpace(item.EntryId) ? $"item_{i}" : item.EntryId.Trim());
                desiredKeys.Add(entryId);

                if (!TryGetOrCreateEntry(session, entryId, out ConstraintPosePreviewEntry entry, out error))
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
                    ApplyConstraintColoring(entry, highlightedJoints, item.PreviewColor);
                    UpdateEndEffectorMarker(entry, item.ConstraintType, item.SampleData);
                    entry.RenderSignature = renderSignature;
                    entry.HasRenderSignature = true;
                    changed = true;
                }

                changed |= SetEntryVisible(entry, true);
            }

            List<string> keysToRemove = null;
            foreach (KeyValuePair<string, ConstraintPosePreviewEntry> kv in entries)
            {
                if (!IsEntryInScope(kv.Value, normalizedPrefix))
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
                    entries.Remove(keysToRemove[i]);
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
            if (!TryGetSession(context, out ConstraintPosePreviewSession session))
            {
                return;
            }

            foreach (KeyValuePair<string, ConstraintPosePreviewEntry> kv in session.Entries)
            {
                ApplyEntryState(kv.Value, visible, selectable);
            }

            SceneView.RepaintAll();
        }

        internal static bool HasAnyTransformChanges(PoseCacheRenderContext context, string entryId = null)
        {
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
                entry?.Root == null)
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

        internal static void ClearTransformChanges(PoseCacheRenderContext context, string entryId = null)
        {
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
                entry?.Root == null)
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
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
                entry?.Root == null)
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

        internal static bool TryGetEndEffectorBone(
            PoseCacheRenderContext context,
            string entryId,
            string constraintType,
            out Transform bone)
        {
            bone = null;
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
                entry?.TargetCache == null)
            {
                return false;
            }

            HumanBodyBones humanBone = ResolveEndEffectorBone(constraintType);
            bone = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(entry.TargetCache, humanBone);
            return bone != null;
        }

        internal static bool IsNonRootPoseTransform(
            PoseCacheRenderContext context,
            string entryId,
            Transform transform)
        {
            if (transform == null ||
                !TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
                entry?.Root == null ||
                (transform != entry.Root && !transform.IsChildOf(entry.Root)) ||
                IsAuxiliaryTransform(entry, transform))
            {
                return false;
            }

            Transform hips = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(
                entry.TargetCache,
                HumanBodyBones.Hips);
            return transform != entry.Root && transform != hips;
        }

        internal static void RestoreNonRootBoneTranslations(
            PoseCacheRenderContext context,
            string entryId)
        {
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
                entry?.Root == null ||
                entry.TargetCache?.boneTransforms == null ||
                entry.TargetCache.bindLocalPositions == null)
            {
                return;
            }

            Transform hips = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(
                entry.TargetCache,
                HumanBodyBones.Hips);
            Transform[] bones = entry.TargetCache.boneTransforms;
            Vector3[] bindPositions = entry.TargetCache.bindLocalPositions;
            int count = Mathf.Min(bones.Length, bindPositions.Length);
            for (int i = 0; i < count; i++)
            {
                Transform bone = bones[i];
                if (bone == null || bone == entry.Root || bone == hips)
                {
                    continue;
                }

                bone.localPosition = bindPositions[i];
            }
        }

        internal static bool TryGetPreviewRoot(PoseCacheRenderContext context, string entryId, out Transform root)
        {
            root = null;
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
                entry?.Root == null)
            {
                return false;
            }

            root = entry.Root;
            return true;
        }

        internal static bool TryGetEndEffectorTarget(
            PoseCacheRenderContext context,
            string entryId,
            out GameObject target)
        {
            target = null;
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
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
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
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
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) ||
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

            if (!TryGetSession(context, out ConstraintPosePreviewSession session))
            {
                error = "pose cache context has no active session.";
                return false;
            }

            ConstraintPosePreviewEntry entry;
            if (!string.IsNullOrWhiteSpace(entryId))
            {
                session.Entries.TryGetValue(entryId.Trim(), out entry);
            }
            else
            {
                TryGetFirstEntryForContext(session, out entry);
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

            ConstraintPosePreviewEntry transient = null;
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

                transient = new ConstraintPosePreviewEntry
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
            if (string.IsNullOrWhiteSpace(entryId) ||
                !TryGetSession(context, out ConstraintPosePreviewSession session))
            {
                return;
            }

            string key = entryId.Trim();
            if (!session.Entries.TryGetValue(key, out ConstraintPosePreviewEntry entry))
            {
                return;
            }

            DestroyEntry(entry);
            session.Entries.Remove(key);
            SceneView.RepaintAll();
        }

        internal static void DestroyEntriesForItemId(string entryId, PoseCacheRenderContext? keepContext = null)
        {
            if (string.IsNullOrWhiteSpace(entryId) || Sessions.Count == 0)
            {
                return;
            }

            string normalizedEntryId = entryId.Trim();
            string keepContextKey = keepContext.HasValue
                ? keepContext.Value.ContextKey
                : null;
            bool changed = false;
            string prefixedEntryIdSuffix = ":" + normalizedEntryId;

            foreach (ConstraintPosePreviewSession session in Sessions.Values)
            {
                if (session == null ||
                    (!string.IsNullOrEmpty(keepContextKey) &&
                        string.Equals(session.Context.ContextKey, keepContextKey, StringComparison.Ordinal)))
                {
                    continue;
                }

                List<string> keysToRemove = null;
                foreach (KeyValuePair<string, ConstraintPosePreviewEntry> kv in session.Entries)
                {
                    if (string.Equals(kv.Key, normalizedEntryId, StringComparison.Ordinal) ||
                        kv.Key.EndsWith(prefixedEntryIdSuffix, StringComparison.Ordinal))
                    {
                        keysToRemove ??= new List<string>();
                        keysToRemove.Add(kv.Key);
                    }
                }

                if (keysToRemove == null)
                {
                    continue;
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    string key = keysToRemove[i];
                    if (session.Entries.TryGetValue(key, out ConstraintPosePreviewEntry entry))
                    {
                        DestroyEntry(entry);
                        session.Entries.Remove(key);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                SceneView.RepaintAll();
            }
        }

        internal static void DestroyEntriesForClipId(int clipId, PoseCacheRenderContext? keepContext = null)
        {
            if (clipId == 0 || Sessions.Count == 0)
            {
                return;
            }

            string keepContextKey = keepContext.HasValue
                ? keepContext.Value.ContextKey
                : null;
            var sessionsToRemove = new List<ConstraintPosePreviewSession>();

            foreach (ConstraintPosePreviewSession session in Sessions.Values)
            {
                if (session == null || session.Context.ClipId != clipId)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(keepContextKey) &&
                    string.Equals(session.Context.ContextKey, keepContextKey, StringComparison.Ordinal))
                {
                    continue;
                }

                sessionsToRemove.Add(session);
            }

            for (int i = 0; i < sessionsToRemove.Count; i++)
            {
                DestroySession(sessionsToRemove[i], repaint: i == sessionsToRemove.Count - 1);
            }
        }

        internal static void DestroyContext(PoseCacheRenderContext context)
        {
            if (!Sessions.TryGetValue(context.ContextKey, out ConstraintPosePreviewSession session))
            {
                return;
            }

            DestroySession(session, repaint: true);
        }

        internal static void DestroyAll()
        {
            var sessions = new List<ConstraintPosePreviewSession>(Sessions.Values);
            Sessions.Clear();
            for (int i = 0; i < sessions.Count; i++)
            {
                DestroySessionEntries(sessions[i]);
                sessions[i]?.MarkDisposed();
            }

            SceneView.RepaintAll();
        }

        private static void DestroySession(ConstraintPosePreviewSession session, bool repaint)
        {
            if (session == null)
            {
                return;
            }

            if (Sessions.TryGetValue(session.Context.ContextKey, out ConstraintPosePreviewSession active) &&
                ReferenceEquals(active, session))
            {
                Sessions.Remove(session.Context.ContextKey);
            }

            DestroySessionEntries(session);
            session.MarkDisposed();
            if (repaint)
            {
                SceneView.RepaintAll();
            }
        }

        private static void DestroySessionEntries(ConstraintPosePreviewSession session)
        {
            if (session == null)
            {
                return;
            }

            foreach (ConstraintPosePreviewEntry entry in session.Entries.Values)
            {
                DestroyEntry(entry);
            }

            session.Entries.Clear();
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
                if (asset != null && KimodoUnityObjectIdUtility.IdHash(asset) == clipId)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ScheduleInvalidContextCleanup()
        {
            if (invalidContextCleanupQueued || Sessions.Count == 0)
            {
                return;
            }

            invalidContextCleanupQueued = true;
            EditorApplication.delayCall += DestroyInvalidContexts;
        }

        internal static void DestroyInvalidContexts()
        {
            invalidContextCleanupQueued = false;
            if (Sessions.Count == 0)
            {
                return;
            }

            var sessionsToRemove = new List<ConstraintPosePreviewSession>();
            foreach (ConstraintPosePreviewSession session in Sessions.Values)
            {
                if (session == null ||
                    !IsClipStillOnTrack(session.Context.ClipId, session.Context.TrackId))
                {
                    sessionsToRemove.Add(session);
                }
            }

            for (int i = 0; i < sessionsToRemove.Count; i++)
            {
                DestroySession(sessionsToRemove[i], repaint: i == sessionsToRemove.Count - 1);
            }
        }

        internal static void DestroyEntriesInScope(PoseCacheRenderContext context, string entryPrefix)
        {
            string normalizedPrefix = entryPrefix ?? string.Empty;
            if (!TryGetSession(context, out ConstraintPosePreviewSession session))
            {
                return;
            }

            var keysToRemove = new List<string>();
            foreach (KeyValuePair<string, ConstraintPosePreviewEntry> kv in session.Entries)
            {
                if (IsEntryInScope(kv.Value, normalizedPrefix))
                {
                    keysToRemove.Add(kv.Key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                string key = keysToRemove[i];
                if (session.Entries.TryGetValue(key, out ConstraintPosePreviewEntry entry))
                {
                    DestroyEntry(entry);
                    session.Entries.Remove(key);
                }
            }

            if (keysToRemove.Count > 0)
            {
                SceneView.RepaintAll();
            }
        }

        private static bool IsEntryInScope(ConstraintPosePreviewEntry entry, string entryPrefix)
        {
            if (entry == null || string.IsNullOrEmpty(entryPrefix))
            {
                return entry != null;
            }

            return entry.Key != null && entry.Key.StartsWith(entryPrefix, StringComparison.Ordinal);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange _)
        {
            DestroyAll();
        }

        private static bool TryGetOrCreateEntry(
            ConstraintPosePreviewSession session,
            string entryId,
            out ConstraintPosePreviewEntry entry,
            out string error)
        {
            entry = null;
            error = string.Empty;
            PoseCacheRenderContext context = session.Context;
            if (context.ClipId == 0 || context.AnimatorId == 0)
            {
                error = "invalid clip/animator id";
                return false;
            }

            string normalizedEntryId = string.IsNullOrWhiteSpace(entryId) ? "default" : entryId.Trim();
            if (session.Entries.TryGetValue(normalizedEntryId, out entry) &&
                entry != null &&
                entry.Root != null &&
                entry.Root.gameObject != null)
            {
                return true;
            }

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

            entry = new ConstraintPosePreviewEntry
            {
                Key = normalizedEntryId,
                Root = rigInstance.Root != null ? rigInstance.Root.transform : null,
                TargetCache = rigInstance.TargetCache,
                ProfileCache = rigInstance.ProfileCache,
                GeneratedMaterials = rigInstance.GeneratedMaterials,
                PickingEnabled = false
            };

            session.Entries[normalizedEntryId] = entry;
            SetEntrySelectable(entry, false);
            return true;
        }

        private static bool TryGetFirstEntryForContext(
            ConstraintPosePreviewSession session,
            out ConstraintPosePreviewEntry entry)
        {
            entry = null;
            foreach (KeyValuePair<string, ConstraintPosePreviewEntry> kv in session.Entries)
            {
                if (kv.Value != null && kv.Value.Root != null)
                {
                    entry = kv.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetEntryForContext(
            ConstraintPosePreviewSession session,
            string entryId,
            out ConstraintPosePreviewEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return TryGetFirstEntryForContext(session, out entry);
            }

            return session.Entries.TryGetValue(entryId.Trim(), out entry) && entry?.Root != null;
        }

        private static void DestroyEntry(ConstraintPosePreviewEntry entry)
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

        private static bool SetEntryVisible(ConstraintPosePreviewEntry entry, bool visible)
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

        private static void SetEntrySelectable(ConstraintPosePreviewEntry entry, bool selectable)
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
                ? HideFlags.DontSave
                : HideFlags.HideInHierarchy | HideFlags.DontSave;
            SetEndEffectorMarkerSelectable(entry, selectable);
        }

        private static void SetEndEffectorMarkerSelectable(ConstraintPosePreviewEntry entry, bool selectable)
        {
            if (entry?.EndEffectorMarker == null)
            {
                return;
            }

            entry.EndEffectorMarker.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
        }

        private static void ApplyEntryState(ConstraintPosePreviewEntry entry, bool visible, bool selectable)
        {
            if (entry == null)
            {
                return;
            }

            SetEntryVisible(entry, visible);
            SetEntrySelectable(entry, selectable);
        }

        private static void ApplyConstraintColoring(
            ConstraintPosePreviewEntry entry,
            HashSet<string> highlightedJoints,
            Color previewColor)
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
                        SetMaterialColor(
                            mat,
                            previewColor == default ? NonConstraintColor : previewColor,
                            NonConstraintAlpha);
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
                AddHash(ref hash, item != null ? item.PreviewColor.GetHashCode() : 0);
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

        private static bool ApplySampleToRig(KimodoMarkerSampleResult sample, string modelName, ConstraintPosePreviewEntry entry, out string error)
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
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
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
            ConstraintPosePreviewEntry entry,
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
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
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

        private static void UpdateEndEffectorMarker(
            ConstraintPosePreviewEntry entry,
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
            ConstraintPosePreviewEntry entry,
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
            ConstraintPosePreviewEntry entry,
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
            ConstraintPosePreviewEntry entry,
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

            return KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(sample.localAxisAngles[0]);
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

        private static bool IsAuxiliaryTransform(ConstraintPosePreviewEntry entry, Transform transform)
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
