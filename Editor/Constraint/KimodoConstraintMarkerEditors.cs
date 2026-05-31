using KimodoUnityMotionTools.ProjectEditor.GenerationPipeline;
using KimodoUnityMotionTools.Generation.Pipeline;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal abstract class KimodoConstraintStandardMarkerEditorBase : UnityEditor.Editor
    {
        protected abstract string TypeLabel { get; }
        protected abstract string TipText { get; }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(TipText, MessageType.Info);
            EditorGUILayout.Space(4f);

            DrawCommonHeader(TypeLabel);
            DrawMarkerTime();

            KimodoConstraintMarkerBase markerTarget = target as KimodoConstraintMarkerBase;
            SerializedProperty overrideProp = serializedObject.FindProperty("useOverride");
            bool useOverride = overrideProp != null && overrideProp.boolValue;
            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(markerTarget);
            if (!useOverride && windowOpen)
            {
                KimodoConstraintOverrideEditWindow openWindow = KimodoConstraintOverrideEditWindow.GetOpenWindow();
                if (openWindow != null && openWindow.TargetMarker == markerTarget)
                {
                    openWindow.Close();
                }
                windowOpen = false;
            }

            if (!useOverride && !windowOpen)
            {
                if (!KimodoConstraintMarkerEditorUtility.TrySampleMarkerDataFromMarker(markerTarget, out KimodoMarkerSampleResult preview, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
                else
                {
                    KimodoConstraintMarkerPoseMapper.TryWriteSample(markerTarget, preview, keepOverrideEnabled: false, out _);
                }
            }

            DrawFields(!useOverride);

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(target as KimodoConstraintMarkerBase);
            }

            if (!windowOpen && markerTarget != null && !KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(markerTarget, out string poseError) && !string.IsNullOrWhiteSpace(poseError))
            {
                EditorGUILayout.HelpBox($"Pose cache update failed: {poseError}", MessageType.Warning);
            }
        }

        private void DrawCommonHeader(string type)
        {
            EditorGUILayout.LabelField($"Kimodo Constraint Marker ({type})", EditorStyles.boldLabel);
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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.rootPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.localAxisAngles"), true);
            EditorGUI.EndDisabledGroup();
        }
    }

    [CustomEditor(typeof(KimodoRoot2DConstraintMarker))]
    internal sealed class KimodoRoot2DConstraintMarkerEditor : KimodoConstraintStandardMarkerEditorBase
    {
        protected override string TypeLabel => "Root2D";
        protected override string TipText =>
            "Purpose: constrain the character root trajectory on the ground plane (X/Z) at a key frame. Optional heading constraint is supported.\n" +
            "Recommended for path following, locomotion route control, and turn direction control.";

        protected override void DrawFields(bool readOnly)
        {
            if (readOnly)
            {
                EditorGUILayout.HelpBox("Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(readOnly);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.rootPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.hasRootHeading"));
            SerializedProperty includeGlobalHeadingProp = serializedObject.FindProperty("sampleData.hasRootHeading");
            if (includeGlobalHeadingProp != null && includeGlobalHeadingProp.boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.rootHeading"));
            }
            EditorGUI.EndDisabledGroup();
        }
    }

    [CustomEditor(typeof(KimodoEndEffectorConstraintMarker), true)]
    internal sealed class KimodoEndEffectorConstraintMarkerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            string typeName = (target as KimodoEndEffectorConstraintMarker)?.ConstraintType ?? "end-effector";
            bool isCustomEndEffector = string.Equals(typeName, "end-effector", StringComparison.OrdinalIgnoreCase);
            EditorGUILayout.HelpBox(GetTipByType(typeName), MessageType.Info);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField($"Kimodo Constraint Marker ({typeName})", EditorStyles.boldLabel);

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
            if (!useOverride && windowOpen)
            {
                KimodoConstraintOverrideEditWindow openWindow = KimodoConstraintOverrideEditWindow.GetOpenWindow();
                if (openWindow != null && openWindow.TargetMarker == markerTarget)
                {
                    openWindow.Close();
                }
                windowOpen = false;
            }

            if (!useOverride && !windowOpen)
            {
                if (!KimodoConstraintMarkerEditorUtility.TrySampleMarkerDataFromMarker(markerTarget, out KimodoMarkerSampleResult preview, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
                else
                {
                    KimodoConstraintMarkerPoseMapper.TryWriteSample(markerTarget, preview, keepOverrideEnabled: false, out _);
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

            if (!windowOpen && markerTarget != null && !KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(markerTarget, out string poseError) && !string.IsNullOrWhiteSpace(poseError))
            {
                EditorGUILayout.HelpBox($"Pose cache update failed: {poseError}", MessageType.Warning);
            }
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

            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.rootPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.localAxisAngles"), true);
            EditorGUI.EndDisabledGroup();
        }

    }

    internal static class KimodoConstraintMarkerEditorUtility
    {
        public const double KimodoFps = 30.0;

        public static double GetLocalSecondsInClip(TimelineClip clipRange, double globalTime)
        {
            if (clipRange == null)
            {
                return 0.0;
            }

            double local = clipRange.ToLocalTime(globalTime);
            if (local < 0.0)
            {
                return 0.0;
            }
            if (local > clipRange.duration)
            {
                return clipRange.duration;
            }
            return local;
        }

        public static bool TryGetClipRangeForMarker(IMarker marker, out TimelineClip clipRange)
        {
            clipRange = null;
            if (marker == null || marker.parent == null || TimelineEditor.inspectedAsset == null)
            {
                return false;
            }

            // Prefer sampleData.sampleTime as stable ownership hint so marker can be pulled back
            // even after user drags it across neighboring clips.
            if (marker is KimodoConstraintMarkerBase kimodoMarker)
            {
                double hintedTime = kimodoMarker.SampleData != null ? kimodoMarker.SampleData.sampleTime : marker.time;
                if (TryFindClipRangeByTime(marker, hintedTime, out clipRange))
                {
                    return true;
                }
            }

            if (TryFindClipRangeByTime(marker, marker.time, out clipRange))
            {
                return true;
            }

            return false;
        }

        private static bool TryFindClipRangeByTime(IMarker marker, double time, out TimelineClip clipRange)
        {
            clipRange = null;
            if (marker == null || marker.parent == null || TimelineEditor.inspectedAsset == null)
            {
                return false;
            }

            foreach (TrackAsset track in TimelineEditor.inspectedAsset.GetOutputTracks())
            {
                if (track != marker.parent)
                {
                    continue;
                }

                foreach (TimelineClip clip in track.GetClips())
                {
                    if (clip.asset is AnimationPlayableAsset && time >= clip.start && time <= clip.end)
                    {
                        clipRange = clip;
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool TrySampleMarkerDataFromMarker(
            KimodoConstraintMarkerBase marker,
            out KimodoMarkerSampleResult sampledData,
            out string error)
        {
            sampledData = null;
            error = string.Empty;

            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!TryGetClipRangeForMarker(marker, out TimelineClip clipRange) || clipRange == null)
            {
                error = "clip range not found";
                return false;
            }

            TrackAsset track = clipRange.GetParentTrack();
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
            if (animator == null || animator.transform == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            double sampleTime = marker.time;

            double originalTime = director.time;
            DirectorWrapMode originalWrap = director.extrapolationMode;
            KimodoMarkerSampleResult sample;
            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;
                director.time = sampleTime;
                director.Evaluate();

                string modelName = clipRange.asset is KimodoPlayableClip playableClip
                    ? playableClip.bridgeModelName
                    : "Kimodo-SOMA-RP-v1";
                if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar originAvatar, out string originError))
                {
                    error = $"Resolve origin avatar failed: {originError}";
                    return false;
                }

                KimodoLocalAvatarUtility.AvatarResolveResult targetAvatarResult = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(animator.gameObject);
                Avatar targetAvatar = targetAvatarResult.Avatar;
                if (targetAvatar == null || !targetAvatar.isValid || !targetAvatar.isHuman)
                {
                    error = $"Resolve target avatar failed: {targetAvatarResult.Error}";
                    return false;
                }

                if (!KimodoMarkerSamplingUtility.TrySampleMarker(
                        animator,
                        animator.transform,
                        clipRange,
                        modelName,
                        sampleTime,
                        marker.ConstraintType,
                        originAvatar,
                        targetAvatar,
                        out sample,
                        out error))
                {
                    return false;
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrap;
            }

            sample.sampleTime = sampleTime;
            sampledData = KimodoConstraintMarkerPoseMapper.NormalizeSample(marker, sample);
            if (sampledData == null)
            {
                error = "failed to build marker sample";
                return false;
            }

            return true;
        }

        public static void MoveMarkerToTime(IMarker marker, double globalTime)
        {
            if (marker == null)
            {
                return;
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

            marker.time = globalTime;
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

            TimelineEditor.Refresh(RefreshReason.ContentsModified);
            SceneView.RepaintAll();
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

            double sourceTime = Math.Max(0.0, timeProp.doubleValue);
            if (Math.Abs(timeProp.doubleValue - sourceTime) > 1e-9)
            {
                timeProp.doubleValue = sourceTime;
            }

            double displayCurrent = Math.Round(sourceTime, 4, MidpointRounding.AwayFromZero);

            double editedTime = EditorGUILayout.DoubleField(
                new GUIContent("Sample Time (seconds)", "Stored in marker data and used by preview/edit. Allowed range: [0, +inf)."),
                displayCurrent);
            double normalizedEdited = Math.Max(0.0, editedTime);
            EditorGUILayout.LabelField($"Marker Time: {marker.time:F4}s", EditorStyles.miniLabel);
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
                EditorUtility.SetDirty(marker);
            }

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

            if (!TryGetClipRangeForMarker(marker, out TimelineClip clipRange) || clipRange == null)
            {
                error = "clip range not found";
                return false;
            }

            TrackAsset track = clipRange.GetParentTrack();
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

            KimodoPlayableClip playableClip = clipRange.asset as KimodoPlayableClip;
            string modelName = playableClip != null && !string.IsNullOrWhiteSpace(playableClip.bridgeModelName)
                ? playableClip.bridgeModelName.Trim()
                : "Kimodo-SOMA-RP-v1";
            KimodoConstraintRigType rigType = ResolveRigTypeFromModelName(modelName);
            int clipContextId = playableClip != null
                ? playableClip.GetInstanceID()
                : ((clipRange.asset as UnityEngine.Object) != null
                    ? (clipRange.asset as UnityEngine.Object).GetInstanceID()
                    : track.GetInstanceID());
            context = new PoseCacheRenderContext(clipContextId, animator.GetInstanceID(), modelName, rigType);
            return true;
        }

        public static bool TryBuildRenderContextForPlayableClip(
            KimodoPlayableClip playableClip,
            out PoseCacheRenderContext context,
            out TimelineClip timelineClip,
            out string error)
        {
            context = default;
            timelineClip = null;
            error = string.Empty;
            if (playableClip == null)
            {
                error = "playable clip is null";
                return false;
            }

            timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(playableClip);
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
            KimodoConstraintRigType rigType = ResolveRigTypeFromModelName(modelName);
            context = new PoseCacheRenderContext(playableClip.GetInstanceID(), animator.GetInstanceID(), modelName, rigType);
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

            if (!TryBuildRenderContextForMarker(marker, out PoseCacheRenderContext context, out error))
            {
                return false;
            }

            if (!KimodoConstraintMarkerPoseMapper.TryNormalizeSample(marker, marker.SampleData, out KimodoMarkerSampleResult sample, out error))
            {
                return false;
            }

            var item = new PoseCacheRenderItem
            {
                EntryId = marker.GetInstanceID().ToString(),
                SampleData = sample,
                ConstraintType = marker.ConstraintType,
                HighlightJoints = BuildHighlightJointsForRendering(marker, context.ModelName),
                Visible = true
            };
            var batch = new List<PoseCacheRenderItem>(1) { item };
            return KimodoConstraintPoseCache.RenderBatch(context, batch, out error);
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
                if (marker == null)
                {
                    continue;
                }

                if (!KimodoConstraintMarkerPoseMapper.TryNormalizeSample(marker, marker.SampleData, out KimodoMarkerSampleResult sample, out string normalizeError))
                {
                    error = normalizeError;
                    return false;
                }

                items.Add(new PoseCacheRenderItem
                {
                    EntryId = marker.GetInstanceID().ToString(),
                    SampleData = sample,
                    ConstraintType = marker.ConstraintType,
                    HighlightJoints = BuildHighlightJointsForRendering(marker, context.ModelName),
                    Visible = true
                });
            }

            return KimodoConstraintPoseCache.RenderBatch(context, items, out error);
        }

        internal static List<string> BuildHighlightJointsForRendering(KimodoConstraintMarkerBase marker, string modelName)
        {
            var output = new List<string>();
            if (marker == null)
            {
                return output;
            }

            string root = KimodoMarkerSamplingUtility.GetRootJointNameForModel(modelName);
            if (!string.IsNullOrWhiteSpace(root))
            {
                output.Add(root);
            }

            if (marker is KimodoRoot2DConstraintMarker)
            {
                return output;
            }

            if (marker is KimodoFullBodyConstraintMarker)
            {
                string[] modelJointNames = KimodoMarkerSamplingUtility.GetJointNamesForModel(modelName);
                if (modelJointNames != null)
                {
                    for (int i = 0; i < modelJointNames.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(modelJointNames[i]))
                        {
                            output.Add(modelJointNames[i]);
                        }
                    }
                }

                return output;
            }

            List<string> names = marker.SampleData != null ? marker.SampleData.jointNames : null;
            if (names == null)
            {
                return output;
            }

            for (int i = 0; i < names.Count; i++)
            {
                string n = names[i];
                if (!string.IsNullOrWhiteSpace(n))
                {
                    output.Add(n.Trim());
                }
            }

            return output;
        }

        private static KimodoConstraintRigType ResolveRigTypeFromModelName(string modelName)
        {
            string m = (modelName ?? string.Empty).Trim().ToLowerInvariant();
            if (m.Contains("smplx"))
            {
                return KimodoConstraintRigType.Smplx;
            }

            if (m.Contains("g1"))
            {
                return KimodoConstraintRigType.G1;
            }

            return KimodoConstraintRigType.Soma30;
        }

        public static void DrawOverrideEditButton(SerializedObject so, KimodoConstraintMarkerBase marker)
        {
            if (so == null || marker == null)
            {
                return;
            }

            bool windowOpen = KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker);
            string label = windowOpen ? "Reopen Edit" : "Edit";
            if (GUILayout.Button(new GUIContent(label, "Open pose edit window. This enables useOverride automatically if needed."), GUILayout.Height(22f)))
            {
                SerializedProperty overrideProp = so.FindProperty("useOverride");
                if (overrideProp != null && !overrideProp.boolValue)
                {
                    overrideProp.boolValue = true;
                    so.ApplyModifiedProperties();
                }

                if (marker.useOverride)
                {
                    KimodoConstraintOverrideEditWindow.ShowWindow(marker);
                }
            }
        }
    }
}

