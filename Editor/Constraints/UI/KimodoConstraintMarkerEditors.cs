using System;
using System.Collections.Generic;
using System.Globalization;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoRoot2DConstraintEditorGUI
    {
        internal static float ResolveRotationY(Vector2 heading)
        {
            return heading.sqrMagnitude > 1e-8f
                ? Mathf.Atan2(heading.x, heading.y) * Mathf.Rad2Deg
                : 0f;
        }

        internal static Vector2 ResolveHeading(float rotationY)
        {
            float radians = rotationY * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        }

        internal static void Draw(SerializedObject serializedObject)
        {
            SerializedProperty positionProp = serializedObject.FindProperty("sampleData.kimodoRootPosition");
            if (positionProp != null)
            {
                Vector3 position = positionProp.vector3Value;
                EditorGUI.BeginChangeCheck();
                float forward = EditorGUILayout.FloatField(
                    new GUIContent("Forward", "Signed ground-plane position along canonical Unity +Z."),
                    position.z);
                float rightward = EditorGUILayout.FloatField(
                    new GUIContent("Rightward", "Signed ground-plane position along canonical Unity +X."),
                    position.x);
                if (EditorGUI.EndChangeCheck())
                {
                    position.x = rightward;
                    position.z = forward;
                    positionProp.vector3Value = position;
                }
            }

            SerializedProperty includeHeadingProp = serializedObject.FindProperty("sampleData.hasRootHeading");
            if (includeHeadingProp == null)
            {
                return;
            }

            EditorGUILayout.PropertyField(
                includeHeadingProp,
                new GUIContent("Constrain Rotation Y", "Constrain the absolute world yaw around Unity Y."));
            if (!includeHeadingProp.boolValue)
            {
                return;
            }

            SerializedProperty headingProp = serializedObject.FindProperty("sampleData.rootHeading");
            if (headingProp == null)
            {
                return;
            }

            EditorGUI.BeginChangeCheck();
            float rotationY = EditorGUILayout.FloatField(
                new GUIContent("Rotation Y", "Absolute world yaw in degrees around Unity Y."),
                ResolveRotationY(headingProp.vector2Value));
            if (EditorGUI.EndChangeCheck())
            {
                headingProp.vector2Value = ResolveHeading(rotationY);
            }

            KimodoConstraintHeadingPreviewGUI.Draw(headingProp.vector2Value, enabled: true);
        }
    }

    [InitializeOnLoad]
    internal static class KimodoConstraintSelectionPreviewTool
    {
        private const string EntryPrefix = "selection:";
        private const double PollIntervalSeconds = 0.2d;
        private static readonly Color SinglePreviewColor = Color.white;
        private static readonly Color[] Palette =
        {
            new Color(0.55f, 0.78f, 0.9f),
            new Color(0.92f, 0.78f, 0.45f),
            new Color(0.88f, 0.58f, 0.82f),
            new Color(0.58f, 0.88f, 0.62f),
            new Color(0.9f, 0.62f, 0.48f),
            new Color(0.68f, 0.68f, 0.9f)
        };

        private sealed class PreviewSource
        {
            public IKimodoConstraintPreviewSelectable Selectable;
            public UnityEngine.Object Object;
            public TimelineClip TimelineClip;
            public double Time;
        }

        private sealed class PreviewGroup
        {
            public PoseCacheRenderContext Context;
            public readonly List<PoseCacheRenderItem> Items = new List<PoseCacheRenderItem>();
        }

        private sealed class PreviewLabel
        {
            public PoseCacheRenderContext Context;
            public string EntryId;
            public string Text;
            public Color Color;
            public int Priority;
            public double Time;
        }

        private static readonly Dictionary<string, PoseCacheRenderContext> RenderedContexts =
            new Dictionary<string, PoseCacheRenderContext>(StringComparer.Ordinal);
        private static readonly List<PreviewLabel> Labels = new List<PreviewLabel>();
        private static bool refreshQueued;
        private static bool forceRefreshRequested;
        private static double nextPollTime;
        private static int selectionSignature;

        static KimodoConstraintSelectionPreviewTool()
        {
            Selection.selectionChanged += ScheduleRefresh;
            Undo.undoRedoPerformed += ScheduleRefresh;
            EditorApplication.update += PollSelection;
            EditorApplication.quitting += Clear;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            SceneView.duringSceneGui += DrawLabels;
            ScheduleRefresh();
        }

        internal static void ScheduleRefresh()
        {
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            EditorApplication.delayCall += Refresh;
        }

        internal static void ForceRefresh()
        {
            forceRefreshRequested = true;
            ScheduleRefresh();
        }

        private static void PollSelection()
        {
            if (EditorApplication.timeSinceStartup < nextPollTime)
            {
                return;
            }

            nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            if (ComputeSelectionSignature() != selectionSignature)
            {
                ScheduleRefresh();
            }
        }

        private static void OnSceneClosing(Scene _, bool __)
        {
            ClearSceneSamplingState();
        }

        private static void OnActiveSceneChanged(Scene _, Scene __)
        {
            ClearSceneSamplingState();
            ScheduleRefresh();
        }

        private static void ClearSceneSamplingState()
        {
            KimodoTimelineConstraintClipCache.Clear();
            KimodoConstraintMarkerEditorUtility.ClearSamplingCaches();
            Clear();
            selectionSignature = 0;
        }

        private static void Refresh()
        {
            refreshQueued = false;
            if (forceRefreshRequested)
            {
                forceRefreshRequested = false;
                KimodoTimelineConstraintClipCache.Clear();
                KimodoConstraintMarkerEditorUtility.ClearSamplingCaches();
                Clear();
            }

            List<PreviewSource> sources = CollectSources();
            sources.Sort(CompareSources);

            var groups = new Dictionary<string, PreviewGroup>(StringComparer.Ordinal);
            var selectedMarkerIds = new HashSet<int>();
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i].Object is KimodoConstraintMarkerBase marker)
                {
                    selectedMarkerIds.Add(KimodoUnityObjectIdUtility.IdHash(marker));
                }
            }

            Labels.Clear();
            for (int i = 0; i < sources.Count; i++)
            {
                PreviewSource source = sources[i];
                Color color = ResolvePreviewColor(i);
                if (source.Object is KimodoConstraintMarkerBase marker)
                {
                    AddMarkerPreview(groups, marker, color);
                }
                else if (source.Object is KimodoPlayableClip playable)
                {
                    AddClipPreview(groups, playable, source.TimelineClip, selectedMarkerIds, color);
                }
            }

            foreach (KeyValuePair<string, PoseCacheRenderContext> previous in RenderedContexts)
            {
                if (!groups.ContainsKey(previous.Key))
                {
                    KimodoConstraintPoseCache.DestroyEntriesInScope(previous.Value, EntryPrefix);
                }
            }

            RenderedContexts.Clear();
            foreach (KeyValuePair<string, PreviewGroup> pair in groups)
            {
                PreviewGroup group = pair.Value;
                if (!KimodoConstraintPoseCache.RenderBatch(
                        group.Context,
                        group.Items,
                        out string error,
                        EntryPrefix))
                {
                    Debug.LogWarning($"[Kimodo][ConstraintPreview] {error}");
                    KimodoConstraintPoseCache.DestroyEntriesInScope(group.Context, EntryPrefix);
                    continue;
                }

                RenderedContexts[pair.Key] = group.Context;
            }

            Labels.Sort(CompareLabels);
            selectionSignature = ComputeSelectionSignature();
            SceneView.RepaintAll();
        }

        private static Color ResolvePreviewColor(int index)
        {
            return index == 0 ? SinglePreviewColor : Palette[(index - 1) % Palette.Length];
        }

        private static List<PreviewSource> CollectSources()
        {
            var result = new List<PreviewSource>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips != null)
            {
                for (int i = 0; i < selectedClips.Length; i++)
                {
                    TimelineClip timelineClip = selectedClips[i];
                    if (timelineClip?.asset is KimodoPlayableClip playable)
                    {
                        AddSource(result, keys, playable, timelineClip, GetTimelineClipKey(timelineClip));
                    }
                }
            }

            UnityEngine.Object[] selectedObjects = Selection.objects;
            for (int i = 0; i < selectedObjects.Length; i++)
            {
                UnityEngine.Object selected = selectedObjects[i];
                if (selected is KimodoConstraintMarkerBase marker)
                {
                    AddSource(result, keys, marker, null, "marker:" + KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker));
                }
                else if (selected is KimodoPlayableClip playable)
                {
                    TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(playable);
                    AddSource(result, keys, playable, timelineClip, GetTimelineClipKey(timelineClip));
                }
            }

            return result;
        }

        private static void AddSource(
            List<PreviewSource> result,
            HashSet<string> keys,
            UnityEngine.Object sourceObject,
            TimelineClip timelineClip,
            string key)
        {
            if (!(sourceObject is IKimodoConstraintPreviewSelectable selectable) ||
                !selectable.ConstraintPreviewEnabled ||
                !keys.Add(key))
            {
                return;
            }

            result.Add(new PreviewSource
            {
                Selectable = selectable,
                Object = sourceObject,
                TimelineClip = timelineClip,
                Time = sourceObject is KimodoConstraintMarkerBase marker
                    ? marker.time
                    : timelineClip?.start ?? 0.0
            });
        }

        private static string GetTimelineClipKey(TimelineClip timelineClip)
        {
            if (timelineClip == null)
            {
                return "clip:(none)";
            }

            return $"clip:{KimodoUnityObjectIdUtility.IdHash(timelineClip.GetParentTrack())}:" +
                $"{KimodoUnityObjectIdUtility.IdHash(timelineClip.asset as UnityEngine.Object)}:" +
                $"{timelineClip.start:R}:{timelineClip.duration:R}";
        }

        private static void AddMarkerPreview(
            Dictionary<string, PreviewGroup> groups,
            KimodoConstraintMarkerBase marker,
            Color color)
        {
            if (marker == null || KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker))
            {
                return;
            }

            if (!marker.useOverride &&
                !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(
                    marker,
                    forceRefresh: false,
                    out _))
            {
                return;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                    marker,
                    out PoseCacheRenderContext context,
                    out _) ||
                !KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(
                    marker,
                    marker.SampleData,
                    out KimodoMarkerSampleResult sample,
                    out _))
            {
                return;
            }

            string entryId = "marker:" + KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker);
            AddItem(
                groups,
                context,
                entryId,
                sample,
                marker.ConstraintType,
                KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                marker.ConstraintPreviewName,
                marker.time,
                marker.ConstraintPreviewPriority,
                color);
        }

        private static void AddClipPreview(
            Dictionary<string, PreviewGroup> groups,
            KimodoPlayableClip playable,
            TimelineClip timelineClip,
            HashSet<int> selectedMarkerIds,
            Color color)
        {
            if (playable == null || timelineClip == null ||
                !KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForPlayableClip(
                    playable,
                    out PoseCacheRenderContext context,
                    out _,
                    out _,
                    timelineClip))
            {
                return;
            }

            TrackAsset track = timelineClip.GetParentTrack();
            if (track == null)
            {
                return;
            }

            List<KimodoConstraintMarkerBase> references =
                KimodoTimelineConstraintMarkerSampler.CollectMarkersForClip(track, timelineClip);
            string clipKey = GetTimelineClipKey(timelineClip);
            foreach (KimodoConstraintMarkerBase marker in references)
            {
                if (selectedMarkerIds.Contains(KimodoUnityObjectIdUtility.IdHash(marker)))
                {
                    continue;
                }

                if (!marker.useOverride &&
                    !KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(
                        marker,
                        forceRefresh: false,
                        out _))
                {
                    continue;
                }

                if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(
                        marker,
                        marker.SampleData,
                        out KimodoMarkerSampleResult sample,
                        out _))
                {
                    continue;
                }

                AddItem(
                    groups,
                    context,
                    $"{clipKey}:marker:{KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker)}",
                    sample,
                    marker.ConstraintType,
                    KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                    $"{playable.ConstraintPreviewName} · {marker.ConstraintType}",
                    marker.time,
                    playable.ConstraintPreviewPriority,
                    color);
            }

            int frameCount = Mathf.Max(
                1,
                KimodoFrameTimeUtility.SecondsToFrameCount(
                    timelineClip.duration,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(playable.bridgeModelName)));
            if (playable.inOutConstraintMode == KimodoInOutConstraintMode.None ||
                !KimodoInOutConstraintAdapter.TryBuildBoundarySamplesForPreview(
                    timelineClip,
                    playable.inOutConstraintMode,
                    playable.enableInConstraint,
                    playable.enableOutConstraint,
                    KimodoInOutConstraintTools.ClampFrameCount(frameCount),
                    out KimodoMarkerSampleResult begin,
                    out KimodoMarkerSampleResult end,
                    out _))
            {
                return;
            }

            if (begin != null)
            {
                AddItem(
                    groups, context, $"{clipKey}:in", begin, "fullbody", null,
                    $"{playable.ConstraintPreviewName} · In", timelineClip.start,
                    playable.ConstraintPreviewPriority, color);
            }
            if (end != null)
            {
                AddItem(
                    groups, context, $"{clipKey}:out", end, "fullbody", null,
                    $"{playable.ConstraintPreviewName} · Out", timelineClip.start + end.sampleTime,
                    playable.ConstraintPreviewPriority, color);
            }
        }

        private static void AddItem(
            Dictionary<string, PreviewGroup> groups,
            PoseCacheRenderContext context,
            string entryId,
            KimodoMarkerSampleResult sample,
            string constraintType,
            List<string> highlightJoints,
            string name,
            double time,
            int priority,
            Color color)
        {
            if (!groups.TryGetValue(context.ContextKey, out PreviewGroup group))
            {
                group = new PreviewGroup { Context = context };
                groups.Add(context.ContextKey, group);
            }

            group.Items.Add(new PoseCacheRenderItem
            {
                EntryId = entryId,
                SampleData = sample,
                ConstraintType = constraintType,
                HighlightJoints = highlightJoints,
                PreviewColor = color,
                Visible = true
            });
            Labels.Add(new PreviewLabel
            {
                Context = context,
                EntryId = EntryPrefix + entryId,
                Text = $"{time:F3}s · {name}",
                Color = color,
                Priority = priority,
                Time = time
            });
        }

        private static void DrawLabels(SceneView _)
        {
            var positions = new List<Vector3>();
            for (int i = 0; i < Labels.Count; i++)
            {
                PreviewLabel label = Labels[i];
                if (!KimodoConstraintPoseCache.TryGetPreviewRoot(
                        label.Context,
                        label.EntryId,
                        out Transform root))
                {
                    continue;
                }

                int overlap = 0;
                for (int p = 0; p < positions.Count; p++)
                {
                    if ((positions[p] - root.position).sqrMagnitude < 0.04f)
                    {
                        overlap++;
                    }
                }
                positions.Add(root.position);

                var style = new GUIStyle(EditorStyles.boldLabel);
                style.alignment = TextAnchor.MiddleCenter;
                style.normal.textColor = label.Color;
                Handles.Label(
                    root.position + Vector3.down * (0.1f + overlap * 0.08f),
                    label.Text,
                    style);
            }
        }

        private static int CompareSources(PreviewSource left, PreviewSource right)
        {
            int compare = left.Selectable.ConstraintPreviewPriority.CompareTo(right.Selectable.ConstraintPreviewPriority);
            if (compare != 0)
            {
                return compare;
            }
            compare = left.Time.CompareTo(right.Time);
            return compare != 0
                ? compare
                : string.CompareOrdinal(
                    KimodoUnityObjectIdUtility.NameKey(left.Object),
                    KimodoUnityObjectIdUtility.NameKey(right.Object));
        }

        private static int CompareLabels(PreviewLabel left, PreviewLabel right)
        {
            int compare = left.Priority.CompareTo(right.Priority);
            return compare != 0 ? compare : left.Time.CompareTo(right.Time);
        }

        private static int ComputeSelectionSignature()
        {
            unchecked
            {
                int hash = 17;
                UnityEngine.Object[] selectedObjects = Selection.objects;
                for (int i = 0; i < selectedObjects.Length; i++)
                {
                    UnityEngine.Object selected = selectedObjects[i];
                    hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(selected);
                    hash = hash * 31 + (selected != null ? EditorUtility.GetDirtyCount(selected) : 0);
                }

                TimelineClip[] selectedClips = TimelineEditor.selectedClips;
                if (selectedClips != null)
                {
                    for (int i = 0; i < selectedClips.Length; i++)
                    {
                        TimelineClip timelineClip = selectedClips[i];
                        UnityEngine.Object asset = timelineClip?.asset as UnityEngine.Object;
                        hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(asset);
                        hash = hash * 31 + (asset != null ? EditorUtility.GetDirtyCount(asset) : 0);
                        hash = hash * 31 + (timelineClip?.start.GetHashCode() ?? 0);
                        hash = hash * 31 + (timelineClip?.duration.GetHashCode() ?? 0);
                        hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(timelineClip?.GetParentTrack());
                    }
                }

                hash = hash * 31 + (TimelineEditor.inspectedDirector != null
                    ? KimodoUnityObjectIdUtility.IdHash(TimelineEditor.inspectedDirector)
                    : 0);
                return hash;
            }
        }

        private static void Clear()
        {
            foreach (KeyValuePair<string, PoseCacheRenderContext> context in RenderedContexts)
            {
                KimodoConstraintPoseCache.DestroyEntriesInScope(context.Value, EntryPrefix);
            }
            RenderedContexts.Clear();
            Labels.Clear();
        }
    }

    internal abstract class KimodoConstraintStandardMarkerEditorBase : UnityEditor.Editor
    {
        protected abstract string TypeLabel { get; }
        protected abstract string TipText { get; }

        private void OnDisable()
        {
            KimodoConstraintMarkerEditorUtility.ClearMarkerPoseCachePreview(target as KimodoConstraintMarkerBase, keepIfOverrideWindowOpen: true);
        }

        public override void OnInspectorGUI()
        {
            KimodoConstraintMarkerEditorUtility.HandleDeleteCommand(target as KimodoConstraintMarkerBase);
            serializedObject.Update();

            EditorGUILayout.HelpBox(TipText, MessageType.Info);
            EditorGUILayout.Space(4f);

            DrawCommonHeader(TypeLabel);
            DrawMarkerTime();

            KimodoConstraintMarkerBase markerTarget = target as KimodoConstraintMarkerBase;
            SerializedProperty overrideProp = serializedObject.FindProperty("useOverride");
            bool useOverride = overrideProp != null && overrideProp.boolValue;
            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(markerTarget);

            if (!useOverride && !windowOpen)
            {
                if (!KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(markerTarget, forceRefresh: false, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
            }

            DrawFields(!useOverride);

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(target as KimodoConstraintMarkerBase);
            }

            KimodoConstraintSelectionPreviewTool.ScheduleRefresh();
        }

        private void DrawCommonHeader(string type)
        {
            EditorGUILayout.LabelField($"Kimodo Constraint Marker ({type})", EditorStyles.boldLabel);
            KimodoConstraintMarkerEditorUtility.DrawEnabledField(serializedObject);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("useOverride"));
            KimodoConstraintMarkerEditorUtility.DrawOverrideEditButton(serializedObject, target as KimodoConstraintMarkerBase);
            EditorGUILayout.Space(4f);
        }

        private void DrawMarkerTime()
        {
            KimodoConstraintMarkerEditorUtility.DrawSampleTimeField(serializedObject, target as IMarker);
        }

        protected abstract void DrawFields(bool readOnly);
    }

    [CustomEditor(typeof(KimodoConstraintMarker))]
    internal sealed class KimodoConstraintMarkerEditor : KimodoConstraintStandardMarkerEditorBase
    {
        protected override string TypeLabel => "Constraint";
        protected override string TipText =>
            "One canonical pose with independently enabled muscle, root and end-effector channels. " +
            "Export resolves Muscle → Foot IK → Hand IK → Root2D and keeps the server protocol unchanged.";

        protected override void DrawFields(bool readOnly)
        {
            if (readOnly)
            {
                EditorGUILayout.HelpBox("Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }
            EditorGUI.BeginDisabledGroup(readOnly);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.mask"), true);
            DrawCanonicalPoseEuler(serializedObject);
            EditorGUI.EndDisabledGroup();
        }

        private static void DrawCanonicalPoseEuler(SerializedObject so)
        {
            SerializedProperty pose = so.FindProperty("sampleData.characterPose");
            if (pose == null)
            {
                return;
            }

            EditorGUILayout.LabelField("CharacterPose (canonical)", EditorStyles.boldLabel);
            SerializedProperty muscles = pose.FindPropertyRelative("muscles");
            if (muscles != null)
            {
                EditorGUILayout.LabelField("Muscle Pose", "Edit dependency-bone Euler angles in Override Edit.");
            }
            DrawEulerTransform(pose.FindPropertyRelative("root"), "Root");
            DrawEulerTransform(pose.FindPropertyRelative("hands.left"), "Left Hand");
            DrawEulerTransform(pose.FindPropertyRelative("hands.right"), "Right Hand");
            DrawEulerTransform(pose.FindPropertyRelative("feet.left"), "Left Foot");
            DrawEulerTransform(pose.FindPropertyRelative("feet.right"), "Right Foot");
        }

        private static void DrawEulerTransform(SerializedProperty transform, string label)
        {
            if (transform == null) return;
            SerializedProperty position = transform.FindPropertyRelative("t");
            SerializedProperty rotation = transform.FindPropertyRelative("q");
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            if (position != null) EditorGUILayout.PropertyField(position, new GUIContent("Position"));
            if (rotation != null)
            {
                Quaternion value = rotation.quaternionValue;
                EditorGUI.BeginChangeCheck();
                Vector3 euler = EditorGUILayout.Vector3Field("Euler", value.eulerAngles);
                if (EditorGUI.EndChangeCheck()) rotation.quaternionValue = Quaternion.Euler(euler);
            }
            EditorGUILayout.EndVertical();
        }
    }

    [CustomEditor(typeof(KimodoFullBodyConstraintMarker))]
    internal sealed class KimodoFullBodyConstraintMarkerEditor : KimodoConstraintStandardMarkerEditorBase
    {
        protected override string TypeLabel => "FullBody";
        protected override string TipText =>
            "Purpose: apply a strong full-body pose constraint at a key frame (root position + local joint rotations).\n" +
            "Recommended when you need the generated motion to match a specific target pose at that frame.";

        protected override void DrawFields(bool readOnly)
        {
            if (readOnly)
            {
                EditorGUILayout.HelpBox("Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(readOnly);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.kimodoRootPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.localAxisAngles"), true);
            EditorGUI.EndDisabledGroup();
        }
    }

    [CustomEditor(typeof(KimodoRoot2DConstraintMarker))]
    internal sealed class KimodoRoot2DConstraintMarkerEditor : KimodoConstraintStandardMarkerEditorBase
    {
        protected override string TypeLabel => "Root2D";
        protected override string TipText =>
            "Purpose: constrain forward (+Z), rightward (+X), and optional absolute Rotation Y at a key frame.\n" +
            "Recommended for path following, locomotion route control, and turn direction control.";

        protected override void DrawFields(bool readOnly)
        {
            if (readOnly)
            {
                EditorGUILayout.HelpBox("Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(readOnly);
            KimodoRoot2DConstraintEditorGUI.Draw(serializedObject);
            EditorGUI.EndDisabledGroup();
        }
    }

    [CustomEditor(typeof(KimodoEndEffectorConstraintMarker), true)]
    internal sealed class KimodoEndEffectorConstraintMarkerEditor : UnityEditor.Editor
    {
        private void OnDisable()
        {
            KimodoConstraintMarkerEditorUtility.ClearMarkerPoseCachePreview(target as KimodoConstraintMarkerBase, keepIfOverrideWindowOpen: true);
        }

        public override void OnInspectorGUI()
        {
            KimodoConstraintMarkerEditorUtility.HandleDeleteCommand(target as KimodoConstraintMarkerBase);
            serializedObject.Update();

            string typeName = (target as KimodoEndEffectorConstraintMarker)?.ConstraintType ?? "end-effector";
            bool isCustomEndEffector = string.Equals(typeName, "end-effector", StringComparison.OrdinalIgnoreCase);
            EditorGUILayout.HelpBox(GetTipByType(typeName), MessageType.Info);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField($"Kimodo Constraint Marker ({typeName})", EditorStyles.boldLabel);
            KimodoConstraintMarkerEditorUtility.DrawEnabledField(serializedObject);

            SerializedProperty overrideProp = serializedObject.FindProperty("useOverride");
            if (isCustomEndEffector)
            {
                overrideProp.boolValue = false;
                EditorGUILayout.Toggle(new GUIContent("useOverride", "Disabled for custom end-effector marker; values are sampled from timeline pose."), false);
            }
            else
            {
                EditorGUILayout.PropertyField(overrideProp);
                KimodoConstraintMarkerEditorUtility.DrawOverrideEditButton(serializedObject, target as KimodoConstraintMarkerBase);
            }

            DrawMarkerTime();
            bool useOverride = !isCustomEndEffector && overrideProp != null && overrideProp.boolValue;
            KimodoConstraintMarkerBase markerTarget = target as KimodoConstraintMarkerBase;
            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(markerTarget);

            if (!useOverride && !windowOpen)
            {
                if (!KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(markerTarget, forceRefresh: false, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
            }

            if (isCustomEndEffector)
            {
                EditorGUILayout.HelpBox("end-effector has no override mode; sampling from timeline pose.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(useOverride
                    ? "Override enabled. Editing marker values."
                    : "Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }
            DrawEEFields(typeName, !useOverride);

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(target as KimodoConstraintMarkerBase);
            }

            KimodoConstraintSelectionPreviewTool.ScheduleRefresh();
        }

        private void DrawMarkerTime()
        {
            KimodoConstraintMarkerEditorUtility.DrawSampleTimeField(serializedObject, target as IMarker);
        }

        private static string GetTipByType(string typeName)
        {
            switch (typeName)
            {
                case "left-hand":
                    return "Purpose: constrain the left-hand end-effector chain position/orientation at a key frame.\nRecommended for grab, wave, and pointing control.";
                case "right-hand":
                    return "Purpose: constrain the right-hand end-effector chain position/orientation at a key frame.\nRecommended for grab, wave, and pointing control.";
                case "left-foot":
                    return "Purpose: constrain the left-foot end-effector chain position/orientation at a key frame.\nRecommended for foot placement, stepping targets, and anti-sliding control.";
                case "right-foot":
                    return "Purpose: constrain the right-foot end-effector chain position/orientation at a key frame.\nRecommended for foot placement, stepping targets, and anti-sliding control.";
                default:
                    return "Purpose: custom end-effector constraint (joint_names can include LeftHand/RightHand/LeftFoot/RightFoot/Hips).\n" +
                           "Recommended for mixed multi-target constraints (for example, hand and foot targets at the same time).";
            }
        }

        private void DrawEEFields(string typeName, bool readOnly)
        {
            EditorGUI.BeginDisabledGroup(readOnly);
            SerializedProperty jointNamesProp = serializedObject.FindProperty("sampleData.jointNames");
            if (jointNamesProp != null && typeName == "end-effector")
            {
                EditorGUILayout.PropertyField(jointNamesProp, true);
            }
            else if (typeName != "end-effector")
            {
                EditorGUILayout.HelpBox("Fixed joint group marker type; joint_names is determined by marker class.", MessageType.None);
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.kimodoRootPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.localAxisAngles"), true);
            EditorGUI.EndDisabledGroup();
        }

    }

    internal static class KimodoConstraintMarkerEditorUtility
    {
        public const double KimodoFps = 30.0;
        private static readonly Dictionary<int, AutoSampleCacheEntry> AutoSampleCache = new Dictionary<int, AutoSampleCacheEntry>();
        private static readonly Dictionary<int, PoseRenderCacheEntry> PoseRenderSignatures = new Dictionary<int, PoseRenderCacheEntry>();
        private static readonly Dictionary<int, string> CachedIntStrings = new Dictionary<int, string>();
        private static int dragMuscleSnapshotId;

        private const string DefaultBridgeModelName = "Kimodo-SOMA-RP-v1";

        private struct MarkerSamplingContext
        {
            public TrackAsset Track;
            public Animator Animator;
            public Avatar SourceAvatar;
            public string ModelName;
            public int CacheTimeFrames;
        }

        internal static bool TryGetMarkerTrack(IMarker marker, out TrackAsset track)
        {
            track = marker?.parent as TrackAsset;
            return track != null;
        }

        private sealed class AutoSampleCacheEntry
        {
            public AutoSampleSignatureSnapshot Snapshot;
            public bool Success;
            public string Error;
        }

        private sealed class PoseRenderCacheEntry
        {
            public PoseRenderSignatureSnapshot Snapshot;
            public bool Success;
            public string Error;
        }

        private struct AutoSampleSignatureSnapshot
        {
            public string ConstraintType;
            public double GlobalTime;
            public string ModelName;
            public int TrackId;
            public int AnimatorId;
            public int SourceAvatarId;
            public int SourceAvatarDirtyCount;
            public int SourceSignature;
            public int CacheTimeFrames;
            public Vector3 TrackOffsetPosition;
            public Quaternion TrackOffsetRotation;
            public bool HasRootHeading;
            public Vector3 KimodoRootPosition;
            public bool HasEndEffectorTargetPosition;
            public Vector3 EndEffectorTargetPositionRootLocal;
            public bool HasEndEffectorTargetRotation;
            public Quaternion EndEffectorTargetRotationBodyRelative;
            public Vector3 UnityRootPos;
            public Quaternion UnityRootRot;
            public string[] JointNames;
        }

        private struct PoseRenderSignatureSnapshot
        {
            public string CharacterPoseJson;
            public string MaskSignature;
            public string ConstraintType;
            public double SampleTime;
            public int ClipId;
            public int AnimatorId;
            public int SourceAvatarId;
            public string ModelName;
            public KimodoConstraintRigType RigType;
            public bool HasRootHeading;
            public Vector3 KimodoRootPosition;
            public Vector2 RootHeading;
            public bool HasEndEffectorTargetPosition;
            public Vector3 EndEffectorTargetPositionRootLocal;
            public bool HasEndEffectorTargetRotation;
            public Quaternion EndEffectorTargetRotationBodyRelative;
            public Vector3 UnityRootPos;
            public Quaternion UnityRootRot;
            public string[] JointNames;
            public Vector3[] LocalAxisAngles;
            public int[] SampledJointIndices;
        }

        internal static string GetCachedIntString(int value)
        {
            if (!CachedIntStrings.TryGetValue(value, out string cached))
            {
                cached = value.ToString(CultureInfo.InvariantCulture);
                CachedIntStrings[value] = cached;
            }

            return cached;
        }

        public static bool TryGetClipRangeForMarker(IMarker marker, out TimelineClip clipRange)
        {
            clipRange = null;
            if (!TryGetMarkerTrack(marker, out TrackAsset track))
            {
                return false;
            }

            _ = track.end; // Refresh Timeline's calculated pre/post extrapolation spans after clip edits.
            foreach (TimelineClip clip in track.GetClips())
            {
                if (!(clip?.asset is AnimationPlayableAsset) ||
                    !IsTimeInClipFrameRange(marker.time, clip) && !clip.IsExtrapolatedTime(marker.time))
                {
                    continue;
                }

                if (clipRange == null || clip.start > clipRange.start)
                {
                    clipRange = clip;
                }
            }

            return clipRange != null;
        }

        internal static bool IsTimeInClipFrameRange(double time, TimelineClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            double frameRate = clip.GetParentTrack()?.timelineAsset?.editorSettings.frameRate ??
                KimodoMotionModelProfiles.DefaultFrameRate;
            int timeFrame = KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(time, frameRate);
            int startFrame = KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(clip.start, frameRate);
            int endFrame = KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(clip.end, frameRate);
            return timeFrame >= startFrame && timeFrame < endFrame;
        }

        public static bool TryUpdateAutoSampleMarkerData(KimodoConstraintMarkerBase marker, bool forceRefresh, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!marker.constraintEnabled)
            {
                ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
                return true;
            }

            if (!TryGetMarkerTrack(marker, out TrackAsset track))
            {
                error = "parent track not found";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null || animator.transform == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            TimelineClip referenceClip = FindReferenceClip(track, marker.time, activeClip: null);
            KimodoLocalAvatarUtility.AvatarResolveResult sourceAvatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, animator);
            Avatar sourceAvatar = sourceAvatarResult.Avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = $"Resolve source avatar failed: {sourceAvatarResult.Error}";
                return false;
            }

            MarkerSamplingContext context = new MarkerSamplingContext
            {
                Track = track,
                Animator = animator,
                SourceAvatar = sourceAvatar,
                ModelName = ResolveModelName(referenceClip),
                CacheTimeFrames = KimodoPlayableClipGenerationSettings.instance.TimelineConstraintCacheTimeFrames
            };

            int id = KimodoUnityObjectIdUtility.IdHash(marker);
            if (!forceRefresh &&
                AutoSampleCache.TryGetValue(id, out AutoSampleCacheEntry cached) &&
                AutoSampleSnapshotMatches(marker, context, cached.Snapshot))
            {
                error = cached.Error ?? string.Empty;
                return cached.Success;
            }

            double sampleTime = marker.time;
            var timelineContext = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = null,
                Track = track,
                Director = director,
                Animator = animator,
                SourceAvatar = sourceAvatar,
                ModelName = context.ModelName
            };
            string samplingType = marker is KimodoConstraintMarker ? "fullbody" : marker.ConstraintType;
            if (!KimodoTimelineConstraintClipCache.TrySampleMarker(
                    timelineContext,
                    sampleTime,
                    sampleTime,
                    samplingType,
                    context.ModelName,
                    forceRefresh,
                    out KimodoMarkerSampleResult sample,
                    out error))
            {
                AutoSampleCache[id] = new AutoSampleCacheEntry
                {
                    Snapshot = BuildAutoSampleSnapshot(marker, context, marker.SampleData),
                    Success = false,
                    Error = error ?? string.Empty
                };
                return false;
            }

            float timelineFrameRate = KimodoTimelineConstraintClipCache.ResolveTimelineFrameRate(timelineContext);
            int timelineFrame = KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(
                sampleTime,
                timelineFrameRate);
            double timelineSampleTime = KimodoTimelineConstraintClipCache.ResolveTimelineSampleTime(
                sampleTime,
                timelineFrameRate);
            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][ConstraintSampleFrame] marker='{marker.ConstraintType}' " +
                $"markerTime={sampleTime:R}s timelineFps={timelineFrameRate:R} " +
                $"exactFrame={(sampleTime * timelineFrameRate):R} " +
                $"zeroBasedFrame={timelineFrame} oneBasedFrame={timelineFrame + 1} " +
                $"quantizedSampleTime={timelineSampleTime:R}s");

            sample.sampleTime = sampleTime;
            if (marker is KimodoConstraintMarker)
            {
                sample.mask = KimodoConstraintMask.Resolve(marker.SampleData.mask, "fullbody").Clone();
            }
            KimodoMarkerSampleResult preview = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, sample);
            if (preview == null)
            {
                error = "failed to build marker sample";
                AutoSampleCache[id] = new AutoSampleCacheEntry
                {
                    Snapshot = BuildAutoSampleSnapshot(marker, context, marker.SampleData),
                    Success = false,
                    Error = error
                };
                return false;
            }

            if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(marker, preview, keepOverrideEnabled: false, out error))
            {
                AutoSampleCache[id] = new AutoSampleCacheEntry
                {
                    Snapshot = BuildAutoSampleSnapshot(marker, context, marker.SampleData),
                    Success = false,
                    Error = error ?? string.Empty
                };
                return false;
            }

            AutoSampleCache[id] = new AutoSampleCacheEntry
            {
                Snapshot = BuildAutoSampleSnapshot(marker, context, preview),
                Success = true,
                Error = string.Empty
            };
            PoseRenderSignatures.Remove(id);
            return true;
        }

        public static bool TryRefreshMarkerCache(KimodoConstraintMarkerBase marker, out string error)
        {
            error = string.Empty;
            if (!TryUpdateAutoSampleMarkerData(marker, forceRefresh: true, out error))
            {
                return false;
            }

            KimodoConstraintSelectionPreviewTool.ScheduleRefresh();
            SceneView.RepaintAll();
            return true;
        }

        internal static void ClearSamplingCaches()
        {
            AutoSampleCache.Clear();
            PoseRenderSignatures.Clear();
        }

        internal static void DrawEnabledField(SerializedObject so)
        {
            SerializedProperty enabled = so?.FindProperty("constraintEnabled");
            if (enabled == null)
            {
                return;
            }

            bool wasEnabled = enabled.boolValue;
            EditorGUILayout.PropertyField(enabled, new GUIContent("Enabled"));
            if (!wasEnabled && enabled.boolValue)
            {
                KimodoConstraintSelectionPreviewTool.ForceRefresh();
            }
        }

        private static AutoSampleSignatureSnapshot BuildAutoSampleSnapshot(
            KimodoConstraintMarkerBase marker,
            MarkerSamplingContext context,
            KimodoMarkerSampleResult sample = null)
        {
            KimodoMarkerSampleResult source = sample ?? marker?.SampleData;
            double globalTime = marker != null ? Math.Max(0.0, marker.time) : 0.0;
            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation);
            return new AutoSampleSignatureSnapshot
            {
                ConstraintType = marker != null ? marker.ConstraintType ?? string.Empty : string.Empty,
                GlobalTime = globalTime,
                ModelName = context.ModelName ?? string.Empty,
                TrackId = KimodoUnityObjectIdUtility.IdHash(context.Track),
                AnimatorId = KimodoUnityObjectIdUtility.IdHash(context.Animator),
                SourceAvatarId = KimodoUnityObjectIdUtility.IdHash(context.SourceAvatar),
                SourceAvatarDirtyCount = context.SourceAvatar != null ? EditorUtility.GetDirtyCount(context.SourceAvatar) : 0,
                SourceSignature = KimodoTimelineConstraintClipCache.ComputeSamplingSourceSignature(context.Track),
                CacheTimeFrames = context.CacheTimeFrames,
                TrackOffsetPosition = trackOffsetPosition,
                TrackOffsetRotation = trackOffsetRotation,
                HasRootHeading = source != null && source.hasRootHeading,
                KimodoRootPosition = source != null ? source.kimodoRootPosition : default,
                HasEndEffectorTargetPosition = source != null && source.hasEndEffectorTargetPosition,
                EndEffectorTargetPositionRootLocal = source != null ? source.endEffectorTargetPositionRootLocal : default,
                HasEndEffectorTargetRotation = source != null && source.hasEndEffectorTargetRotation,
                EndEffectorTargetRotationBodyRelative = source != null ? source.endEffectorTargetRotationBodyRelative : Quaternion.identity,
                UnityRootPos = source != null ? source.unityRootPos : default,
                UnityRootRot = source != null ? source.unityRootRot : default,
                JointNames = CopyStringArray(source != null ? source.jointNames : null)
            };
        }

        private static bool AutoSampleSnapshotMatches(
            KimodoConstraintMarkerBase marker,
            MarkerSamplingContext context,
            AutoSampleSignatureSnapshot snapshot)
        {
            KimodoMarkerSampleResult sample = marker != null ? marker.SampleData : null;
            double globalTime = marker != null ? Math.Max(0.0, marker.time) : 0.0;
            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation);
            return string.Equals(snapshot.ConstraintType ?? string.Empty, marker != null ? marker.ConstraintType ?? string.Empty : string.Empty, StringComparison.Ordinal) &&
                Math.Abs(snapshot.GlobalTime - globalTime) <= 1e-9 &&
                string.Equals(snapshot.ModelName ?? string.Empty, context.ModelName ?? string.Empty, StringComparison.Ordinal) &&
                snapshot.TrackId == KimodoUnityObjectIdUtility.IdHash(context.Track) &&
                snapshot.AnimatorId == KimodoUnityObjectIdUtility.IdHash(context.Animator) &&
                snapshot.SourceAvatarId == KimodoUnityObjectIdUtility.IdHash(context.SourceAvatar) &&
                snapshot.SourceAvatarDirtyCount == (context.SourceAvatar != null ? EditorUtility.GetDirtyCount(context.SourceAvatar) : 0) &&
                snapshot.SourceSignature == KimodoTimelineConstraintClipCache.ComputeSamplingSourceSignature(context.Track) &&
                snapshot.CacheTimeFrames == context.CacheTimeFrames &&
                Vector3Approximately(snapshot.TrackOffsetPosition, trackOffsetPosition) &&
                QuaternionApproximately(snapshot.TrackOffsetRotation, trackOffsetRotation) &&
                snapshot.HasRootHeading == (sample != null && sample.hasRootHeading) &&
                Vector3Approximately(snapshot.KimodoRootPosition, sample != null ? sample.kimodoRootPosition : default) &&
                snapshot.HasEndEffectorTargetPosition == (sample != null && sample.hasEndEffectorTargetPosition) &&
                Vector3Approximately(snapshot.EndEffectorTargetPositionRootLocal, sample != null ? sample.endEffectorTargetPositionRootLocal : default) &&
                snapshot.HasEndEffectorTargetRotation == (sample != null && sample.hasEndEffectorTargetRotation) &&
                QuaternionApproximately(snapshot.EndEffectorTargetRotationBodyRelative, sample != null ? sample.endEffectorTargetRotationBodyRelative : Quaternion.identity) &&
                Vector3Approximately(snapshot.UnityRootPos, sample != null ? sample.unityRootPos : default) &&
                QuaternionApproximately(snapshot.UnityRootRot, sample != null ? sample.unityRootRot : default) &&
                StringArrayEquals(snapshot.JointNames, sample != null ? sample.jointNames : null);
        }

        private static string ResolveModelName(TimelineClip clipRange)
        {
            KimodoPlayableClip playableClip = clipRange != null ? clipRange.asset as KimodoPlayableClip : null;
            return playableClip != null && !string.IsNullOrWhiteSpace(playableClip.bridgeModelName)
                ? playableClip.bridgeModelName.Trim()
                : DefaultBridgeModelName;
        }

        private static string ResolveModelName(TrackAsset track, double timelineTime, TimelineClip activeClip)
        {
            return ResolveModelName(FindReferenceClip(track, timelineTime, activeClip));
        }

        private static TimelineClip FindReferenceClip(TrackAsset track, double timelineTime, TimelineClip activeClip)
        {
            if (activeClip?.asset is KimodoPlayableClip)
            {
                return activeClip;
            }

            TimelineClip owningClip = KimodoTimelineConstraintMarkerSampler.FindOwningClip(track, timelineTime);
            if (owningClip != null)
            {
                return owningClip;
            }

            TimelineClip nearestKimodo = null;
            double nearestDistance = double.PositiveInfinity;
            if (track != null)
            {
                foreach (TimelineClip clip in track.GetClips())
                {
                    if (!(clip?.asset is KimodoPlayableClip))
                    {
                        continue;
                    }

                    double distance = timelineTime < clip.start
                        ? clip.start - timelineTime
                        : timelineTime - clip.end;
                    if (distance < nearestDistance ||
                        (Math.Abs(distance - nearestDistance) <= 1e-9 &&
                         (nearestKimodo == null || clip.start > nearestKimodo.start)))
                    {
                        nearestKimodo = clip;
                        nearestDistance = distance;
                    }
                }
            }

            return nearestKimodo ?? activeClip;
        }

        public static void MoveMarkerToTime(IMarker marker, double globalTime)
        {
            if (marker == null)
            {
                return;
            }

            if (marker is KimodoConstraintMarkerBase kimodoMarker)
            {
                ClearMarkerEditorCaches(kimodoMarker);
                KimodoConstraintPoseCache.DestroyEntriesForItemId(GetMarkerEntryId(kimodoMarker));
                kimodoMarker.time = globalTime;
                kimodoMarker.SampleData.sampleTime = Math.Max(0.0, globalTime);
            }

            UnityEngine.Object markerObject = marker as UnityEngine.Object;
            UnityEngine.Object parentTrackObject = marker.parent as UnityEngine.Object;

            if (markerObject != null)
            {
                Undo.RecordObject(markerObject, "Move Kimodo Constraint Marker");
            }
            if (parentTrackObject != null)
            {
                Undo.RecordObject(parentTrackObject, "Move Kimodo Constraint Marker");
            }


            if (markerObject != null)
            {
                EditorUtility.SetDirty(markerObject);
            }
            if (parentTrackObject != null)
            {
                EditorUtility.SetDirty(parentTrackObject);
            }

            if (TimelineEditor.inspectedAsset != null)
            {
                EditorUtility.SetDirty(TimelineEditor.inspectedAsset);
            }

            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsModified);
        }

        public static void DrawSampleTimeField(SerializedObject so, IMarker marker)
        {
            if (so == null || marker == null)
            {
                return;
            }

            SerializedProperty timeProp = so.FindProperty("sampleData.sampleTime");
            if (timeProp == null)
            {
                return;
            }

            // Keep stored sample time aligned with marker timeline position.
            double markerTime = Math.Max(0.0, marker.time);
            if (Math.Abs(timeProp.doubleValue - markerTime) > 1e-9)
            {
                timeProp.doubleValue = markerTime;
            }

            double sourceTime = Math.Max(0.0, timeProp.doubleValue);
            if (Math.Abs(timeProp.doubleValue - sourceTime) > 1e-9)
            {
                timeProp.doubleValue = sourceTime;
            }

            double displayCurrent = Math.Round(sourceTime, 4, MidpointRounding.AwayFromZero);
            double displaySampleTime = Math.Max(0.0, marker.time);
            if (TryGetClipRangeForMarker(marker, out TimelineClip clipRange) && clipRange != null)
            {
                displaySampleTime = KimodoMarkerSamplingUtility.ClampLocalSampleTime(clipRange, marker.time);
            }
            displaySampleTime = Math.Round(displaySampleTime, 4, MidpointRounding.AwayFromZero);

            double editedTime = EditorGUILayout.DoubleField(
                new GUIContent("Marker Time (seconds)", "Absolute timeline time stored in marker data and used by preview/edit. Allowed range: [0, +inf)."),
                displayCurrent);
            double normalizedEdited = Math.Max(0.0, editedTime);
            EditorGUILayout.LabelField($"Sample Time: {displaySampleTime:F4}s", EditorStyles.miniLabel);
            if (Math.Abs(normalizedEdited - sourceTime) > 1e-9)
            {
                MoveMarkerToTime(marker, normalizedEdited);

                // Refresh SerializedObject cache after direct marker.time mutation to avoid stale writeback.
                so.UpdateIfRequiredOrScript();
                SerializedProperty refreshedTimeProp = so.FindProperty("sampleData.sampleTime");
                if (refreshedTimeProp != null)
                {
                    refreshedTimeProp.doubleValue = normalizedEdited;
                }
            }
        }

        public static void NotifyInspectorChanged(KimodoConstraintMarkerBase marker)
        {
            if (marker != null)
            {
                if (marker.constraintEnabled)
                {
                    ClearMarkerEditorCaches(marker);
                }
                else
                {
                    ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
                }
                EditorUtility.SetDirty(marker);
            }

            SceneView.RepaintAll();
        }

        public static void ClearMarkerPoseCachePreview(KimodoConstraintMarkerBase marker, bool keepIfOverrideWindowOpen)
        {
            if (marker == null)
            {
                return;
            }

            ClearMarkerEditorCaches(marker);

            if (keepIfOverrideWindowOpen && KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker))
            {
                return;
            }

            KimodoConstraintPoseCache.DestroyEntriesForItemId(GetMarkerEntryId(marker));
            SceneView.RepaintAll();
        }

        public static bool TryBuildRenderContextForMarker(KimodoConstraintMarkerBase marker, out PoseCacheRenderContext context, out string error)
        {
            context = default;
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!TryGetMarkerTrack(marker, out TrackAsset track))
            {
                error = "parent track not found";
                return false;
            }

            TryGetClipRangeForMarker(marker, out TimelineClip clipRange);

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "animation track has no animator binding";
                return false;
            }

            TimelineClip referenceClip = FindReferenceClip(track, marker.time, clipRange);
            KimodoPlayableClip playableClip = referenceClip?.asset as KimodoPlayableClip;
            string modelName = ResolveModelName(referenceClip);
            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, animator);
            if (!avatarResult.IsHumanoid || avatarResult.Avatar == null)
            {
                error = $"Resolve source avatar failed: {avatarResult.Error}";
                return false;
            }
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            int clipContextId = playableClip != null
                ? KimodoUnityObjectIdUtility.IdHash(playableClip)
                : ((referenceClip?.asset as UnityEngine.Object) != null
                    ? KimodoUnityObjectIdUtility.IdHash(referenceClip.asset as UnityEngine.Object)
                    : KimodoUnityObjectIdUtility.IdHash(track));
            context = new PoseCacheRenderContext(
                clipContextId,
                KimodoUnityObjectIdUtility.IdHash(animator),
                KimodoUnityObjectIdUtility.IdHash(track),
                modelName,
                rigType,
                avatarResult.Avatar);
            return true;
        }

        internal static void LogDragMuscleSnapshot(
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext renderContext,
            string entryId)
        {
            try
            {
                if (marker == null)
                {
                    return;
                }

                if (!TryCaptureDragMuscleSnapshot(
                        marker,
                        renderContext,
                        entryId,
                        out MuscleSample timelinePose,
                        out float timelinePoseScale,
                        out MuscleSample virtualSkeleton,
                        out float virtualSkeletonScale,
                        out MuscleSample targetCharacter,
                        out float targetCharacterScale,
                        out double timelineSampleTime,
                        out string details,
                        out string error))
                {
                    Debug.LogWarning($"[Kimodo][ConstraintDragMuscles] capture failed: {error}");
                    return;
                }

                dragMuscleSnapshotId++;
                KimodoConstraintPoseDiagnostics.LogDragMuscleSnapshot(
                    dragMuscleSnapshotId,
                    marker.ConstraintType,
                    timelineSampleTime,
                    timelinePose,
                    timelinePoseScale,
                    virtualSkeleton,
                    virtualSkeletonScale,
                    targetCharacter,
                    targetCharacterScale,
                    details);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][ConstraintDragMuscles] capture failed: {ex.Message}");
            }
        }

        private static bool TryCaptureDragMuscleSnapshot(
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext renderContext,
            string entryId,
            out MuscleSample timelinePose,
            out float timelinePoseScale,
            out MuscleSample virtualSkeleton,
            out float virtualSkeletonScale,
            out MuscleSample targetCharacter,
            out float targetCharacterScale,
            out double timelineSampleTime,
            out string details,
            out string error)
        {
            timelinePose = null;
            timelinePoseScale = 0f;
            virtualSkeleton = null;
            virtualSkeletonScale = 0f;
            targetCharacter = null;
            targetCharacterScale = 0f;
            timelineSampleTime = marker != null ? marker.time : 0d;
            details = string.Empty;
            error = string.Empty;

            if (!TryGetMarkerTrack(marker, out TrackAsset track))
            {
                error = "marker track is unavailable.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            Animator animator = director != null ? director.GetGenericBinding(track) as Animator : null;
            if (director == null || animator == null || !KimodoRetargetCoreUtility.IsValidHumanoid(renderContext.SourceAvatar))
            {
                error = "Timeline director, binding Animator, or source Avatar is unavailable.";
                return false;
            }

            var timelineContext = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = null,
                Track = track,
                Director = director,
                Animator = animator,
                SourceAvatar = renderContext.SourceAvatar,
                ModelName = renderContext.ModelName
            };
            float timelineFrameRate = KimodoTimelineConstraintClipCache.ResolveTimelineFrameRate(timelineContext);
            timelineSampleTime = KimodoTimelineConstraintClipCache.ResolveTimelineSampleTime(marker.time, timelineFrameRate);

            if (!KimodoTimelineSamplingSession.TryCreate(
                    timelineContext,
                    renderContext.ModelName,
                    out KimodoTimelineSamplingSession sampler,
                    out error))
            {
                return false;
            }
            try
            {
                if (!sampler.TryCaptureMuscleSample(
                        timelineSampleTime,
                        normalizeRootToAnchor: false,
                        Vector3.zero,
                        Quaternion.identity,
                        out timelinePose,
                        out error))
                {
                    return false;
                }
                timelinePoseScale = sampler.SourceHumanScale;
            }
            finally
            {
                sampler.Dispose();
            }

            if (!KimodoConstraintPoseCache.TryCaptureDragMuscleSamples(
                    renderContext,
                    entryId,
                    out virtualSkeleton,
                    out virtualSkeletonScale,
                    out targetCharacter,
                    out targetCharacterScale,
                    out error))
            {
                return false;
            }

            details = $"timelinePoseClip=true animator='{animator.name}' entry='{entryId ?? string.Empty}'";
            return true;
        }

        public static bool TryBuildRenderContextForPlayableClip(
            KimodoPlayableClip playableClip,
            out PoseCacheRenderContext context,
            out TimelineClip timelineClip,
            out string error,
            TimelineClip timelineClipOverride = null)
        {
            context = default;
            timelineClip = null;
            error = string.Empty;
            if (playableClip == null)
            {
                error = "playable clip is null";
                return false;
            }

            timelineClip = timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(playableClip);
            if (timelineClip == null)
            {
                error = "timeline clip not found for playable clip";
                return false;
            }

            TrackAsset track = timelineClip.GetParentTrack();
            if (track == null)
            {
                error = "parent track not found";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "animation track has no animator binding";
                return false;
            }

            string modelName = string.IsNullOrWhiteSpace(playableClip.bridgeModelName)
                ? "Kimodo-SOMA-RP-v1"
                : playableClip.bridgeModelName.Trim();
            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, animator);
            if (!avatarResult.IsHumanoid || avatarResult.Avatar == null)
            {
                error = $"Resolve source avatar failed: {avatarResult.Error}";
                return false;
            }
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            context = new PoseCacheRenderContext(
                KimodoUnityObjectIdUtility.IdHash(playableClip),
                KimodoUnityObjectIdUtility.IdHash(animator),
                KimodoUnityObjectIdUtility.IdHash(track),
                modelName,
                rigType,
                avatarResult.Avatar);
            return true;
        }

        public static bool TryRenderMarkerToPoseCache(KimodoConstraintMarkerBase marker, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!marker.constraintEnabled)
            {
                ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
                return true;
            }

            if (!TryBuildRenderContextForMarker(marker, out PoseCacheRenderContext context, out error))
            {
                return false;
            }

            return TryRenderMarkerToPoseCache(marker, context, out _, out error);
        }

        internal static bool TryRenderMarkerToPoseCache(
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext context,
            out string error)
        {
            return TryRenderMarkerToPoseCache(marker, context, out _, out error);
        }

        private static bool TryRenderMarkerToPoseCache(
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext context,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            string entryId = GetMarkerEntryId(marker);
            KimodoConstraintPoseCache.DestroyEntriesForItemId(entryId, context);

            if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(marker, marker.SampleData, out KimodoMarkerSampleResult normalizedSample, out error))
            {
                return false;
            }

            sample = normalizedSample;

            var item = new PoseCacheRenderItem
            {
                EntryId = entryId,
                SampleData = normalizedSample,
                ConstraintType = marker.ConstraintType,
                HighlightJoints = KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                Visible = true
            };
            var batch = new List<PoseCacheRenderItem>(1) { item };
            if (!KimodoConstraintPoseCache.RenderBatch(context, batch, out error))
            {
                return false;
            }

            PoseRenderSignatures[KimodoUnityObjectIdUtility.IdHash(marker)] = new PoseRenderCacheEntry
            {
                Snapshot = BuildRenderSnapshot(marker, context, normalizedSample),
                Success = true,
                Error = string.Empty
            };
            return true;
        }

        public static bool TryRenderMarkersBatchToPoseCache(
            PoseCacheRenderContext context,
            IReadOnlyList<KimodoConstraintMarkerBase> markers,
            out string error)
        {
            error = string.Empty;
            if (markers == null || markers.Count == 0)
            {
                KimodoConstraintPoseCache.SetGroupState(context, visible: false, selectable: false);
                return true;
            }

            var items = new List<PoseCacheRenderItem>(markers.Count);
            for (int i = 0; i < markers.Count; i++)
            {
                KimodoConstraintMarkerBase marker = markers[i];
                if (marker == null || !marker.constraintEnabled)
                {
                    continue;
                }

                string entryId = GetMarkerEntryId(marker);
                KimodoConstraintPoseCache.DestroyEntriesForItemId(entryId, context);

                if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(marker, marker.SampleData, out KimodoMarkerSampleResult sample, out string normalizeError))
                {
                    error = normalizeError;
                    return false;
                }

                items.Add(new PoseCacheRenderItem
                {
                    EntryId = entryId,
                    SampleData = sample,
                    ConstraintType = marker.ConstraintType,
                    HighlightJoints = KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                    Visible = true
                });
            }

            return KimodoConstraintPoseCache.RenderBatch(context, items, out error);
        }

        public static void DrawOverrideEditButton(SerializedObject so, KimodoConstraintMarkerBase marker)
        {
            if (so == null || marker == null)
            {
                return;
            }

            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker);
            using (new EditorGUI.DisabledScope(!marker.constraintEnabled))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Refresh Cache", "Force re-sample the marker pose and rebuild the preview cache."), GUILayout.Height(22f)))
                {
                    if (!TryRefreshMarkerCache(marker, out string refreshError))
                    {
                        Debug.LogWarning($"[Kimodo][ConstraintMarker] Refresh cache failed: {refreshError}");
                    }
                }

                string label = windowOpen ? "Reopen Edit" : "Edit";
                if (GUILayout.Button(new GUIContent(label, "Open pose edit window. Override is enabled only after the preview pose is changed."), GUILayout.Height(22f)))
                {
                    KimodoConstraintMarkerBase markerToOpen = marker;
                    EditorApplication.delayCall += () =>
                    {
                        if (markerToOpen != null && markerToOpen.constraintEnabled)
                        {
                            KimodoConstraintOverrideEditWindow.ShowWindow(markerToOpen);
                        }
                    };
                }
            }
        }

        private static void ClearMarkerEditorCaches(KimodoConstraintMarkerBase marker)
        {
            if (marker == null)
            {
                return;
            }

            int id = KimodoUnityObjectIdUtility.IdHash(marker);
            AutoSampleCache.Remove(id);
            PoseRenderSignatures.Remove(id);
        }

        public static void HandleDeleteCommand(KimodoConstraintMarkerBase marker)
        {
            if (marker == null)
            {
                return;
            }

            Event currentEvent = Event.current;
            if (currentEvent == null)
            {
                return;
            }

            bool isDeleteCommand =
                string.Equals(currentEvent.commandName, "Delete", StringComparison.Ordinal) ||
                string.Equals(currentEvent.commandName, "SoftDelete", StringComparison.Ordinal);
            if (!isDeleteCommand)
            {
                return;
            }

            if (currentEvent.type == EventType.ValidateCommand)
            {
                currentEvent.Use();
                return;
            }

            if (currentEvent.type != EventType.ExecuteCommand)
            {
                return;
            }

            if (TryDeleteMarkerWithUndo(marker, out string error))
            {
                currentEvent.Use();
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.LogWarning($"[Kimodo][ConstraintMarker] Delete failed: {error}");
            }
        }

        public static bool TryDeleteMarkerWithUndo(KimodoConstraintMarkerBase marker, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!(marker.parent is TrackAsset track))
            {
                error = "marker parent track not found";
                return false;
            }

            UnityEngine.Object markerObject = marker;
            UnityEngine.Object inspectedAsset = TimelineEditor.inspectedAsset;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete Kimodo Constraint Marker");

            if (inspectedAsset != null)
            {
                Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { track, inspectedAsset }, "Delete Kimodo Constraint Marker");
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(track, "Delete Kimodo Constraint Marker");
            }

            ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
            track.DeleteMarker(marker);

            if (markerObject != null)
            {
                EditorUtility.SetDirty(markerObject);
            }

            EditorUtility.SetDirty(track);
            if (inspectedAsset != null)
            {
                EditorUtility.SetDirty(inspectedAsset);
            }

            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsAddedOrRemoved);
            Undo.CollapseUndoOperations(undoGroup);
            return true;
        }

        internal static string GetMarkerEntryId(KimodoConstraintMarkerBase marker)
        {
            return marker == null ? string.Empty : GetCachedIntString(KimodoUnityObjectIdUtility.IdHash(marker));
        }

        private static PoseRenderSignatureSnapshot BuildRenderSnapshot(
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext context,
            KimodoMarkerSampleResult sample)
        {
            KimodoMarkerSampleResult source = sample ?? marker?.SampleData;
            return new PoseRenderSignatureSnapshot
            {
                CharacterPoseJson = source?.characterPose != null ? JsonUtility.ToJson(source.characterPose) : string.Empty,
                MaskSignature = BuildMaskSignature(source?.mask),
                ConstraintType = marker != null ? marker.ConstraintType ?? string.Empty : string.Empty,
                SampleTime = source != null ? source.sampleTime : 0.0,
                ClipId = context.ClipId,
                AnimatorId = context.AnimatorId,
                SourceAvatarId = KimodoUnityObjectIdUtility.IdHash(context.SourceAvatar),
                ModelName = context.ModelName ?? string.Empty,
                RigType = context.RigType,
                HasRootHeading = source != null && source.hasRootHeading,
                KimodoRootPosition = source != null ? source.kimodoRootPosition : default,
                RootHeading = source != null ? source.rootHeading : default,
                HasEndEffectorTargetPosition = source != null && source.hasEndEffectorTargetPosition,
                EndEffectorTargetPositionRootLocal = source != null ? source.endEffectorTargetPositionRootLocal : default,
                HasEndEffectorTargetRotation = source != null && source.hasEndEffectorTargetRotation,
                EndEffectorTargetRotationBodyRelative = source != null ? source.endEffectorTargetRotationBodyRelative : Quaternion.identity,
                UnityRootPos = source != null ? source.unityRootPos : default,
                UnityRootRot = source != null ? source.unityRootRot : default,
                JointNames = CopyStringArray(source != null ? source.jointNames : null),
                LocalAxisAngles = CopyVector3Array(source != null ? source.localAxisAngles : null),
                SampledJointIndices = CopyIntArray(source != null ? source.sampledJointIndices : null)
            };
        }

        private static bool RenderSnapshotMatches(
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext context,
            PoseRenderSignatureSnapshot snapshot)
        {
            KimodoMarkerSampleResult sample = marker != null ? marker.SampleData : null;
            return string.Equals(snapshot.CharacterPoseJson ?? string.Empty, sample?.characterPose != null ? JsonUtility.ToJson(sample.characterPose) : string.Empty, StringComparison.Ordinal) &&
                string.Equals(snapshot.MaskSignature ?? string.Empty, BuildMaskSignature(sample?.mask), StringComparison.Ordinal) &&
                string.Equals(snapshot.ConstraintType ?? string.Empty, marker != null ? marker.ConstraintType ?? string.Empty : string.Empty, StringComparison.Ordinal) &&
                Math.Abs(snapshot.SampleTime - (sample != null ? sample.sampleTime : 0.0)) <= 1e-9 &&
                snapshot.ClipId == context.ClipId &&
                snapshot.AnimatorId == context.AnimatorId &&
                snapshot.SourceAvatarId == KimodoUnityObjectIdUtility.IdHash(context.SourceAvatar) &&
                string.Equals(snapshot.ModelName ?? string.Empty, context.ModelName ?? string.Empty, StringComparison.Ordinal) &&
                snapshot.RigType == context.RigType &&
                snapshot.HasRootHeading == (sample != null && sample.hasRootHeading) &&
                Vector3Approximately(snapshot.KimodoRootPosition, sample != null ? sample.kimodoRootPosition : default) &&
                Vector2Approximately(snapshot.RootHeading, sample != null ? sample.rootHeading : default) &&
                snapshot.HasEndEffectorTargetPosition == (sample != null && sample.hasEndEffectorTargetPosition) &&
                Vector3Approximately(snapshot.EndEffectorTargetPositionRootLocal, sample != null ? sample.endEffectorTargetPositionRootLocal : default) &&
                snapshot.HasEndEffectorTargetRotation == (sample != null && sample.hasEndEffectorTargetRotation) &&
                QuaternionApproximately(snapshot.EndEffectorTargetRotationBodyRelative, sample != null ? sample.endEffectorTargetRotationBodyRelative : Quaternion.identity) &&
                Vector3Approximately(snapshot.UnityRootPos, sample != null ? sample.unityRootPos : default) &&
                QuaternionApproximately(snapshot.UnityRootRot, sample != null ? sample.unityRootRot : default) &&
                StringArrayEquals(snapshot.JointNames, sample != null ? sample.jointNames : null) &&
                Vector3ArrayEquals(snapshot.LocalAxisAngles, sample != null ? sample.localAxisAngles : null) &&
                IntArrayEquals(snapshot.SampledJointIndices, sample != null ? sample.sampledJointIndices : null);
        }

        private static string BuildSampleSignature(KimodoMarkerSampleResult sample)
        {
            if (sample == null)
            {
                return string.Empty;
            }

            return string.Join("|",
                sample.characterPose != null ? JsonUtility.ToJson(sample.characterPose) : string.Empty,
                sample.constraintType ?? string.Empty,
                FormatDouble(sample.sampleTime),
                FormatDouble(sample.humanScale),
                BuildMaskSignature(sample.mask),
                sample.rigType.ToString(),
                sample.hasRootHeading ? "1" : "0",
                FormatVector3(sample.kimodoRootPosition),
                FormatVector2(sample.rootHeading),
                sample.hasEndEffectorTargetPosition ? "1" : "0",
                FormatVector3(sample.endEffectorTargetPositionRootLocal),
                sample.hasEndEffectorTargetRotation ? "1" : "0",
                FormatQuaternion(sample.endEffectorTargetRotationBodyRelative),
                BuildStringListSignature(sample.jointNames),
                BuildVector3ListSignature(sample.localAxisAngles),
                BuildIntListSignature(sample.sampledJointIndices));
        }

        private static string BuildMaskSignature(KimodoConstraintMask mask) => mask == null
            ? string.Empty
            : $"{mask.muscle}:{mask.rootPosition}:{mask.rootHeading}:{mask.leftFoot}:{mask.rightFoot}:{mask.leftHand}:{mask.rightHand}";

        private static string BuildStringListSignature(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(",", values);
        }

        private static string BuildVector3ListSignature(IReadOnlyList<Vector3> values)
        {
            if (values == null || values.Count == 0)
            {
                return string.Empty;
            }

            var parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                parts[i] = FormatVector3(values[i]);
            }

            return string.Join(",", parts);
        }

        private static string BuildIntListSignature(IReadOnlyList<int> values)
        {
            if (values == null || values.Count == 0)
            {
                return string.Empty;
            }

            var parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }

        private static string FormatVector2(Vector2 value)
        {
            return $"{FormatFloat(value.x)},{FormatFloat(value.y)}";
        }

        private static string FormatVector3(Vector3 value)
        {
            return $"{FormatFloat(value.x)},{FormatFloat(value.y)},{FormatFloat(value.z)}";
        }

        private static string FormatQuaternion(Quaternion value)
        {
            return $"{FormatFloat(value.x)},{FormatFloat(value.y)},{FormatFloat(value.z)},{FormatFloat(value.w)}";
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string[] CopyStringArray(IReadOnlyList<string> values)
        {
            int count = values != null ? values.Count : 0;
            if (count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = values[i] ?? string.Empty;
            }

            return result;
        }

        private static Vector3[] CopyVector3Array(IReadOnlyList<Vector3> values)
        {
            int count = values != null ? values.Count : 0;
            if (count == 0)
            {
                return Array.Empty<Vector3>();
            }

            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = values[i];
            }

            return result;
        }

        private static int[] CopyIntArray(IReadOnlyList<int> values)
        {
            int count = values != null ? values.Count : 0;
            if (count == 0)
            {
                return Array.Empty<int>();
            }

            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = values[i];
            }

            return result;
        }

        private static bool StringArrayEquals(string[] left, IReadOnlyList<string> right)
        {
            int leftCount = left != null ? left.Length : 0;
            int rightCount = right != null ? right.Count : 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (int i = 0; i < leftCount; i++)
            {
                if (!string.Equals(left[i] ?? string.Empty, right[i] ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Vector3ArrayEquals(Vector3[] left, IReadOnlyList<Vector3> right)
        {
            int leftCount = left != null ? left.Length : 0;
            int rightCount = right != null ? right.Count : 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (int i = 0; i < leftCount; i++)
            {
                if (!Vector3Approximately(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IntArrayEquals(int[] left, IReadOnlyList<int> right)
        {
            int leftCount = left != null ? left.Length : 0;
            int rightCount = right != null ? right.Count : 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (int i = 0; i < leftCount; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Vector2Approximately(Vector2 left, Vector2 right)
        {
            return (left - right).sqrMagnitude <= 1e-10f;
        }

        private static bool QuaternionApproximately(Quaternion left, Quaternion right)
        {
            return Mathf.Abs(Quaternion.Dot(left, right)) >= 1f - 1e-10f;
        }

        private static bool Vector3Approximately(Vector3 left, Vector3 right)
        {
            return (left - right).sqrMagnitude <= 1e-10f;
        }
    }
}

