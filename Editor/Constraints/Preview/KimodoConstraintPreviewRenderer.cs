using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal enum ConstraintPreviewSemantic
    {
        ExistingFullBodyPreview,
        InOutPosePreview
    }

    internal enum PreviewColorMode
    {
        Source,
        MultiplyTint,
        Override
    }

    internal readonly struct ConstraintPreviewContext
    {
        public readonly int ClipId;
        public readonly int AnimatorId;
        public readonly int TrackId;
        public readonly string ModelName;
        public readonly KimodoConstraintRigType RigType;
        public readonly Avatar SourceAvatar;
        public readonly string PreviewKey;

        public ConstraintPreviewContext(
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
            PreviewKey = KimodoConstraintMarkerEditorUtility.GetCachedIntString(clipId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(animatorId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(trackId) + ":" +
                KimodoConstraintMarkerEditorUtility.GetCachedIntString(KimodoUnityObjectIdUtility.IdHash(sourceAvatar));
            }
        }

    internal class ConstraintPreviewItem
    {
        public string EntryId;
        public KimodoMarkerSampleResult SampleData;
        public string ConstraintType;
        public KimodoConstraintMode ConstraintMode = KimodoConstraintMode.FullBody;
        public ConstraintPreviewSemantic PreviewSemantic = ConstraintPreviewSemantic.ExistingFullBodyPreview;
        public bool HandlesEnabled;
        public List<string> HighlightJoints;
        public Color PreviewColor = Color.white;
        public PreviewColorMode ColorMode = PreviewColorMode.Source;
        public bool Visible = true;
        public Action<KimodoMarkerSampleResult> OnSampleChanged;
    }

    // Generic preview input. The renderer does not know whether the request
    // came from the Inspector, EditWindow, or another editor surface.
    internal sealed class ConstraintPreviewRequest : ConstraintPreviewItem
    {
    }

    internal sealed class ConstraintPreviewInstance
    {
        public string Key;
        public Transform Root;
        public RetargetSkeleton TargetSkeleton;
        public List<Material> GeneratedMaterials;
        public KimodoConstraintMode ConstraintMode = KimodoConstraintMode.FullBody;
        public ConstraintPreviewSemantic PreviewSemantic = ConstraintPreviewSemantic.ExistingFullBodyPreview;
        public bool HandlesEnabled;
        // Current frame sample used to rebuild the preview rig. This is not a
        // Current frame sample for the active preview instance.
        public KimodoMarkerSampleResult SampleData;
        public bool PickingEnabled;
        public bool ShowVirtualAvatar = true;
        public bool Visible = true;
        public Action<KimodoMarkerSampleResult> OnSampleChanged;
    }

    internal sealed class ConstraintPreviewScope : IDisposable
    {
        internal readonly Dictionary<string, ConstraintPreviewInstance> Entries =
            new Dictionary<string, ConstraintPreviewInstance>(StringComparer.Ordinal);

        internal ConstraintPreviewContext Context { get; }
        internal bool IsDisposed { get; private set; }

        internal ConstraintPreviewScope(ConstraintPreviewContext context)
        {
            Context = context;
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                KimodoConstraintPreviewRenderer.DestroyBatch(this);
            }
        }

        internal void MarkDisposed()
        {
            IsDisposed = true;
        }
    }

    [InitializeOnLoad]
    internal static class KimodoConstraintPreviewRenderer
    {
        // Active preview instances only. Entries are never reused between
        // renders; a refresh always rebuilds the requested pose from scratch.
        private static readonly Dictionary<string, ConstraintPreviewScope> ActiveScopes =
            new Dictionary<string, ConstraintPreviewScope>(StringComparer.Ordinal);
        private static string selectedHandleKey;

        private const float NonConstraintAlpha = 1.0f;
        private const float HighlightAlpha = 1.0f;
        private static readonly Color NonConstraintColor = new Color(1f, 1f, 1f, NonConstraintAlpha);
        private static readonly Color HighlightColor = new Color(1f, 0f, 0f, HighlightAlpha);
        private static readonly Color LeftTargetColor = new Color(0.18f, 0.48f, 0.96f);
        private static readonly Color RightTargetColor = new Color(0.94f, 0.22f, 0.22f);
        private const float EndEffectorTargetSize = 0.05f;

        static KimodoConstraintPreviewRenderer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += DestroyAll;
            SceneView.duringSceneGui += DrawControllerHandles;
        }

        private static void DrawControllerHandles(SceneView _)
        {
            Handles.BeginGUI();
            GUI.Label(
                new Rect(10f, 10f, 500f, 20f),
                "Selected Handle: " + (selectedHandleKey ?? "<none>"));
            Handles.EndGUI();

            foreach (ConstraintPreviewScope session in ActiveScopes.Values)
            {
                if (session?.IsDisposed == true) continue;
                foreach (ConstraintPreviewInstance entry in session.Entries.Values)
                {
                    if (entry == null || !entry.HandlesEnabled || !entry.Visible || entry.SampleData == null)
                    {
                        continue;
                    }

                    KimodoConstraintMask mask = entry.SampleData.enableMask;
                    if (entry.SampleData.rootOverride != null)
                    {
                        DrawSampleHandle(
                            entry,
                            HumanBodyBones.Hips,
                            entry.SampleData.rootOverride,
                            Color.white,
                            "Root Override",
                            isRoot: true);
                    }

                    if (entry.ConstraintMode == KimodoConstraintMode.Root2D ||
                        entry.SampleData.effectors == null)
                    {
                        continue;
                    }

                    bool showAllEffectors = entry.ConstraintMode == KimodoConstraintMode.FullBody;
                    DrawEffectorHandle(entry, HumanBodyBones.LeftHand, entry.SampleData.effectors.leftHand,
                        showAllEffectors || mask?.leftHand == true);
                    DrawEffectorHandle(entry, HumanBodyBones.RightHand, entry.SampleData.effectors.rightHand,
                        showAllEffectors || mask?.rightHand == true);
                    DrawEffectorHandle(entry, HumanBodyBones.LeftFoot, entry.SampleData.effectors.leftFoot,
                        showAllEffectors || mask?.leftFoot == true);
                    DrawEffectorHandle(entry, HumanBodyBones.RightFoot, entry.SampleData.effectors.rightFoot,
                        showAllEffectors || mask?.rightFoot == true);
                }
            }
        }

        private static void DrawEffectorHandle(
            ConstraintPreviewInstance entry,
            HumanBodyBones bone,
            KimodoRigidTransform value,
            bool enabled)
        {
            if (!enabled || value == null) return;
            DrawSampleHandle(entry, bone, value, TargetColor(bone), bone.ToString(), isRoot: false);
        }

        private static void DrawSampleHandle(
            ConstraintPreviewInstance entry,
            HumanBodyBones bone,
            KimodoRigidTransform value,
            Color color,
            string label,
            bool isRoot)
        {
            Vector3 position = value.position;
            Quaternion rotation = ResolveHandleRotation(entry, bone, value.rotation);
            float size = isRoot
                ? Mathf.Max(0.1f, HandleUtility.GetHandleSize(position) * 0.1f)
                : Mathf.Max(EndEffectorTargetSize, HandleUtility.GetHandleSize(position) * 0.09f);
            Handles.color = color;
            Handles.CapFunction cap = isRoot || bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand
                ? Handles.SphereHandleCap
                : Handles.CubeHandleCap;
            string handleKey = (entry.Key ?? string.Empty) + ":" + bone;
            bool selected = string.Equals(selectedHandleKey, handleKey, StringComparison.Ordinal);

            if (selected)
            {
                Handles.color = Color.yellow;
                Handles.DrawWireDisc(position, Vector3.up, size * 1.5f);
                Handles.Label(position + Vector3.up * size * 2f, "SELECTED " + handleKey);
            }

            if (!selected)
            {
                // The first click selects the value; subsequent events draw
                // Unity's native position/rotation tools for that value.
                int controlId = GUIUtility.GetControlID(FocusType.Passive);
                Event currentEvent = Event.current;
                bool mouseDown = currentEvent != null &&
                    currentEvent.type == EventType.MouseDown &&
                    currentEvent.button == 0;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(
                    controlId,
                    position,
                    size,
                    Vector3.zero,
                    cap);
                if (mouseDown &&
                    (GUIUtility.hotControl == controlId ||
                     HandleUtility.nearestControl == controlId))
                {
                    selectedHandleKey = handleKey;
                    GUIUtility.hotControl = controlId;
                    // Repaint so this value changes from FreeMoveHandle to
                    // the combined TransformHandle on the next SceneView pass.
                    SceneView.RepaintAll();
                }
                if (EditorGUI.EndChangeCheck())
                {
                    value.position = moved;
                    PromoteHandleChannel(entry.SampleData, bone, rotationChanged: false);
                    entry.OnSampleChanged?.Invoke(entry.SampleData.Clone());
                }
            }
            else
            {
                cap(
                    GUIUtility.GetControlID(FocusType.Passive),
                    position,
                    rotation,
                    size,
                    EventType.Repaint);

                // This is a value-backed handle, not a selected Transform, so
                // use Unity's combined native Transform gizmo explicitly.
                EditorGUI.BeginChangeCheck();
                Quaternion previousRotation = rotation;
                Handles.TransformHandle(ref position, ref rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    bool rotationChanged = Quaternion.Angle(previousRotation, rotation) > 1e-4f;
                    value.position = position;
                    value.rotation = ResolveStoredHandRotation(entry, bone, rotation);
                    PromoteHandleChannel(entry.SampleData, bone, rotationChanged);
                    entry.OnSampleChanged?.Invoke(entry.SampleData.Clone());
                }
            }

            Handles.Label(position + Vector3.up * size, label);
        }

        private static Quaternion ResolveHandleRotation(
            ConstraintPreviewInstance entry,
            HumanBodyBones bone,
            Quaternion storedRotation)
        {
            if (!IsHand(bone) || entry?.TargetSkeleton == null ||
                !entry.TargetSkeleton.GetBoneBindWorldRotation(bone, out Quaternion initialWorld))
            {
                return storedRotation;
            }

            // Hand effector rotations are stored as currentWorld * inverse(bindWorld).
            // Scene handles should display the corresponding absolute bone rotation.
            return (storedRotation * initialWorld).normalized;
        }

        private static Quaternion ResolveStoredHandRotation(
            ConstraintPreviewInstance entry,
            HumanBodyBones bone,
            Quaternion handleRotation)
        {
            if (!IsHand(bone) || entry?.TargetSkeleton == null ||
                !entry.TargetSkeleton.GetBoneBindWorldRotation(bone, out Quaternion initialWorld))
            {
                return handleRotation.normalized;
            }

            return (handleRotation * Quaternion.Inverse(initialWorld)).normalized;
        }

        private static bool IsHand(HumanBodyBones bone) =>
            bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand;

        private static void PromoteHandleChannel(
            KimodoMarkerSampleResult sample,
            HumanBodyBones bone,
            bool rotationChanged)
        {
            if (sample == null) return;
            sample.enableMask ??= new KimodoConstraintMask();
            sample.validMask ??= new KimodoConstraintMask();
            switch (bone)
            {
                case HumanBodyBones.Hips:
                    sample.enableMask.rootPosition = true;
                    sample.enableMask.rootHeading |= rotationChanged;
                    sample.validMask.rootPosition = true;
                    sample.validMask.rootHeading |= rotationChanged;
                    break;
                case HumanBodyBones.LeftHand: sample.enableMask.leftHand = sample.validMask.leftHand = true; break;
                case HumanBodyBones.RightHand: sample.enableMask.rightHand = sample.validMask.rightHand = true; break;
                case HumanBodyBones.LeftFoot: sample.enableMask.leftFoot = sample.validMask.leftFoot = true; break;
                case HumanBodyBones.RightFoot: sample.enableMask.rightFoot = sample.validMask.rightFoot = true; break;
            }
        }

        internal static bool CreatePreviewScope(
            ConstraintPreviewContext context,
            out ConstraintPreviewScope batch,
            out string error)
        {
            batch = null;
            error = string.Empty;
            if (context.ClipId == 0 || context.AnimatorId == 0 || context.TrackId == 0)
            {
                error = "invalid clip/animator/track context";
                return false;
            }

            // Discard the previous graph/rig. Sampling and solving always
            // reflect the current marker.
            if (ActiveScopes.TryGetValue(context.PreviewKey, out ConstraintPreviewScope existing))
                DestroyBatch(existing, repaint: false);
            batch = new ConstraintPreviewScope(context);
            ActiveScopes[context.PreviewKey] = batch;
            return true;
        }

        internal static void DestroyBatch(ConstraintPreviewScope session, bool repaint = true)
        {
            if (session == null) return;
            if (ActiveScopes.TryGetValue(session.Context.PreviewKey, out ConstraintPreviewScope active) &&
                ReferenceEquals(active, session))
                ActiveScopes.Remove(session.Context.PreviewKey);
            DestroyBatchEntries(session);
            session.MarkDisposed();
            if (repaint) SceneView.RepaintAll();
        }

        private static bool TryGetBatch(
            ConstraintPreviewContext context,
            out ConstraintPreviewScope session)
        {
            return ActiveScopes.TryGetValue(context.PreviewKey, out session) &&
                session != null && !session.IsDisposed;
        }

        internal static bool RenderPreview(
            ConstraintPreviewContext context,
            IReadOnlyList<ConstraintPreviewItem> items,
            out string error,
            string entryPrefix = null)
        {
            error = string.Empty;
            if (!CreatePreviewScope(context, out ConstraintPreviewScope session, out error))
            {
                return false;
            }

            Dictionary<string, ConstraintPreviewInstance> entries = session.Entries;
            string normalizedPrefix = entryPrefix ?? string.Empty;
            if (items == null || items.Count == 0)
            {
                DestroyBatch(session);
                return true;
            }

            bool hasVisible = false;
            for (int i = 0; i < items.Count; i++)
            {
                ConstraintPreviewItem item = items[i];
                if (item != null && item.Visible && item.SampleData != null)
                {
                    hasVisible = true;
                    break;
                }
            }

            if (!hasVisible)
            {
                DestroyBatch(session);
                return true;
            }

            var desiredKeys = new HashSet<string>(StringComparer.Ordinal);
            bool changed = false;
            for (int i = 0; i < items.Count; i++)
            {
                ConstraintPreviewItem item = items[i];
                if (item == null || !item.Visible || item.SampleData == null)
                {
                    continue;
                }

                string entryId = normalizedPrefix +
                    (string.IsNullOrWhiteSpace(item.EntryId) ? $"item_{i}" : item.EntryId.Trim());
                desiredKeys.Add(entryId);

                if (entries.TryGetValue(entryId, out ConstraintPreviewInstance stale))
                {
                    DestroyEntry(stale);
                    entries.Remove(entryId);
                }

                if (!CreateInstance(session, entryId, out ConstraintPreviewInstance entry, out error))
                {
                    return false;
                }

                entry.ConstraintMode = item.ConstraintMode;
                entry.PreviewSemantic = item.PreviewSemantic;
                entry.HandlesEnabled = item.HandlesEnabled;
                entry.Visible = item.Visible;
                entry.OnSampleChanged = item.OnSampleChanged;
                entry.ShowVirtualAvatar = true;

                entry.SampleData = item.SampleData.Clone();
                var highlightedJoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectHighlightedJointsFromItem(item, context.ModelName, highlightedJoints);

                bool applied = ApplySampleToRig(
                    KimodoConstraintSampleComposer.ResolveUnifiedSample(item.SampleData),
                    context.ModelName,
                    entry,
                    out error);
                if (!applied)
                {
                    error = $"constraint preview render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}): {error}";
                    return false;
                }

                ApplyConstraintColoring(entry, highlightedJoints, item.PreviewColor, item.ColorMode);
                changed = true;
                changed |= SetEntryVisible(entry, true);
            }

            List<string> keysToRemove = null;
            foreach (KeyValuePair<string, ConstraintPreviewInstance> kv in entries)
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

        internal static bool RenderConstraintPreview(
            ConstraintPreviewContext context,
            ConstraintPreviewRequest request,
            out string error)
        {
            return RenderPreview(
                context,
                request == null ? null : new ConstraintPreviewItem[] { request },
                out error);
        }

        internal static void SetGroupState(ConstraintPreviewContext context, bool visible, bool selectable)
        {
            if (!TryGetBatch(context, out ConstraintPreviewScope session))
            {
                return;
            }

            foreach (KeyValuePair<string, ConstraintPreviewInstance> kv in session.Entries)
            {
                ApplyEntryState(kv.Value, visible, selectable);
            }

            SceneView.RepaintAll();
        }

        internal static bool TryGetPreviewRoot(ConstraintPreviewContext context, string entryId, out Transform root)
        {
            root = null;
            if (!TryGetBatch(context, out ConstraintPreviewScope session) ||
                !TryGetEntryForContext(session, entryId, out ConstraintPreviewInstance entry) ||
                entry?.Root == null)
            {
                return false;
            }

            root = entry.Root;
            return true;
        }

        internal static void DestroyEntry(ConstraintPreviewContext context, string entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId) ||
                !TryGetBatch(context, out ConstraintPreviewScope session))
            {
                return;
            }

            string key = entryId.Trim();
            if (!session.Entries.TryGetValue(key, out ConstraintPreviewInstance entry))
            {
                return;
            }

            DestroyEntry(entry);
            session.Entries.Remove(key);
            SceneView.RepaintAll();
        }

        internal static void DestroyScope(ConstraintPreviewContext context)
        {
            if (ActiveScopes.TryGetValue(context.PreviewKey, out ConstraintPreviewScope scope))
                DestroyBatch(scope);
        }

        internal static void DestroyAll()
        {
            var batches = new List<ConstraintPreviewScope>(ActiveScopes.Values);
            ActiveScopes.Clear();
            for (int i = 0; i < batches.Count; i++)
            {
                DestroyBatchEntries(batches[i]);
                batches[i]?.MarkDisposed();
            }

            SceneView.RepaintAll();
        }

        private static void DestroyBatchEntries(ConstraintPreviewScope session)
        {
            if (session == null)
            {
                return;
            }

            foreach (ConstraintPreviewInstance entry in session.Entries.Values)
            {
                DestroyEntry(entry);
            }

            session.Entries.Clear();
        }

        private static bool IsEntryInScope(ConstraintPreviewInstance entry, string entryPrefix)
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

        private static bool CreateInstance(
            ConstraintPreviewScope session,
            string entryId,
            out ConstraintPreviewInstance entry,
            out string error)
        {
            entry = null;
            error = string.Empty;
            ConstraintPreviewContext context = session.Context;
            if (context.ClipId == 0 || context.AnimatorId == 0)
            {
                error = "invalid clip/animator id";
                return false;
            }

            string normalizedEntryId = string.IsNullOrWhiteSpace(entryId) ? "default" : entryId.Trim();

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

            entry = new ConstraintPreviewInstance
            {
                Key = normalizedEntryId,
                Root = rigInstance.Root != null ? rigInstance.Root.transform : null,
                TargetSkeleton = rigInstance.TargetCache,
                GeneratedMaterials = rigInstance.GeneratedMaterials,
                PickingEnabled = false
            };

            session.Entries[normalizedEntryId] = entry;
            SetEntrySelectable(entry, false);
            return true;
        }

        private static bool TryGetFirstEntryForContext(
            ConstraintPreviewScope session,
            out ConstraintPreviewInstance entry)
        {
            entry = null;
            foreach (KeyValuePair<string, ConstraintPreviewInstance> kv in session.Entries)
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
            ConstraintPreviewScope session,
            string entryId,
            out ConstraintPreviewInstance entry)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return TryGetFirstEntryForContext(session, out entry);
            }

            return session.Entries.TryGetValue(entryId.Trim(), out entry) && entry?.Root != null;
        }

        private static void DestroyEntry(ConstraintPreviewInstance entry)
        {
            if (entry == null)
            {
                return;
            }
            KimodoConstraintPoseRigFactory.DisposePoseRig(new KimodoConstraintPoseRigFactory.PoseRigInstance
            {
                Root = entry.Root != null ? entry.Root.gameObject : null,
                TargetCache = entry.TargetSkeleton,
                GeneratedMaterials = entry.GeneratedMaterials
            });
            entry.Root = null;
            entry.TargetSkeleton = null;
            entry.GeneratedMaterials = null;

        }

        private static bool SetEntryVisible(ConstraintPreviewInstance entry, bool visible)
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
            entry.Visible = visible;
            return changed;
        }

        private static void SetEntrySelectable(ConstraintPreviewInstance entry, bool selectable)
        {
            if (entry?.Root == null || entry.Root.gameObject == null)
            {
                return;
            }

            if (entry.PickingEnabled == selectable) return;

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
        }

        private static void ApplyEntryState(ConstraintPreviewInstance entry, bool visible, bool selectable)
        {
            if (entry == null)
            {
                return;
            }

            SetEntryVisible(entry, visible);
            SetEntrySelectable(entry, selectable);
        }

        private static void ApplyConstraintColoring(
            ConstraintPreviewInstance entry,
            HashSet<string> highlightedJoints,
            Color previewColor,
            PreviewColorMode colorMode)
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

                    if (!highlighted && colorMode == PreviewColorMode.Source)
                    {
                        renderer.SetPropertyBlock(null, m);
                        continue;
                    }

                    Color sourceColor = ResolveSourceColor(mat);
                    Color tint = previewColor == default ? NonConstraintColor : previewColor;
                    Color color = highlighted
                        ? HighlightColor
                        : colorMode == PreviewColorMode.Override
                            ? tint
                        : new Color(
                            sourceColor.r * tint.r,
                            sourceColor.g * tint.g,
                            sourceColor.b * tint.b,
                            sourceColor.a * tint.a);
                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block, m);
                    if (mat.HasProperty("_BaseColor")) block.SetColor("_BaseColor", color);
                    else if (mat.HasProperty("_Color")) block.SetColor("_Color", color);
                    else if (mat.HasProperty("_TintColor")) block.SetColor("_TintColor", color);
                    else continue;
                    renderer.SetPropertyBlock(block, m);
                }
            }
        }

        private static Color ResolveSourceColor(Material material)
        {
            if (material == null) return Color.white;
            if (material.HasProperty("_BaseColor")) return material.GetColor("_BaseColor");
            if (material.HasProperty("_Color")) return material.GetColor("_Color");
            if (material.HasProperty("_TintColor")) return material.GetColor("_TintColor");
            return Color.white;
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

        private static void CollectHighlightedJointsFromItem(ConstraintPreviewItem item, string modelName, HashSet<string> output)
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
            ConstraintPreviewInstance entry,
            out string error)
        {
            return KimodoConstraintPoseRigFactory.TryApplyPose(
                new KimodoConstraintPoseRigFactory.PoseRigInstance
                {
                    Root = entry?.Root != null ? entry.Root.gameObject : null,
                    TargetCache = entry?.TargetSkeleton,
                    GeneratedMaterials = entry?.GeneratedMaterials
                },
                sample,
                modelName,
                out error);
        }

        private static Color TargetColor(HumanBodyBones bone) =>
            bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.LeftFoot
                ? LeftTargetColor
                : RightTargetColor;

    }
}
