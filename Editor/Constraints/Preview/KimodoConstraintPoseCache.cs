using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
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
        public KimodoConstraintMarker SourceMarker;
    }

    internal sealed class ConstraintPosePreviewEntry
    {
        public string Key;
        public Transform Root;
        public SkeletonCache TargetCache;
        public SkeletonCache ProfileCache;
        public List<Material> GeneratedMaterials;
        public GameObject EndEffectorMarker;
        public Dictionary<HumanBodyBones, GameObject> FullBodyTargets;
        public KimodoConstraintMarker SourceMarker;
        public KimodoMarkerSampleResult BaseSample;
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

            if (sample.characterPose != null)
            {
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample.mask, sample.constraintType);
                if (!sample.characterPose.TryValidate(out error))
                {
                    return false;
                }
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        CharacterPoseMuscleAdapter.ToMuscleSample(sample.characterPose),
                        frameRate,
                        targetCache,
                        out BoneSample canonicalTargetSample,
                        out _,
                        out error,
                        solveLeftHandIk: mask.leftHand,
                        solveRightHandIk: mask.rightHand,
                        applyFootIk: mask.leftFoot || mask.rightFoot) ||
                    !KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(
                        canonicalTargetSample,
                        targetCache,
                        out error))
                {
                    return false;
                }

                ApplyRoot2DHeadingToPreviewRoot(sample, targetCache.skeletonRoot);
                return true;
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
                    out error,
                    solveLeftHandIk: KimodoConstraintMask.Resolve(sample.mask, sample.constraintType).leftHand,
                    solveRightHandIk: KimodoConstraintMask.Resolve(sample.mask, sample.constraintType).rightHand,
                    applyFootIk: KimodoConstraintMask.Resolve(sample.mask, sample.constraintType).leftFoot ||
                        KimodoConstraintMask.Resolve(sample.mask, sample.constraintType).rightFoot))
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
            bool isRoot2D = string.Equals(sample?.constraintType, "root2d", StringComparison.OrdinalIgnoreCase);
            bool isUnifiedRoot = string.Equals(sample?.constraintType, "constraint", StringComparison.OrdinalIgnoreCase) &&
                KimodoConstraintMask.Resolve(sample?.mask, sample?.constraintType).rootPosition;
            if (previewRoot == null || sample == null || (!isRoot2D && !isUnifiedRoot))
            {
                return;
            }

            // CharacterPose is canonical: applying its root transform a second
            // time after retargeting causes Root2D drag-back.
            // There is no compatibility fallback.
            if (sample.characterPose == null || !sample.characterPose.TryValidate(out _))
            {
                return;
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
        private static readonly Color FullBodyRootColor = new Color(0.78f, 0.78f, 0.78f);
        private static readonly Color LeftTargetColor = new Color(0.18f, 0.48f, 0.96f);
        private static readonly Color RightTargetColor = new Color(0.94f, 0.22f, 0.22f);
        private const float EndEffectorTargetSize = 0.05f;
        private static readonly HumanBodyBones[] FullBodyTargetBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot
        };

        static KimodoConstraintPoseCache()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += DestroyAll;
            Selection.selectionChanged += ScheduleInvalidContextCleanup;
            Undo.undoRedoPerformed += ScheduleInvalidContextCleanup;
            SceneView.duringSceneGui += DrawControllerHandles;
        }

        private static void DrawControllerHandles(SceneView _)
        {
            foreach (ConstraintPosePreviewSession session in Sessions.Values)
            {
                if (session?.IsDisposed == true) continue;
                foreach (ConstraintPosePreviewEntry entry in session.Entries.Values)
                {
                    if (entry?.FullBodyTargets == null) continue;
                    foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
                    {
                        DrawTargetHandle(entry, item.Key, item.Value);
                    }
                }
            }
        }

        private static void DrawTargetHandle(ConstraintPosePreviewEntry entry, HumanBodyBones bone, GameObject target)
        {
            if (target == null || !target.activeInHierarchy) return;
            Transform transform = target.transform;
            Vector3 position = transform.position;
            float size = HandleUtility.GetHandleSize(position) * (bone == HumanBodyBones.Hips ? 0.14f : 0.09f);
            Handles.color = bone == HumanBodyBones.Hips ? FullBodyRootColor : TargetColor(bone);
            Handles.CapFunction cap = bone == HumanBodyBones.Hips || bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand
                ? Handles.SphereHandleCap : Handles.CubeHandleCap;
            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(entry.SourceMarker);
            bool editable = windowOpen && entry.SourceMarker != null &&
                (bone != HumanBodyBones.Hips || !entry.SourceMarker.autoSampleFullBody);
            if (!windowOpen)
            {
                if (Handles.Button(position, transform.rotation, size, size * 1.25f, cap)) OpenHandleEditor(entry);
            }
            else if (editable)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(position, size, Vector3.zero, cap);
                Quaternion rotated = Handles.RotationHandle(transform.rotation, moved);
                if (EditorGUI.EndChangeCheck()) transform.SetPositionAndRotation(moved, rotated);
            }
            else
            {
                cap(0, position, transform.rotation, size, EventType.Repaint);
            }
            Handles.Label(position + Vector3.up * size, bone == HumanBodyBones.Hips ? "Root Position / Rotation" : bone.ToString());
        }

        private static void OpenHandleEditor(ConstraintPosePreviewEntry entry)
        {
            if (entry?.SourceMarker == null || !entry.SourceMarker.constraintEnabled) return;
            Selection.activeObject = entry.SourceMarker;
            EditorApplication.delayCall += () =>
            {
                if (entry.SourceMarker != null) KimodoConstraintOverrideEditWindow.ShowWindow(entry.SourceMarker);
            };
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

                entry.SourceMarker = item.SourceMarker;

                int renderSignature = ComputeRenderSignature(item, context.ModelName);
                if (!entry.HasRenderSignature || entry.RenderSignature != renderSignature)
                {
                    entry.BaseSample = item.SampleData.Clone();
                    KimodoMarkerSampleResult renderSample =
                        KimodoConstraintSampleResolver.ResolveUnifiedSample(item.SampleData);
                    if (!ApplySampleToRig(renderSample, context.ModelName, entry, out error))
                    {
                        error = $"pose cache render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}): {error}";
                        return false;
                    }

                    var highlightedJoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectHighlightedJointsFromItem(item, context.ModelName, highlightedJoints);
                    ApplyConstraintColoring(entry, highlightedJoints, item.PreviewColor);
                    UpdateEndEffectorMarker(entry, item.ConstraintType, renderSample);
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
                if (t != null && t.hasChanged && !IsAuxiliaryTransform(entry, t))
                {
                    return true;
                }
            }

            if (entry.EndEffectorMarker != null && entry.EndEffectorMarker.transform.hasChanged)
            {
                return true;
            }
            return HasFullBodyTargetTransformChanges(entry);
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

            if (entry.EndEffectorMarker != null)
            {
                entry.EndEffectorMarker.transform.hasChanged = false;
            }
            ClearFullBodyTargetTransformChanges(entry);
        }

        internal static bool HasEndEffectorTargetTransformChanges(
            PoseCacheRenderContext context,
            string entryId)
        {
            return TryGetSession(context, out ConstraintPosePreviewSession session) &&
                TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) &&
                ((entry?.EndEffectorMarker != null && entry.EndEffectorMarker.transform.hasChanged) ||
                 HasFullBodyTargetTransformChanges(entry));
        }

        internal static bool HasIkTargetTransformChanges(
            PoseCacheRenderContext context,
            string entryId)
        {
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry))
            {
                return false;
            }
            if (entry?.EndEffectorMarker != null && entry.EndEffectorMarker.transform.hasChanged) return true;
            if (entry?.FullBodyTargets == null) return false;
            foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
            {
                if (item.Key != HumanBodyBones.Hips &&
                    IsTargetEnabled(KimodoConstraintMask.Resolve(entry.BaseSample?.mask, entry.BaseSample?.constraintType), item.Key) &&
                    item.Value != null && item.Value.transform.hasChanged) return true;
            }
            return false;
        }

        internal static bool HasRootTargetTransformChanges(
            PoseCacheRenderContext context,
            string entryId)
        {
            return TryGetSession(context, out ConstraintPosePreviewSession session) &&
                TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) &&
                 (entry?.FullBodyTargets != null &&
                   entry.FullBodyTargets.TryGetValue(HumanBodyBones.Hips, out GameObject target) &&
                   target != null && target.transform.hasChanged);
        }

        internal static void EnableChangedConstraintChannels(
            PoseCacheRenderContext context,
            string entryId,
            KimodoMarkerSampleResult sample)
        {
            if (sample == null ||
                !TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry))
            {
                return;
            }

            sample.mask ??= new KimodoConstraintMask();
            if (entry.FullBodyTargets == null) return;
            foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
            {
                if (item.Value == null || !item.Value.transform.hasChanged) continue;
                if (item.Key != HumanBodyBones.Hips && !IsTargetEnabled(sample.mask, item.Key)) continue;
                switch (item.Key)
                {
                    case HumanBodyBones.Hips:
                        sample.mask.rootPosition = true;
                        sample.mask.rootHeading = true;
                        sample.hasRootHeading = true;
                        break;
                    case HumanBodyBones.LeftHand: sample.mask.leftHand = true; break;
                    case HumanBodyBones.RightHand: sample.mask.rightHand = true; break;
                    case HumanBodyBones.LeftFoot: sample.mask.leftFoot = true; break;
                    case HumanBodyBones.RightFoot: sample.mask.rightFoot = true; break;
                }
            }
        }

        internal static void GetChangedAutoSampleChannels(
            PoseCacheRenderContext context,
            string entryId,
            out bool fullBodyChanged)
        {
            fullBodyChanged = false;
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry))
            {
                return;
            }

            if (entry.FullBodyTargets == null) return;
            foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
            {
                if (item.Value != null && item.Value.transform.hasChanged &&
                    item.Key == HumanBodyBones.Hips)
                {
                    fullBodyChanged = true;
                    return;
                }
            }
        }

        internal static bool TryPreviewEndEffectorTargetPose(
            PoseCacheRenderContext context,
            string entryId,
            string constraintType,
            out string error)
        {
            error = string.Empty;
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry))
            {
                error = "pose cache context has no active end-effector entry.";
                return false;
            }

            bool wasActive = entry.TargetCache.root.activeSelf;
            entry.TargetCache.root.SetActive(true);
            try
            {
                bool applied = string.Equals(constraintType, "fullbody", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(constraintType, "constraint", StringComparison.OrdinalIgnoreCase)
                    ? TryApplyFullBodyTargetsToRig(entry, context.ModelName, out error)
                    : TryApplyEndEffectorTargetToRig(entry, context.ModelName, constraintType, out error);
                if (!applied)
                {
                    return false;
                }

                SceneView.RepaintAll();
                return true;
            }
            finally
            {
                entry.TargetCache.root.SetActive(wasActive);
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

        internal static bool TryGetHumanoidBoneLocalEuler(
            PoseCacheRenderContext context,
            string entryId,
            HumanBodyBones bone,
            out Vector3 euler)
        {
            euler = Vector3.zero;
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry))
            {
                return false;
            }

            Transform transform = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(entry?.TargetCache, bone);
            if (transform == null) return false;
            euler = transform.localEulerAngles;
            return true;
        }

        internal static bool TrySetHumanoidBoneLocalEuler(
            PoseCacheRenderContext context,
            string entryId,
            HumanBodyBones bone,
            Vector3 euler)
        {
            if (!TryGetSession(context, out ConstraintPosePreviewSession session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry))
            {
                return false;
            }

            Transform transform = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(entry?.TargetCache, bone);
            if (transform == null) return false;
            transform.localRotation = Quaternion.Euler(euler);
            transform.hasChanged = true;
            return true;
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

        internal static bool TryGetFullBodyTarget(
            PoseCacheRenderContext context,
            string entryId,
            HumanBodyBones bone,
            out GameObject target)
        {
            target = null;
            return TryGetSession(context, out ConstraintPosePreviewSession session) &&
                TryGetEntryForContext(session, entryId, out ConstraintPosePreviewEntry entry) &&
                entry?.FullBodyTargets != null &&
                entry.FullBodyTargets.TryGetValue(bone, out target) &&
                target != null;
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
            TrackAsset track = KimodoEditorObjectIdUtility.ObjectFromId(trackId) as TrackAsset;
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
            DestroyFullBodyTargets(entry);

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

            bool changed = false;
            if (entry.Root.gameObject.activeSelf != visible)
            {
                entry.Root.gameObject.SetActive(visible);
                changed = true;
            }
            if (entry.EndEffectorMarker != null && entry.EndEffectorMarker.activeSelf != visible)
            {
                entry.EndEffectorMarker.SetActive(visible);
                changed = true;
            }
            if (entry.FullBodyTargets != null)
            {
                foreach (GameObject target in entry.FullBodyTargets.Values)
                {
                    if (target != null && target.activeSelf != visible)
                    {
                        target.SetActive(visible);
                        changed = true;
                    }
                }
            }
            return changed;
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
            if (entry == null)
            {
                return;
            }

            SetIkTargetSelectable(entry.EndEffectorMarker, selectable);
            if (entry.FullBodyTargets != null)
            {
                foreach (GameObject target in entry.FullBodyTargets.Values)
                {
                    SetIkTargetSelectable(target, selectable);
                }
            }
        }

        private static void SetIkTargetSelectable(GameObject target, bool selectable)
        {
            if (target == null)
            {
                return;
            }

            target.hideFlags = selectable
                ? HideFlags.DontSave
                : HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
            try
            {
                if (selectable)
                {
                    SceneVisibilityManager.instance.EnablePicking(target, false);
                }
                else
                {
                    SceneVisibilityManager.instance.DisablePicking(target, false);
                }
            }
            catch
            {
                // Scene visibility may be unavailable during editor shutdown.
            }
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

            List<string> highlighted = item.HighlightJoints != null && item.HighlightJoints.Count > 0
                ? new List<string>(item.HighlightJoints)
                : KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(null, modelName);
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
                    AddHash(ref hash, sample.characterPose != null ? JsonUtility.ToJson(sample.characterPose) : string.Empty);
                    AddHash(ref hash, sample.hasRoot2DOverride && sample.root2DOverride != null
                        ? JsonUtility.ToJson(sample.root2DOverride)
                        : string.Empty);
                    AddHash(ref hash, sample.constraintType);
                    AddHash(ref hash, sample.sampleTime.GetHashCode());
                    AddHash(ref hash, MaskSignature(sample.mask));
                    AddHash(ref hash, sample.hasRootHeading ? 1 : 0);
                    AddHash(ref hash, sample.hasRoot2DOverride ? 1 : 0);
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

        private static string MaskSignature(KimodoConstraintMask mask) => mask == null
            ? string.Empty
            : $"{mask.muscle}:{mask.rootPosition}:{mask.rootHeading}:{mask.leftFoot}:{mask.rightFoot}:{mask.leftHand}:{mask.rightHand}";

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
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(entry.BaseSample?.mask, markerType).Clone();
                if (string.Equals(markerType, "constraint", StringComparison.OrdinalIgnoreCase) &&
                    HasFullBodyTargetTransformChanges(entry) &&
                    !TryApplyFullBodyTargetsToRig(entry, modelName, mask, out error))
                {
                    return false;
                }
                if (ResolveEndEffectorBone(markerType) != HumanBodyBones.LastBone &&
                    entry.EndEffectorMarker != null &&
                    !TryApplyEndEffectorTargetToRig(entry, modelName, markerType, out error))
                {
                    return false;
                }

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
                        out error,
                        solveLeftHandIk: mask.leftHand,
                        solveRightHandIk: mask.rightHand,
                        applyFootIk: mask.leftFoot || mask.rightFoot))
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

                sample.characterPose = CharacterPoseMuscleAdapter.FromMuscleSample(targetSample);
                if (string.Equals(markerType, "constraint", StringComparison.OrdinalIgnoreCase))
                {
                    sample.mask = mask.Clone();
                    sample.hasRootHeading = mask.rootHeading && entry.BaseSample.hasRootHeading;
                    PreserveIndependentRoot2D(entry, sample);
                }
                CaptureEndEffectorTargetPose(entry, markerType, sample);
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
            if (string.Equals(constraintType, "fullbody", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(constraintType, "constraint", StringComparison.OrdinalIgnoreCase))
            {
                if (entry?.EndEffectorMarker != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.EndEffectorMarker);
                    entry.EndEffectorMarker = null;
                }
                UpdateFullBodyTargets(entry, sample);
                return;
            }

            DestroyFullBodyTargets(entry);
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
                entry.EndEffectorMarker = CreateIkTarget(
                    entry,
                    "__KimodoEndConstraintTarget",
                    TargetPrimitive(bone),
                    TargetColor(bone));
            }

            Transform marker = entry.EndEffectorMarker.transform;
            marker.SetParent(null, true);
            if (TryResolveWorldIkGoal(entry, bone, sample, out Vector3 targetPosition, out Quaternion targetRotation))
            {
                marker.SetPositionAndRotation(targetPosition, targetRotation);
            }
            else
            {
                marker.SetPositionAndRotation(target.position, target.rotation);
            }

            marker.localScale = Vector3.one * EndEffectorTargetSize;
            SetEndEffectorMarkerSelectable(entry, entry.PickingEnabled);
            entry.EndEffectorMarker.SetActive(true);
        }

        private static void UpdateFullBodyTargets(
            ConstraintPosePreviewEntry entry,
            KimodoMarkerSampleResult sample)
        {
            if (entry?.TargetCache == null)
            {
                DestroyFullBodyTargets(entry);
                return;
            }

            entry.FullBodyTargets ??= new Dictionary<HumanBodyBones, GameObject>();
            KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample?.mask, sample?.constraintType);
            for (int i = 0; i < FullBodyTargetBones.Length; i++)
            {
                HumanBodyBones bone = FullBodyTargetBones[i];
                Transform bodyPart = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(entry.TargetCache, bone);
                if (bodyPart == null)
                {
                    continue;
                }

                if (!entry.FullBodyTargets.TryGetValue(bone, out GameObject target) || target == null)
                {
                    target = CreateIkTarget(
                        entry,
                        $"__KimodoFullBodyTarget_{bone}",
                        bone == HumanBodyBones.Hips ? PrimitiveType.Sphere : TargetPrimitive(bone),
                        bone == HumanBodyBones.Hips ? FullBodyRootColor : TargetColor(bone));
                    entry.FullBodyTargets[bone] = target;
                }

                if (bone == HumanBodyBones.Hips)
                {
                    target.transform.SetParent(null, true);
                    target.transform.SetPositionAndRotation(bodyPart.position, bodyPart.rotation);
                }
                else if (IsTargetEnabled(mask, bone) &&
                    TryResolveWorldIkGoal(entry, bone, sample, out Vector3 position, out Quaternion rotation))
                {
                    target.transform.SetParent(null, true);
                    target.transform.SetPositionAndRotation(position, rotation);
                }
                else
                {
                    target.transform.SetParent(bodyPart, false);
                    target.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                }
                target.transform.localScale = Vector3.one * (bone == HumanBodyBones.Hips
                    ? ResolveFullBodyRootSize(entry)
                    : EndEffectorTargetSize);
                SetIkTargetSelectable(target, entry.PickingEnabled);
                target.SetActive(true);
            }
        }

        private static GameObject CreateIkTarget(
            ConstraintPosePreviewEntry entry,
            string name,
            PrimitiveType primitive = PrimitiveType.Cube,
            Color? color = null)
        {
            GameObject target = new GameObject(name);
            target.name = name;
            target.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
            return target;
        }

        private static void PreserveIndependentRoot2D(
            ConstraintPosePreviewEntry entry,
            KimodoMarkerSampleResult captured)
        {
            KimodoMarkerSampleResult authored = entry?.BaseSample;
            if (authored?.hasRoot2DOverride != true ||
                authored.root2DOverride == null ||
                captured == null)
            {
                return;
            }

            captured.root2DOverride = new CharacterPoseTransform
            {
                t = authored.root2DOverride.t,
                q = authored.root2DOverride.q
            };
            captured.hasRoot2DOverride = true;
        }

        private static PrimitiveType TargetPrimitive(HumanBodyBones bone) =>
            bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand
                ? PrimitiveType.Sphere
                : PrimitiveType.Cube;

        private static Color TargetColor(HumanBodyBones bone) =>
            bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.LeftFoot
                ? LeftTargetColor
                : RightTargetColor;

        private static float ResolveFullBodyRootSize(ConstraintPosePreviewEntry entry)
        {
            Transform leftHip = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(
                entry?.TargetCache, HumanBodyBones.LeftUpperLeg);
            Transform rightHip = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(
                entry?.TargetCache, HumanBodyBones.RightUpperLeg);
            if (leftHip == null || rightHip == null) return EndEffectorTargetSize;
            return Mathf.Max(EndEffectorTargetSize, Vector3.Distance(leftHip.position, rightHip.position) * 0.05f);
        }

        private static void CaptureEndEffectorTargetPose(
            ConstraintPosePreviewEntry entry,
            string constraintType,
            KimodoMarkerSampleResult sample)
        {
            if (string.Equals(constraintType, "constraint", StringComparison.OrdinalIgnoreCase))
            {
                CaptureFullBodyTargetPoses(entry, sample);
                return;
            }
            HumanBodyBones bone = ResolveEndEffectorBone(constraintType);
            if (bone == HumanBodyBones.LastBone ||
                entry?.EndEffectorMarker == null ||
                sample == null)
            {
                return;
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    entry.TargetCache,
                    out MuscleSample targetSample,
                    out _))
            {
                return;
            }

            KimodoRetargetHumanoidIkUtility.WorldToBodyRelativeIkGoal(
                targetSample.pose.bodyPosition,
                targetSample.pose.bodyRotation,
                entry.TargetCache.humanScale,
                entry.EndEffectorMarker.transform.position,
                entry.EndEffectorMarker.transform.rotation,
                out Vector3 goalPosition,
                out Quaternion goalRotation);
            CharacterPoseTransform goal = ResolveCharacterPoseGoal(sample.characterPose, bone);
            if (goal != null)
            {
                goal.t = goalPosition;
                goal.q = goalRotation;
            }
        }

        private static bool TryResolveWorldIkGoal(
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            KimodoMarkerSampleResult sample,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (entry?.TargetCache == null ||
                !KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    entry.TargetCache,
                    out MuscleSample targetSample,
                    out _) ||
                !TryGetMuscleIkGoal(targetSample, bone, out Vector3 goalPosition, out Quaternion goalRotation))
            {
                return false;
            }

            CharacterPoseTransform storedGoal = ResolveCharacterPoseGoal(sample?.characterPose, bone);
            if (storedGoal != null)
            {
                goalPosition = storedGoal.t;
                goalRotation = storedGoal.q;
            }

            KimodoRetargetHumanoidIkUtility.BodyRelativeIkGoalToWorld(
                targetSample.pose.bodyPosition,
                targetSample.pose.bodyRotation,
                entry.TargetCache.humanScale,
                goalPosition,
                goalRotation,
                out position,
                out rotation);

            if (TryResolveTargetAvatarPoint(entry, bone, sample, out Vector3 storedPosition))
            {
                position = storedPosition;
            }
            return true;
        }

        private static bool TryApplyEndEffectorTargetToRig(
            ConstraintPosePreviewEntry entry,
            string modelName,
            string constraintType,
            out string error)
        {
            error = string.Empty;
            HumanBodyBones bone = ResolveEndEffectorBone(constraintType);
            if (bone == HumanBodyBones.LastBone ||
                entry?.EndEffectorMarker == null ||
                entry.BaseSample == null ||
                entry.TargetCache == null ||
                entry.ProfileCache == null)
            {
                error = "end-effector target pose is unavailable.";
                return false;
            }

            KimodoMarkerSampleResult basePose =
                KimodoConstraintSampleResolver.ResolveUnifiedSample(entry.BaseSample);
            if (!KimodoConstraintSpaceConverter.TryApplyToTargetAvatar(
                    basePose,
                    modelName,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                    entry.ProfileCache,
                    entry.TargetCache,
                    out error) ||
                !KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    entry.TargetCache,
                    out MuscleSample sourceSample,
                    out error))
            {
                return false;
            }

            KimodoRetargetHumanoidIkUtility.WorldToBodyRelativeIkGoal(
                sourceSample.pose.bodyPosition,
                sourceSample.pose.bodyRotation,
                entry.TargetCache.humanScale,
                entry.EndEffectorMarker.transform.position,
                entry.EndEffectorMarker.transform.rotation,
                out Vector3 goalPosition,
                out Quaternion goalRotation);
            if (!TrySetMuscleIkGoal(sourceSample, bone, goalPosition, goalRotation))
            {
                error = $"unsupported humanoid IK goal '{bone}'.";
                return false;
            }

            return KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                sourceSample,
                KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                entry.TargetCache,
                out _,
                out _,
                out error,
                solveLeftHandIk: bone == HumanBodyBones.LeftHand,
                solveRightHandIk: bone == HumanBodyBones.RightHand,
                applyFootIk: bone == HumanBodyBones.LeftFoot || bone == HumanBodyBones.RightFoot);
        }

        private static bool TryApplyFullBodyTargetsToRig(
            ConstraintPosePreviewEntry entry,
            string modelName,
            out string error)
        {
            return TryApplyFullBodyTargetsToRig(
                entry,
                modelName,
                KimodoConstraintMask.Resolve(entry?.BaseSample?.mask, entry?.BaseSample?.constraintType),
                out error);
        }

        private static bool TryApplyFullBodyTargetsToRig(
            ConstraintPosePreviewEntry entry,
            string modelName,
            KimodoConstraintMask mask,
            out string error)
        {
            error = string.Empty;
            if (entry?.FullBodyTargets == null ||
                entry.BaseSample == null ||
                entry.TargetCache == null ||
                entry.ProfileCache == null)
            {
                error = "full-body IK targets are unavailable.";
                return false;
            }

            float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
            KimodoMarkerSampleResult basePose =
                KimodoConstraintSampleResolver.ResolveUnifiedSample(entry.BaseSample);
            if (!KimodoConstraintSpaceConverter.TryApplyToTargetAvatar(
                    basePose,
                    modelName,
                    frameRate,
                    entry.ProfileCache,
                    entry.TargetCache,
                    out error))
            {
                return false;
            }

            if ((mask.muscle || mask.rootPosition || mask.rootHeading) &&
                entry.FullBodyTargets.TryGetValue(HumanBodyBones.Hips, out GameObject pelvisTarget) &&
                pelvisTarget != null)
            {
                Transform hips = KimodoRetargetHumanoidIkUtility.ResolveHumanBoneTransform(
                    entry.TargetCache,
                    HumanBodyBones.Hips);
                if (hips != null)
                {
                    hips.SetPositionAndRotation(pelvisTarget.transform.position, pelvisTarget.transform.rotation);
                }
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    entry.TargetCache,
                    out MuscleSample sourceSample,
                    out error))
            {
                return false;
            }

            for (int i = 1; i < FullBodyTargetBones.Length; i++)
            {
                HumanBodyBones bone = FullBodyTargetBones[i];
                if (!IsTargetEnabled(mask, bone, false) ||
                    !entry.FullBodyTargets.TryGetValue(bone, out GameObject target) || target == null)
                {
                    continue;
                }

                KimodoRetargetHumanoidIkUtility.WorldToBodyRelativeIkGoal(
                    sourceSample.pose.bodyPosition,
                    sourceSample.pose.bodyRotation,
                    entry.TargetCache.humanScale,
                    target.transform.position,
                    target.transform.rotation,
                    out Vector3 goalPosition,
                    out Quaternion goalRotation);
                if (!TrySetMuscleIkGoal(sourceSample, bone, goalPosition, goalRotation))
                {
                    error = $"unsupported humanoid IK goal '{bone}'.";
                    return false;
                }
            }

            return KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                sourceSample,
                frameRate,
                entry.TargetCache,
                out _,
                out _,
                out error,
                solveLeftHandIk: mask.leftHand,
                solveRightHandIk: mask.rightHand,
                applyFootIk: mask.leftFoot || mask.rightFoot);
        }

        private static bool IsTargetEnabled(
            KimodoConstraintMask mask,
            HumanBodyBones bone,
            bool legacyFullBody = false)
        {
            if (mask == null) return true;
            if (legacyFullBody && bone != HumanBodyBones.Hips) return true;
            return bone switch
            {
                HumanBodyBones.Hips => mask.muscle || legacyFullBody,
                HumanBodyBones.LeftHand => mask.leftHand,
                HumanBodyBones.RightHand => mask.rightHand,
                HumanBodyBones.LeftFoot => mask.leftFoot,
                HumanBodyBones.RightFoot => mask.rightFoot,
                _ => false
            };
        }

        private static bool IsHumanoidIkGoalBone(HumanBodyBones bone)
        {
            return bone == HumanBodyBones.LeftHand ||
                bone == HumanBodyBones.RightHand ||
                bone == HumanBodyBones.LeftFoot ||
                bone == HumanBodyBones.RightFoot;
        }

        private static bool TryGetMuscleIkGoal(
            MuscleSample sample,
            HumanBodyBones bone,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (sample == null)
            {
                return false;
            }

            switch (bone)
            {
                case HumanBodyBones.LeftHand:
                    position = sample.leftHandPosition;
                    rotation = sample.leftHandRotation;
                    return true;
                case HumanBodyBones.RightHand:
                    position = sample.rightHandPosition;
                    rotation = sample.rightHandRotation;
                    return true;
                case HumanBodyBones.LeftFoot:
                    position = sample.leftFootPosition;
                    rotation = sample.leftFootRotation;
                    return true;
                case HumanBodyBones.RightFoot:
                    position = sample.rightFootPosition;
                    rotation = sample.rightFootRotation;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TrySetMuscleIkGoal(
            MuscleSample sample,
            HumanBodyBones bone,
            Vector3 position,
            Quaternion rotation)
        {
            if (sample == null)
            {
                return false;
            }

            switch (bone)
            {
                case HumanBodyBones.LeftHand:
                    sample.leftHandPosition = position;
                    sample.leftHandRotation = rotation;
                    return true;
                case HumanBodyBones.RightHand:
                    sample.rightHandPosition = position;
                    sample.rightHandRotation = rotation;
                    return true;
                case HumanBodyBones.LeftFoot:
                    sample.leftFootPosition = position;
                    sample.leftFootRotation = rotation;
                    return true;
                case HumanBodyBones.RightFoot:
                    sample.rightFootPosition = position;
                    sample.rightFootRotation = rotation;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryResolveTargetAvatarPoint(
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            KimodoMarkerSampleResult sample,
            out Vector3 position)
        {
            position = Vector3.zero;
            // Hand/foot markers use the Humanoid T/Q goal directly. A stored
            // end-effector point may use older bone-space semantics and cause
            // a one-frame jump when the Scene view drag starts.
            if (IsHumanoidIkGoalBone(bone))
            {
                return false;
            }

            // Canonical CharacterPose stores humanoid IK goals directly.
            // Non-IK legacy target fields are intentionally unsupported.
            return false;
        }

        private static CharacterPoseTransform ResolveCharacterPoseGoal(
            CharacterPose pose,
            HumanBodyBones bone)
        {
            if (pose == null) return null;
            switch (bone)
            {
                case HumanBodyBones.LeftHand: return pose.hands?.left;
                case HumanBodyBones.RightHand: return pose.hands?.right;
                case HumanBodyBones.LeftFoot: return pose.feet?.left;
                case HumanBodyBones.RightFoot: return pose.feet?.right;
                default: return null;
            }
        }

        private static void CaptureFullBodyTargetPoses(
            ConstraintPosePreviewEntry entry,
            KimodoMarkerSampleResult sample)
        {
            if (entry?.FullBodyTargets == null || sample?.characterPose == null ||
                !KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    entry.TargetCache,
                    out MuscleSample targetSample,
                    out _)) return;
            KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample.mask, sample.constraintType);
            for (int i = 1; i < FullBodyTargetBones.Length; i++)
            {
                HumanBodyBones bone = FullBodyTargetBones[i];
                if (!IsTargetEnabled(mask, bone) ||
                    !entry.FullBodyTargets.TryGetValue(bone, out GameObject target) || target == null)
                {
                    continue;
                }
                CharacterPoseTransform goal = ResolveCharacterPoseGoal(sample.characterPose, bone);
                if (goal == null) continue;
                KimodoRetargetHumanoidIkUtility.WorldToBodyRelativeIkGoal(
                    targetSample.pose.bodyPosition,
                    targetSample.pose.bodyRotation,
                    entry.TargetCache.humanScale,
                    target.transform.position,
                    target.transform.rotation,
                    out goal.t,
                    out goal.q);
            }
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
            return sample?.characterPose?.root?.q ?? Quaternion.identity;
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
            if (marker != null && (transform == marker || transform.IsChildOf(marker)))
            {
                return true;
            }

            if (entry?.FullBodyTargets != null)
            {
                foreach (GameObject target in entry.FullBodyTargets.Values)
                {
                    if (target != null &&
                        (transform == target.transform || transform.IsChildOf(target.transform)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool HasFullBodyTargetTransformChanges(ConstraintPosePreviewEntry entry)
        {
            if (entry?.FullBodyTargets == null)
            {
                return false;
            }
            KimodoConstraintMask mask = KimodoConstraintMask.Resolve(entry.BaseSample?.mask, entry.BaseSample?.constraintType);
            foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
            {
                if (item.Value != null && item.Value.transform.hasChanged &&
                    (item.Key == HumanBodyBones.Hips || IsTargetEnabled(mask, item.Key)))
                {
                    return true;
                }
            }
            return false;
        }

        private static void ClearFullBodyTargetTransformChanges(ConstraintPosePreviewEntry entry)
        {
            if (entry?.FullBodyTargets == null)
            {
                return;
            }
            foreach (GameObject target in entry.FullBodyTargets.Values)
            {
                if (target != null)
                {
                    target.transform.hasChanged = false;
                }
            }
        }

        private static void DestroyFullBodyTargets(ConstraintPosePreviewEntry entry)
        {
            if (entry?.FullBodyTargets == null)
            {
                return;
            }
            foreach (GameObject target in entry.FullBodyTargets.Values)
            {
                if (target != null)
                {
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
            entry.FullBodyTargets.Clear();
            entry.FullBodyTargets = null;
        }

        private static Material CreateTargetMaterial(Color color)
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
                name = "__KimodoConstraintTarget"
            };
            SetMaterialColor(material, color, 1f);
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
