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
        public readonly string ContextKey;

        public PoseCacheRenderContext(
            int clipId,
            int animatorId,
            int trackId,
            string modelName,
            KimodoConstraintRigType rigType)
        {
            ClipId = clipId;
            AnimatorId = animatorId;
            TrackId = trackId;
            ModelName = string.IsNullOrWhiteSpace(modelName) ? "Kimodo-SOMA-RP-v1" : modelName.Trim();
            RigType = rigType;
            ContextKey = KimodoConstraintMarkerEditorUtility.GetCachedIntString(clipId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(animatorId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(trackId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString((int)rigType);
            }
        }

    internal sealed class PoseCacheRenderItem
    {
        public string EntryId;
        public KimodoMarkerSampleResult SampleData;
        public string ConstraintType;
        public KimodoConstraintMode ConstraintMode = KimodoConstraintMode.FullBody;
        public List<string> HighlightJoints;
        public Color PreviewColor = Color.white;
        public bool Visible = true;
        public KimodoConstraintMarker SourceMarker;
    }

    internal sealed class ConstraintPosePreviewEntry
    {
        public string Key;
        public Transform Root;
        public RetargetSkeleton TargetCache;
        public List<Material> GeneratedMaterials;
        public GameObject EndEffectorMarker;
        public Dictionary<HumanBodyBones, GameObject> FullBodyTargets;
        public KimodoConstraintMarker SourceMarker;
        // Current frame sample used to rebuild the preview rig. This is not a
        // sampling cache; it is replaced on every RenderBatch pass.
        public KimodoMarkerSampleResult SampleData;
        public bool PickingEnabled;
        public bool ShowVirtualAvatar = true;
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

    internal static class KimodoConstraintSampleApplier
    {
        internal static bool TryApplyToTargetAvatar(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton targetCache,
            out string error)
        {
            error = string.Empty;
            if (sample == null || targetCache == null)
            {
                error = "Constraint target Avatar cache is unavailable.";
                return false;
            }

            if (KimodoSampleResultPoseUtility.TryDecode(sample, out CharacterPose canonicalPose, out error))
            {
                if (!canonicalPose.TryValidate(out error))
                {
                    return false;
                }
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        CharacterPoseMuscleAdapter.ToMuscleSample(canonicalPose),
                        frameRate,
                        targetCache,
                        out BoneSample canonicalTargetSample,
                        out _,
                        out error,
                    sceneTargets: null) ||
                    !KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                        canonicalTargetSample,
                        targetCache,
                        out error))
                {
                    return false;
                }

                return true;
            }

            error = "SampleResult sampleData is invalid.";
            return false;
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
            bool root2D = entry.SourceMarker?.ConstraintMode == KimodoConstraintMode.Root2D;
            float size = bone == HumanBodyBones.Hips
                ? 0.1f
                : HandleUtility.GetHandleSize(position) * 0.09f;
            if (bone != HumanBodyBones.Hips) size = Mathf.Max(EndEffectorTargetSize, size);
            Handles.color = root2D && bone == HumanBodyBones.Hips
                ? Color.white
                : bone == HumanBodyBones.Hips ? FullBodyRootColor : TargetColor(bone);
            Handles.CapFunction cap = !root2D && (bone == HumanBodyBones.Hips || bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand)
                ? Handles.SphereHandleCap : Handles.CubeHandleCap;
            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(entry.SourceMarker);
            if (!windowOpen)
            {
                return;
            }
            bool editable = windowOpen && entry.SourceMarker != null &&
                (entry.SourceMarker.ConstraintMode == KimodoConstraintMode.FullBody ||
                 bone != HumanBodyBones.Hips || !entry.SourceMarker.autoSample);
            if (editable && Selection.activeTransform == transform)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(position, size, Vector3.zero, cap);
                Quaternion rotated = Handles.RotationHandle(transform.rotation, moved);
                if (EditorGUI.EndChangeCheck()) transform.SetPositionAndRotation(moved, rotated);
            }
            else if (editable)
            {
                if (Handles.Button(position, transform.rotation, size, size * 1.25f, cap))
                {
                    Selection.activeGameObject = target;
                    Tools.current = Tool.Move;
                }
            }
            else
            {
                cap(0, position, transform.rotation, size, EventType.Repaint);
            }
            Handles.Label(position + Vector3.up * size,
                bone == HumanBodyBones.Hips ? root2D ? "Root2D" : "Root Position / Rotation" : bone.ToString());
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
                KimodoConstraintMode renderMode = ResolveRenderMode(item);
                entry.ShowVirtualAvatar = renderMode != KimodoConstraintMode.Root2D;

                {
                    entry.SampleData = item.SampleData.Clone();
                    KimodoMarkerSampleResult renderSample =
                        KimodoConstraintSampleComposer.ResolveUnifiedSample(item.SampleData);
                    var highlightedJoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectHighlightedJointsFromItem(item, context.ModelName, highlightedJoints);

                    if (entry.SourceMarker?.autoSample != false)
                    {
                        // AutoSample flow is deliberately two-phase:
                        // timeline/muscles -> FK avatar -> rig transforms ->
                        // scene-handle effector processing. Solving before refreshing the
                        // rig would feed the previous frame's targets.
                        KimodoMarkerSampleResult fkSample = renderSample.Clone();
                        fkSample.effectors = null;
                        if (!ApplySampleToRig(
                                fkSample,
                                context.ModelName,
                                entry,
                                out error,
                                applySceneTargets: false))
                        {
                            error = $"pose cache FK render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}): {error}";
                            return false;
                        }
                        UpdateEndEffectorMarker(entry, item.ConstraintType, renderMode);
                        if (!ApplySampleToRig(renderSample, context.ModelName, entry, out error))
                        {
                            error = $"pose cache effector render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}): {error}";
                            return false;
                        }
                    }
                    else
                    {
                        if (!ApplySampleToRig(renderSample, context.ModelName, entry, out error))
                        {
                            error = $"pose cache render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}): {error}";
                            return false;
                        }
                        UpdateEndEffectorMarker(entry, item.ConstraintType, renderMode);
                    }

                    ApplyConstraintColoring(entry, highlightedJoints, item.PreviewColor);
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

        internal static bool HasEffectorTransformChanges(
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
            KimodoConstraintMask mask = KimodoConstraintMask.FromSample(entry.SampleData);
            foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
            {
                if (item.Key != HumanBodyBones.Hips &&
                    HasFullBodyTargetTransformChanged(entry, item.Key, item.Value, mask)) return true;
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

            KimodoConstraintMask mask = KimodoConstraintMask.FromSample(sample);
            bool rootChanged = entry.FullBodyTargets != null &&
                entry.FullBodyTargets.TryGetValue(HumanBodyBones.Hips, out GameObject rootTarget) &&
                HasFullBodyTargetTransformChanged(entry, HumanBodyBones.Hips, rootTarget, mask);
            EnableChangedConstraintChannels(entry, mask);
            sample.enableMask ??= new KimodoSampleChannelMask();
            sample.enableMask.root2DPosition |= mask.rootPosition;
            sample.enableMask.root2DHeading |= mask.rootHeading;
            sample.enableMask.leftHandEffector |= mask.leftHand;
            sample.enableMask.rightHandEffector |= mask.rightHand;
            sample.enableMask.leftFootEffector |= mask.leftFoot;
            sample.enableMask.rightFootEffector |= mask.rightFoot;
            if (rootChanged)
            {
                sample.enableMask ??= new KimodoSampleChannelMask();
                sample.enableMask.root2DPosition = true;
                sample.enableMask.root2DHeading = true;
            }
        }

        private static void EnableChangedConstraintChannels(
            ConstraintPosePreviewEntry entry,
            KimodoConstraintMask mask)
        {
            if (entry?.FullBodyTargets == null || mask == null)
            {
                return;
            }

            foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
            {
                if (item.Value == null ||
                    !HasFullBodyTargetTransformChanged(entry, item.Key, item.Value, mask))
                {
                    continue;
                }

                switch (item.Key)
                {
                    case HumanBodyBones.Hips:
                        mask.rootPosition = true;
                        mask.rootHeading = true;
                        break;
                    case HumanBodyBones.LeftHand:
                        mask.leftHand = true;
                        break;
                    case HumanBodyBones.RightHand:
                        mask.rightHand = true;
                        break;
                    case HumanBodyBones.LeftFoot:
                        mask.leftFoot = true;
                        break;
                    case HumanBodyBones.RightFoot:
                        mask.rightFoot = true;
                        break;
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

            // Effector gizmos are transport/display data only. Preview must
            // never run a hidden IK pass or mutate the FK skeleton.
            SceneView.RepaintAll();
            return true;
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

            rootBone = entry.TargetCache?.skeletonRoot ?? entry.Root;
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
            bone = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(entry.TargetCache, humanBone);
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

            Transform transform = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(entry?.TargetCache, bone);
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

            Transform transform = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(entry?.TargetCache, bone);
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

            Transform hips = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
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

            Transform hips = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
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

            UpdateEndEffectorMarker(
                entry,
                constraintType,
                entry.SourceMarker != null ? entry.SourceMarker.ConstraintMode : KimodoConstraintMode.Effector);
            SceneView.RepaintAll();
            return true;
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
                        out KimodoConstraintPoseRigFactory.PoseRigInstance rig,
                        out error))
                {
                    return false;
                }

                transient = new ConstraintPosePreviewEntry
                {
                    Root = rig.Root != null ? rig.Root.transform : null,
                    TargetCache = rig.TargetCache,
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

            RetargetSkeleton targetCache = entry.TargetCache;
            entry.TargetCache = null;
            targetCache?.Dispose();

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
            bool avatarVisible = visible && entry.ShowVirtualAvatar;
            if (entry.Root.gameObject.activeSelf != avatarVisible)
            {
                entry.Root.gameObject.SetActive(avatarVisible);
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
                SceneVisibilityManager.instance.DisablePicking(entry.Root.gameObject, true);
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

                    SetEffectorGizmoSelectable(entry.EndEffectorMarker, selectable);
            if (entry.FullBodyTargets != null)
            {
                foreach (GameObject target in entry.FullBodyTargets.Values)
                {
                    SetEffectorGizmoSelectable(target, selectable);
                }
            }
        }

        private static void SetEffectorGizmoSelectable(GameObject target, bool selectable)
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

        private static bool ApplySampleToRig(
            KimodoMarkerSampleResult sample,
            string modelName,
            ConstraintPosePreviewEntry entry,
            out string error,
            bool applySceneTargets = true)
        {
            error = string.Empty;
            if (sample == null || entry?.TargetCache == null)
            {
                error = "Constraint target Avatar cache is unavailable.";
                return false;
            }

            bool wasActive = entry.TargetCache.root.activeSelf;
            entry.TargetCache.root.SetActive(true);
            try
            {
                return KimodoConstraintSampleApplier.TryApplyToTargetAvatar(
                    sample,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
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
            if (entry?.TargetCache == null)
            {
                error = "Constraint target Avatar cache is unavailable.";
                return false;
            }

            bool wasActive = entry.TargetCache.root.activeSelf;
            entry.TargetCache.root.SetActive(true);
            try
            {
                KimodoConstraintMask mask = KimodoConstraintMask.FromSample(entry.SampleData).Clone();
                bool rootTargetChanged = HasChangedFullBodyTarget(entry, HumanBodyBones.Hips, mask);
                bool leftHandTargetChanged = HasChangedFullBodyTarget(entry, HumanBodyBones.LeftHand, mask);
                bool rightHandTargetChanged = HasChangedFullBodyTarget(entry, HumanBodyBones.RightHand, mask);
                bool leftFootTargetChanged = HasChangedFullBodyTarget(entry, HumanBodyBones.LeftFoot, mask);
                bool rightFootTargetChanged = HasChangedFullBodyTarget(entry, HumanBodyBones.RightFoot, mask);
                if (entry.EndEffectorMarker != null && entry.EndEffectorMarker.transform.hasChanged)
                {
                    HumanBodyBones endEffectorBone = ResolveEndEffectorBone(markerType);
                    if (endEffectorBone != HumanBodyBones.LastBone)
                    {
                        switch (endEffectorBone)
                        {
                            case HumanBodyBones.LeftHand:
                                leftHandTargetChanged = true;
                                break;
                            case HumanBodyBones.RightHand:
                                rightHandTargetChanged = true;
                                break;
                            case HumanBodyBones.LeftFoot:
                                leftFootTargetChanged = true;
                                break;
                            case HumanBodyBones.RightFoot:
                                rightFootTargetChanged = true;
                                break;
                        }
                    }
                }
                EnableChangedConstraintChannels(entry, mask);
                BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(entry.TargetCache);
                if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        targetSample,
                        entry.TargetCache,
                        modelName,
                        markerType,
                        sampleTime,
                        out sample,
                        out error))
                {
                    return false;
                }

                sample.effectors = CaptureEffectorsFromEntry(entry, mask, markerType);
                if (string.Equals(markerType, "constraint", StringComparison.OrdinalIgnoreCase))
                {
                    sample.enableMask.root2DHeading = mask.rootHeading && entry.SampleData.enableMask?.root2DHeading == true;
                    if (rootTargetChanged && entry.FullBodyTargets.TryGetValue(
                            HumanBodyBones.Hips, out GameObject rootTarget) && rootTarget != null)
                    {
                        sample.root2DOverride = new KimodoRigidTransform
                        {
                            t = rootTarget.transform.position,
                            q = rootTarget.transform.rotation
                        };
                        sample.enableMask.root2DPosition = true;
                    }
                    PreserveIndependentRoot2D(entry, sample);
                }
                return true;
            }
            finally
            {
                entry.TargetCache.root.SetActive(wasActive);
            }
        }

        private static KimodoConstraintMode ResolveRenderMode(PoseCacheRenderItem item)
        {
            if (string.Equals(item?.ConstraintType, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                return KimodoConstraintMode.Root2D;
            }
            return item?.ConstraintMode ?? KimodoConstraintMode.FullBody;
        }

        private static bool HasChangedFullBodyTarget(
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            KimodoConstraintMask mask)
        {
            return entry?.FullBodyTargets != null &&
                entry.FullBodyTargets.TryGetValue(bone, out GameObject target) &&
                HasFullBodyTargetTransformChanged(entry, bone, target, mask);
        }

        private static void UpdateEndEffectorMarker(
            ConstraintPosePreviewEntry entry,
            string constraintType,
            KimodoConstraintMode mode)
        {
            if (mode == KimodoConstraintMode.Root2D)
            {
                if (entry?.EndEffectorMarker != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.EndEffectorMarker);
                    entry.EndEffectorMarker = null;
                }
                DestroyFullBodyTargets(entry);
                UpdateRoot2DTarget(entry);
                return;
            }

            if (string.Equals(constraintType, "fullbody", StringComparison.OrdinalIgnoreCase) ||
                (mode == KimodoConstraintMode.FullBody &&
                 string.Equals(constraintType, "constraint", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry?.EndEffectorMarker != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.EndEffectorMarker);
                    entry.EndEffectorMarker = null;
                }
                DestroyRoot2DTarget(entry);
                UpdateFullBodyTargets(entry);
                return;
            }

            if (mode == KimodoConstraintMode.Effector &&
                string.Equals(constraintType, "constraint", StringComparison.OrdinalIgnoreCase))
            {
                if (entry?.EndEffectorMarker != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.EndEffectorMarker);
                    entry.EndEffectorMarker = null;
                }
                UpdateFullBodyTargets(entry);
                return;
            }

            DestroyFullBodyTargets(entry);
            DestroyRoot2DTarget(entry);
            HumanBodyBones bone = ResolveEndEffectorBone(constraintType);
            if (KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                    entry?.TargetCache,
                    bone) == null)
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
                entry.EndEffectorMarker = CreateEffectorGizmo(
                    entry,
                    "__KimodoEndConstraintTarget",
                    TargetPrimitive(bone),
                    TargetColor(bone));
            }

            Transform marker = entry.EndEffectorMarker.transform;
            bool preserveEditedTarget = marker.hasChanged;
            marker.SetParent(null, true);
            Transform bodyPart = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                entry.TargetCache,
                bone);
            if (bodyPart == null)
            {
                UnityEngine.Object.DestroyImmediate(entry.EndEffectorMarker);
                entry.EndEffectorMarker = null;
                return;
            }
            if (!preserveEditedTarget)
            {
                Vector3 targetPosition = bodyPart.position;
                Quaternion targetRotation = ResolveEffectorRotation(entry, bone, bodyPart);
                if (entry.SourceMarker?.autoSample == false &&
                    TryGetEffector(entry.SampleData?.effectors, bone,
                        out Vector3 savedPosition, out Quaternion savedRotation))
                {
                    targetPosition = savedPosition;
                    targetRotation = savedRotation;
                }
                marker.SetPositionAndRotation(targetPosition, targetRotation);
                marker.hasChanged = false;
            }

            marker.localScale = Vector3.one * EndEffectorTargetSize;
            SetEndEffectorMarkerSelectable(entry, entry.PickingEnabled);
            entry.EndEffectorMarker.SetActive(true);
            marker.hasChanged = false;
        }

        private static void UpdateFullBodyTargets(ConstraintPosePreviewEntry entry)
        {
            if (entry?.TargetCache == null)
            {
                DestroyFullBodyTargets(entry);
                return;
            }

            var solvedSample = new KimodoMarkerSampleResult();
            KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(
                entry.TargetCache,
                solvedSample);

            entry.FullBodyTargets ??= new Dictionary<HumanBodyBones, GameObject>();
            KimodoConstraintMask mask = KimodoConstraintMask.FromSample(entry.SampleData);
            for (int i = 0; i < FullBodyTargetBones.Length; i++)
            {
                HumanBodyBones bone = FullBodyTargetBones[i];
                Transform bodyPart = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                    entry.TargetCache,
                    bone);
                if (bodyPart == null)
                {
                    continue;
                }

                bool createdTarget = false;
                if (!entry.FullBodyTargets.TryGetValue(bone, out GameObject target) || target == null)
                {
                    target = CreateEffectorGizmo(
                        entry,
                        $"__KimodoFullBodyTarget_{bone}",
                        bone == HumanBodyBones.Hips ? PrimitiveType.Sphere : TargetPrimitive(bone),
                        bone == HumanBodyBones.Hips ? FullBodyRootColor : TargetColor(bone));
                    entry.FullBodyTargets[bone] = target;
                    createdTarget = true;
                }

                bool preserveEditedTarget = !createdTarget &&
                    target.transform.hasChanged;
                if (preserveEditedTarget)
                {
                    target.transform.localScale = Vector3.one * (bone == HumanBodyBones.Hips
                        ? 0.1f
                        : EndEffectorTargetSize);
                    SetEffectorGizmoSelectable(target, entry.PickingEnabled);
                    target.SetActive(true);
                    target.transform.hasChanged = false;
                    continue;
                }

                if (bone == HumanBodyBones.Hips)
                {
                    target.transform.SetParent(null, true);
                    if (entry.SampleData?.enableMask?.root2DPosition == true &&
                             entry.SampleData.root2DOverride != null)
                    {
                        target.transform.SetPositionAndRotation(
                            entry.SampleData.root2DOverride.t,
                            entry.SampleData.root2DOverride.q);
                    }
                    else if (solvedSample?.enableMask?.root2DPosition == true &&
                             solvedSample.root2DOverride != null)
                    {
                        target.transform.SetPositionAndRotation(
                            solvedSample.root2DOverride.t,
                            solvedSample.root2DOverride.q);
                    }
                }
                else if (entry.SourceMarker?.autoSample == false &&
                    TryGetEffector(entry.SampleData?.effectors, bone,
                        out Vector3 savedPosition, out Quaternion savedRotation))
                {
                    target.transform.SetParent(null, true);
                    target.transform.SetPositionAndRotation(savedPosition, savedRotation);
                }
                else if (entry.SourceMarker?.autoSample == false)
                {
                    // If AutoSample was just disabled, no authored world
                    // target may exist yet. Initialize it from the sampled
                    // bone instead of leaving the new target at (0,0,0).
                    SetTargetToFollowBodyPart(entry, bone, target.transform, bodyPart);
                }
                else if (entry.SourceMarker?.autoSample != false)
                {
                    SetTargetToFollowBodyPart(entry, bone, target.transform, bodyPart);
                }
                target.transform.localScale = Vector3.one * (bone == HumanBodyBones.Hips
                    ? 0.1f
                    : EndEffectorTargetSize);
                SetEffectorGizmoSelectable(target, entry.PickingEnabled);
                target.SetActive(true);
                target.transform.hasChanged = false;
            }
        }

        private static GameObject CreateEffectorGizmo(
            ConstraintPosePreviewEntry entry,
            string name,
            PrimitiveType primitive = PrimitiveType.Cube,
            Color? color = null)
        {
            GameObject target = GameObject.CreatePrimitive(primitive);
            target.name = name;
            Collider collider = target.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }
            target.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
            return target;
        }

        private static void PreserveIndependentRoot2D(
            ConstraintPosePreviewEntry entry,
            KimodoMarkerSampleResult captured)
        {
            KimodoMarkerSampleResult authored = entry?.SampleData;
            if (authored?.enableMask?.root2DPosition != true ||
                authored.root2DOverride == null ||
                captured == null)
            {
                return;
            }

            captured.root2DOverride = new KimodoRigidTransform
            {
                t = authored.root2DOverride.t,
                q = authored.root2DOverride.q
            };
            captured.enableMask.root2DPosition = true;
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
            Transform leftHip = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                entry?.TargetCache, HumanBodyBones.LeftUpperLeg);
            Transform rightHip = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                entry?.TargetCache, HumanBodyBones.RightUpperLeg);
            if (leftHip == null || rightHip == null) return EndEffectorTargetSize;
            return Mathf.Max(EndEffectorTargetSize, Vector3.Distance(leftHip.position, rightHip.position) * 0.05f);
        }

        private static void UpdateRoot2DTarget(ConstraintPosePreviewEntry entry)
        {
            if (entry?.TargetCache == null ||
                entry.SampleData?.enableMask?.root2DPosition != true ||
                entry.SampleData.root2DOverride == null)
            {
                return;
            }

            if (entry.FullBodyTargets == null)
            {
                entry.FullBodyTargets = new Dictionary<HumanBodyBones, GameObject>();
            }

            bool createdTarget = false;
            if (!entry.FullBodyTargets.TryGetValue(HumanBodyBones.Hips, out GameObject target) || target == null)
            {
                target = CreateEffectorGizmo(
                    entry,
                    "__KimodoRoot2DTarget",
                    PrimitiveType.Cube,
                    Color.white);
                entry.FullBodyTargets[HumanBodyBones.Hips] = target;
                createdTarget = true;
            }

            target.transform.SetParent(null, true);
            if (createdTarget || !target.transform.hasChanged)
            {
                target.transform.SetPositionAndRotation(
                    entry.SampleData.root2DOverride.t,
                    entry.SampleData.root2DOverride.q);
                target.transform.hasChanged = false;
            }
            target.transform.localScale = Vector3.one * 0.1f;
            SetEffectorGizmoSelectable(target, entry.PickingEnabled);
            target.SetActive(true);
            target.transform.hasChanged = false;
        }

        private static void DestroyRoot2DTarget(ConstraintPosePreviewEntry entry)
        {
            if (entry?.FullBodyTargets == null ||
                !entry.FullBodyTargets.TryGetValue(HumanBodyBones.Hips, out GameObject target))
            {
                return;
            }
            if (target != null) UnityEngine.Object.DestroyImmediate(target);
            entry.FullBodyTargets.Remove(HumanBodyBones.Hips);
        }

        private static void SetTargetToFollowBodyPart(
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            Transform target,
            Transform bodyPart)
        {
            // FullBody handles represent scene-space effector targets. Their
            // transforms are passed to HumanoidPoseRebuildJob as scene handles.
            target.SetParent(null, true);
            target.SetPositionAndRotation(
                bodyPart.position,
                ResolveEffectorRotation(entry, bone, bodyPart));
        }

        private static Quaternion ResolveEffectorRotation(
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            Transform bodyPart)
        {
            if (entry?.TargetCache == null || bodyPart == null)
            {
                return bodyPart != null ? bodyPart.rotation : Quaternion.identity;
            }

            Quaternion transport = KimodoRetargetMarkerSamplingUtility.ResolveEffectorTransportRotation(
                entry.TargetCache,
                bone,
                bodyPart.rotation,
                bone == HumanBodyBones.LeftFoot || bone == HumanBodyBones.RightFoot ? 1 : 0);
            if (bone == HumanBodyBones.LeftFoot || bone == HumanBodyBones.RightFoot)
            {
                return entry.TargetCache.GetBoneBindWorldRotation(
                    bone,
                    out Quaternion initialFoot)
                    ? (transport * initialFoot).normalized
                    : transport;
            }

            Transform root = entry.TargetCache.skeletonRoot;
            if (root == null ||
                !entry.TargetCache.GetBoneBindWorldRotation(bone, out Quaternion initialWorld))
            {
                return transport;
            }

            Quaternion initialInRoot =
                Quaternion.Inverse(entry.TargetCache.bindSkeletonRootWorldRotation) * initialWorld;
            return (root.rotation * transport * initialInRoot).normalized;
        }

        private static KimodoConstraintEffectors CaptureEffectorsFromEntry(
            ConstraintPosePreviewEntry entry,
            KimodoConstraintMask mask,
            string markerType)
        {
            var result = new KimodoConstraintEffectors();
            if (entry == null || mask == null) return result;
            if (entry.EndEffectorMarker != null)
            {
                HumanBodyBones bone = ResolveEndEffectorBone(markerType);
                KimodoRigidTransform target = new KimodoRigidTransform
                {
                    t = entry.EndEffectorMarker.transform.position,
                    q = entry.EndEffectorMarker.transform.rotation
                };
                switch (bone)
                {
                    case HumanBodyBones.LeftHand: result.leftHand = target; break;
                    case HumanBodyBones.RightHand: result.rightHand = target; break;
                    case HumanBodyBones.LeftFoot: result.leftFoot = target; break;
                    case HumanBodyBones.RightFoot: result.rightFoot = target; break;
                }
            }
            if (entry.FullBodyTargets != null)
            {
                CaptureEffector(entry, mask.leftHand, HumanBodyBones.LeftHand, result);
                CaptureEffector(entry, mask.rightHand, HumanBodyBones.RightHand, result);
                CaptureEffector(entry, mask.leftFoot, HumanBodyBones.LeftFoot, result);
                CaptureEffector(entry, mask.rightFoot, HumanBodyBones.RightFoot, result);
            }
            return result;
        }

        private static void CaptureEffector(
            ConstraintPosePreviewEntry entry,
            bool enabled,
            HumanBodyBones bone,
            KimodoConstraintEffectors effectors)
        {
            if (!enabled || effectors == null ||
                !entry.FullBodyTargets.TryGetValue(bone, out GameObject target) || target == null)
            {
                return;
            }
            var value = new KimodoRigidTransform
            {
                t = target.transform.position,
                q = target.transform.rotation
            };
            switch (bone)
            {
                case HumanBodyBones.LeftHand: effectors.leftHand = value; break;
                case HumanBodyBones.RightHand: effectors.rightHand = value; break;
                case HumanBodyBones.LeftFoot: effectors.leftFoot = value; break;
                case HumanBodyBones.RightFoot: effectors.rightFoot = value; break;
            }
        }

        private static bool TryGetEffector(
            KimodoConstraintEffectors targets,
            HumanBodyBones bone,
            out Vector3 position,
            out Quaternion rotation)
        {
            KimodoRigidTransform value = null;
            if (targets != null)
            {
                value = bone switch
                {
                    HumanBodyBones.LeftHand => targets.leftHand,
                    HumanBodyBones.RightHand => targets.rightHand,
                    HumanBodyBones.LeftFoot => targets.leftFoot,
                    HumanBodyBones.RightFoot => targets.rightFoot,
                    _ => null
                };
            }
            position = value != null ? value.t : Vector3.zero;
            rotation = value != null ? value.q : Quaternion.identity;
            return value != null;
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
            KimodoConstraintMask mask = KimodoConstraintMask.FromSample(entry.SampleData);
            foreach (KeyValuePair<HumanBodyBones, GameObject> item in entry.FullBodyTargets)
            {
                if (HasFullBodyTargetTransformChanged(entry, item.Key, item.Value, mask))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasFullBodyTargetTransformChanged(
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            GameObject target,
            KimodoConstraintMask mask)
        {
            Transform transform = target != null ? target.transform : null;
            if (transform == null || !transform.hasChanged) return false;
            // Target refreshes clear hasChanged; a remaining flag is a Scene drag.
            return true;
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
