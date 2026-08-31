using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal enum ConstraintPreviewSemantic
    {
        ExistingFullBodyPreview,
        InOutPosePreview
    }

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

    internal class PoseCacheRenderItem
    {
        public string EntryId;
        public KimodoMarkerSampleResult SampleData;
        public string ConstraintType;
        public KimodoConstraintMode ConstraintMode = KimodoConstraintMode.FullBody;
        public ConstraintPreviewSemantic PreviewSemantic = ConstraintPreviewSemantic.ExistingFullBodyPreview;
        public bool HandlesEnabled;
        public List<string> HighlightJoints;
        public Color PreviewColor = Color.white;
        public bool Visible = true;
        public Action<KimodoMarkerSampleResult> OnSampleChanged;
    }

    // Generic preview input. The renderer does not know whether the request
    // came from the Inspector, EditWindow, or another editor surface.
    internal sealed class ConstraintPreviewRequest : PoseCacheRenderItem
    {
    }

    internal sealed class ConstraintPosePreviewEntry
    {
        public string Key;
        public Transform Root;
        public RetargetSkeleton TargetCache;
        public List<Material> GeneratedMaterials;
        public KimodoConstraintMode ConstraintMode = KimodoConstraintMode.FullBody;
        public ConstraintPreviewSemantic PreviewSemantic = ConstraintPreviewSemantic.ExistingFullBodyPreview;
        public bool HandlesEnabled;
        // Current frame sample used to rebuild the preview rig. This is not a
        // sampling cache; it is replaced on every RenderBatch pass.
        public KimodoMarkerSampleResult SampleData;
        public bool PickingEnabled;
        public bool ShowVirtualAvatar = true;
        public bool Visible = true;
        public Action<KimodoMarkerSampleResult> OnSampleChanged;
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

    [InitializeOnLoad]
    internal static class KimodoConstraintPoseCache
    {
        private static readonly Dictionary<string, ConstraintPosePreviewSession> Sessions =
            new Dictionary<string, ConstraintPosePreviewSession>(StringComparer.Ordinal);
        private static bool invalidContextCleanupQueued;
        private static string selectedHandleKey;

        private const float NonConstraintAlpha = 1.0f;
        private const float HighlightAlpha = 1.0f;
        private static readonly Color NonConstraintColor = new Color(1f, 1f, 1f, NonConstraintAlpha);
        private static readonly Color HighlightColor = new Color(1f, 0f, 0f, HighlightAlpha);
        private static readonly Color LeftTargetColor = new Color(0.18f, 0.48f, 0.96f);
        private static readonly Color RightTargetColor = new Color(0.94f, 0.22f, 0.22f);
        private const float EndEffectorTargetSize = 0.05f;

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
            Handles.BeginGUI();
            GUI.Label(
                new Rect(10f, 10f, 500f, 20f),
                "Selected Handle: " + (selectedHandleKey ?? "<none>"));
            Handles.EndGUI();

            foreach (ConstraintPosePreviewSession session in Sessions.Values)
            {
                if (session?.IsDisposed == true) continue;
                foreach (ConstraintPosePreviewEntry entry in session.Entries.Values)
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
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            KimodoRigidTransform value,
            bool enabled)
        {
            if (!enabled || value == null) return;
            DrawSampleHandle(entry, bone, value, TargetColor(bone), bone.ToString(), isRoot: false);
        }

        private static void DrawSampleHandle(
            ConstraintPosePreviewEntry entry,
            HumanBodyBones bone,
            KimodoRigidTransform value,
            Color color,
            string label,
            bool isRoot)
        {
            // Root2D's control point is shown at the preview root node while
            // the canonical payload remains the Hips/root world position.
            // Keep this display-only Y offset out of the authored payload.
            bool root2DHandle = isRoot && entry?.ConstraintMode == KimodoConstraintMode.Root2D;
            float yOffset = root2DHandle && entry?.Root != null
                ? entry.Root.position.y - value.position.y
                : 0f;
            Vector3 position = value.position + (root2DHandle ? Vector3.up * yOffset : Vector3.zero);
            Quaternion rotation = value.rotation;
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
                    value.position = moved - (root2DHandle ? Vector3.up * yOffset : Vector3.zero);
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
                    value.position = position - (root2DHandle ? Vector3.up * yOffset : Vector3.zero);
                    value.rotation = rotation.normalized;
                    PromoteHandleChannel(entry.SampleData, bone, rotationChanged);
                    entry.OnSampleChanged?.Invoke(entry.SampleData.Clone());
                }
            }

            Handles.Label(position + Vector3.up * size, label);
        }

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
                    error = $"pose cache render failed for entry '{entryId}' (constraint='{item.ConstraintType ?? string.Empty}', sampleTime={item.SampleData.sampleTime:F3}): {error}";
                    return false;
                }

                ApplyConstraintColoring(entry, highlightedJoints, item.PreviewColor);
                changed = true;
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

        internal static bool RenderConstraintPreview(
            PoseCacheRenderContext context,
            ConstraintPreviewRequest request,
            out string error)
        {
            return RenderBatch(
                context,
                request == null ? null : new PoseCacheRenderItem[] { request },
                out error);
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
            entry.Visible = visible;
            return changed;
        }

        private static void SetEntrySelectable(ConstraintPosePreviewEntry entry, bool selectable)
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
            out string error)
        {
            error = string.Empty;
            if (sample == null || entry?.TargetCache == null)
            {
                error = "Constraint target skeleton cache is unavailable.";
                return false;
            }

            bool wasActive = entry.TargetCache.root.activeSelf;
            entry.TargetCache.root.SetActive(true);
            try
            {
                return KimodoConstraintPosePipeline.TryApply(
                    sample,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                    entry.TargetCache,
                    out _,
                    out _,
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

        private static Color TargetColor(HumanBodyBones bone) =>
            bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.LeftFoot
                ? LeftTargetColor
                : RightTargetColor;

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
