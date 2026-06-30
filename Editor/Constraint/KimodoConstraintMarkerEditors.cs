using System;
using System.Collections.Generic;
using System.Globalization;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoConstraintMarkerSelectionPreviewCleanup
    {
        static KimodoConstraintMarkerSelectionPreviewCleanup()
        {
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.quitting += ClearAll;
        }

        private static void OnSelectionChanged()
        {
            KimodoConstraintMarkerEditorUtility.HandleSelectionChangedForPreviewCleanup(Selection.activeObject as KimodoConstraintMarkerBase);
        }

        private static void ClearAll()
        {
            KimodoConstraintMarkerEditorUtility.HandleSelectionChangedForPreviewCleanup(null);
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

            if (!windowOpen && markerTarget != null && !KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCacheIfNeeded(markerTarget, out string poseError) && !string.IsNullOrWhiteSpace(poseError))
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
            "Purpose: constrain the character root trajectory on the ground plane (X/Z) at a key frame. Optional heading constraint is supported.\n" +
            "Recommended for path following, locomotion route control, and turn direction control.";

        protected override void DrawFields(bool readOnly)
        {
            if (readOnly)
            {
                EditorGUILayout.HelpBox("Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(readOnly);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sampleData.kimodoRootPosition"));
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
        private void OnDisable()
        {
            KimodoConstraintMarkerEditorUtility.ClearMarkerPoseCachePreview(target as KimodoConstraintMarkerBase, keepIfOverrideWindowOpen: true);
        }

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

            if (!windowOpen && markerTarget != null && !KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCacheIfNeeded(markerTarget, out string poseError) && !string.IsNullOrWhiteSpace(poseError))
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

        private const string DefaultBridgeModelName = "Kimodo-SOMA-RP-v1";

        private struct MarkerSamplingContext
        {
            public TimelineClip ClipRange;
            public TrackAsset Track;
            public Animator Animator;
            public AnimationClip SourceClip;
            public Avatar SourceAvatar;
            public string ModelName;
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
            public double LocalTime;
            public string ModelName;
            public int TrackId;
            public int AnimatorId;
            public int SourceClipId;
            public int ClipAssetId;
            public int SourceAvatarId;
            public double ClipStart;
            public double ClipDuration;
            public double ClipIn;
            public double TimeScale;
            public float SourceClipLength;
            public float SourceClipFrameRate;
            public bool HasRootHeading;
            public Vector3 KimodoRootPosition;
            public Vector3 UnityRootPos;
            public Quaternion UnityRootRot;
            public string[] JointNames;
        }

        private struct PoseRenderSignatureSnapshot
        {
            public string ConstraintType;
            public double SampleTime;
            public int ClipId;
            public int AnimatorId;
            public string ModelName;
            public KimodoConstraintRigType RigType;
            public bool HasRootHeading;
            public Vector3 KimodoRootPosition;
            public Vector2 RootHeading;
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
                    if (clip.asset is AnimationPlayableAsset && marker.time >= clip.start && marker.time <= clip.end)
                    {
                        clipRange = clip;
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool TryUpdateAutoSampleMarkerData(KimodoConstraintMarkerBase marker, bool forceRefresh, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!TryGetClipRangeForMarker(marker, out TimelineClip clipRange) || clipRange == null)
            {
                error = $"clip range not found at marker time {marker.time.ToString("F4", CultureInfo.InvariantCulture)}";
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
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null || animator.transform == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            if (!KimodoMarkerSamplingUtility.TryResolveAnimationClipFromTimelineClip(clipRange, out AnimationClip sourceClip, out error))
            {
                return false;
            }

            KimodoLocalAvatarUtility.AvatarResolveResult sourceAvatarResult = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(animator.gameObject);
            Avatar sourceAvatar = sourceAvatarResult.Avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = $"Resolve source avatar failed: {sourceAvatarResult.Error}";
                return false;
            }

            MarkerSamplingContext context = new MarkerSamplingContext
            {
                ClipRange = clipRange,
                Track = track,
                Animator = animator,
                SourceClip = sourceClip,
                SourceAvatar = sourceAvatar,
                ModelName = ResolveModelName(clipRange)
            };

            int id = marker.GetInstanceID();
            if (!forceRefresh &&
                AutoSampleCache.TryGetValue(id, out AutoSampleCacheEntry cached) &&
                AutoSampleSnapshotMatches(marker, context, cached.Snapshot))
            {
                error = cached.Error ?? string.Empty;
                return cached.Success;
            }

            double sampleTime = marker.time;
            double sourceSampleTime = KimodoMarkerSamplingUtility.ResolveSourceClipSampleTime(clipRange, sampleTime);
            if (!KimodoRetargetToolsEditor.TrySampleMarkerForClip(
                    sourceClip,
                    marker.ConstraintType,
                    sourceSampleTime,
                    sourceAvatar,
                    null,
                    animator,
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

            sample.sampleTime = sampleTime;
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

            if (!TryRenderMarkerToPoseCache(marker, out string poseError))
            {
                error = string.IsNullOrWhiteSpace(error)
                    ? poseError
                    : string.IsNullOrWhiteSpace(poseError)
                        ? error
                        : $"{error}; {poseError}";
                return false;
            }

            SceneView.RepaintAll();
            return true;
        }

        private static AutoSampleSignatureSnapshot BuildAutoSampleSnapshot(
            KimodoConstraintMarkerBase marker,
            MarkerSamplingContext context,
            KimodoMarkerSampleResult sample = null)
        {
            KimodoMarkerSampleResult source = sample ?? marker?.SampleData;
            int clipAssetId = context.ClipRange != null && context.ClipRange.asset is UnityEngine.Object clipAsset
                ? clipAsset.GetInstanceID()
                : 0;
            double globalTime = marker != null ? Math.Max(0.0, marker.time) : 0.0;
            return new AutoSampleSignatureSnapshot
            {
                ConstraintType = marker != null ? marker.ConstraintType ?? string.Empty : string.Empty,
                GlobalTime = globalTime,
                LocalTime = KimodoMarkerSamplingUtility.ClampLocalSampleTime(context.ClipRange, globalTime),
                ModelName = context.ModelName ?? string.Empty,
                TrackId = context.Track != null ? context.Track.GetInstanceID() : 0,
                AnimatorId = context.Animator != null ? context.Animator.GetInstanceID() : 0,
                SourceClipId = context.SourceClip != null ? context.SourceClip.GetInstanceID() : 0,
                ClipAssetId = clipAssetId,
                SourceAvatarId = context.SourceAvatar != null ? context.SourceAvatar.GetInstanceID() : 0,
                ClipStart = context.ClipRange != null ? context.ClipRange.start : 0.0,
                ClipDuration = context.ClipRange != null ? context.ClipRange.duration : 0.0,
                ClipIn = context.ClipRange != null ? context.ClipRange.clipIn : 0.0,
                TimeScale = context.ClipRange != null ? context.ClipRange.timeScale : 0.0,
                SourceClipLength = context.SourceClip != null ? context.SourceClip.length : 0f,
                SourceClipFrameRate = context.SourceClip != null ? context.SourceClip.frameRate : 0f,
                HasRootHeading = source != null && source.hasRootHeading,
                KimodoRootPosition = source != null ? source.kimodoRootPosition : default,
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
            int clipAssetId = context.ClipRange != null && context.ClipRange.asset is UnityEngine.Object clipAsset
                ? clipAsset.GetInstanceID()
                : 0;
            double globalTime = marker != null ? Math.Max(0.0, marker.time) : 0.0;
            return string.Equals(snapshot.ConstraintType ?? string.Empty, marker != null ? marker.ConstraintType ?? string.Empty : string.Empty, StringComparison.Ordinal) &&
                Math.Abs(snapshot.GlobalTime - globalTime) <= 1e-9 &&
                Math.Abs(snapshot.LocalTime - KimodoMarkerSamplingUtility.ClampLocalSampleTime(context.ClipRange, globalTime)) <= 1e-9 &&
                string.Equals(snapshot.ModelName ?? string.Empty, context.ModelName ?? string.Empty, StringComparison.Ordinal) &&
                snapshot.TrackId == (context.Track != null ? context.Track.GetInstanceID() : 0) &&
                snapshot.AnimatorId == (context.Animator != null ? context.Animator.GetInstanceID() : 0) &&
                snapshot.SourceClipId == (context.SourceClip != null ? context.SourceClip.GetInstanceID() : 0) &&
                snapshot.ClipAssetId == clipAssetId &&
                snapshot.SourceAvatarId == (context.SourceAvatar != null ? context.SourceAvatar.GetInstanceID() : 0) &&
                Math.Abs(snapshot.ClipStart - (context.ClipRange != null ? context.ClipRange.start : 0.0)) <= 1e-9 &&
                Math.Abs(snapshot.ClipDuration - (context.ClipRange != null ? context.ClipRange.duration : 0.0)) <= 1e-9 &&
                Math.Abs(snapshot.ClipIn - (context.ClipRange != null ? context.ClipRange.clipIn : 0.0)) <= 1e-9 &&
                Math.Abs(snapshot.TimeScale - (context.ClipRange != null ? context.ClipRange.timeScale : 0.0)) <= 1e-9 &&
                Mathf.Abs(snapshot.SourceClipLength - (context.SourceClip != null ? context.SourceClip.length : 0f)) <= 1e-6f &&
                Mathf.Abs(snapshot.SourceClipFrameRate - (context.SourceClip != null ? context.SourceClip.frameRate : 0f)) <= 1e-6f &&
                snapshot.HasRootHeading == (sample != null && sample.hasRootHeading) &&
                Vector3Approximately(snapshot.KimodoRootPosition, sample != null ? sample.kimodoRootPosition : default) &&
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
                ClearMarkerEditorCaches(marker);
                EditorUtility.SetDirty(marker);
            }

            SceneView.RepaintAll();
        }

        public static void HandleSelectionChangedForPreviewCleanup(KimodoConstraintMarkerBase selectedMarker)
        {
            var markerIds = new List<int>(PoseRenderSignatures.Keys);
            for (int i = 0; i < markerIds.Count; i++)
            {
                int markerId = markerIds[i];
                KimodoConstraintMarkerBase marker = EditorUtility.InstanceIDToObject(markerId) as KimodoConstraintMarkerBase;
                if (marker == null)
                {
                    AutoSampleCache.Remove(markerId);
                    PoseRenderSignatures.Remove(markerId);
                    KimodoConstraintPoseCache.DestroyEntriesForItemId(GetCachedIntString(markerId));
                    continue;
                }

                if (ReferenceEquals(marker, selectedMarker))
                {
                    continue;
                }

                if (KimodoConstraintOverrideEditWindow.IsOpenForMarker(marker))
                {
                    continue;
                }

                ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: true);
            }
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
            string modelName = ResolveModelName(clipRange);
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            int clipContextId = playableClip != null
                ? playableClip.GetInstanceID()
                : ((clipRange.asset as UnityEngine.Object) != null
                    ? (clipRange.asset as UnityEngine.Object).GetInstanceID()
                    : track.GetInstanceID());
            context = new PoseCacheRenderContext(clipContextId, animator.GetInstanceID(), modelName, rigType);
            return true;
        }

        public static bool TryRenderMarkerToPoseCacheIfNeeded(KimodoConstraintMarkerBase marker, out string error)
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

            int id = marker.GetInstanceID();
            if (PoseRenderSignatures.TryGetValue(id, out PoseRenderCacheEntry cached) &&
                RenderSnapshotMatches(marker, context, cached.Snapshot))
            {
                error = cached.Error ?? string.Empty;
                return cached.Success;
            }

            if (!TryRenderMarkerToPoseCache(marker, context, out KimodoMarkerSampleResult sample, out error))
            {
                PoseRenderSignatures[id] = new PoseRenderCacheEntry
                {
                    Snapshot = BuildRenderSnapshot(marker, context, marker.SampleData),
                    Success = false,
                    Error = error ?? string.Empty
                };
                return false;
            }

            PoseRenderSignatures[id] = new PoseRenderCacheEntry
            {
                Snapshot = BuildRenderSnapshot(marker, context, sample),
                Success = true,
                Error = string.Empty
            };
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
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
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

            PoseRenderSignatures[marker.GetInstanceID()] = new PoseRenderCacheEntry
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
                if (marker == null)
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
                if (GUILayout.Button(new GUIContent(label, "Open pose edit window. This enables useOverride automatically if needed."), GUILayout.Height(22f)))
                {
                    SerializedProperty overrideProp = so.FindProperty("useOverride");
                    if (overrideProp != null && !overrideProp.boolValue)
                    {
                        overrideProp.boolValue = true;
                        so.ApplyModifiedProperties();
                        ClearMarkerEditorCaches(marker);
                    }

                    if (marker.useOverride)
                    {
                        KimodoConstraintOverrideEditWindow.ShowWindow(marker);
                    }
                }
            }
        }

        private static void ClearMarkerEditorCaches(KimodoConstraintMarkerBase marker)
        {
            if (marker == null)
            {
                return;
            }

            int id = marker.GetInstanceID();
            AutoSampleCache.Remove(id);
            PoseRenderSignatures.Remove(id);
        }

        internal static string GetMarkerEntryId(KimodoConstraintMarkerBase marker)
        {
            return marker == null ? string.Empty : GetCachedIntString(marker.GetInstanceID());
        }

        private static PoseRenderSignatureSnapshot BuildRenderSnapshot(
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext context,
            KimodoMarkerSampleResult sample)
        {
            KimodoMarkerSampleResult source = sample ?? marker?.SampleData;
            return new PoseRenderSignatureSnapshot
            {
                ConstraintType = marker != null ? marker.ConstraintType ?? string.Empty : string.Empty,
                SampleTime = source != null ? source.sampleTime : 0.0,
                ClipId = context.ClipId,
                AnimatorId = context.AnimatorId,
                ModelName = context.ModelName ?? string.Empty,
                RigType = context.RigType,
                HasRootHeading = source != null && source.hasRootHeading,
                KimodoRootPosition = source != null ? source.kimodoRootPosition : default,
                RootHeading = source != null ? source.rootHeading : default,
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
            return string.Equals(snapshot.ConstraintType ?? string.Empty, marker != null ? marker.ConstraintType ?? string.Empty : string.Empty, StringComparison.Ordinal) &&
                Math.Abs(snapshot.SampleTime - (sample != null ? sample.sampleTime : 0.0)) <= 1e-9 &&
                snapshot.ClipId == context.ClipId &&
                snapshot.AnimatorId == context.AnimatorId &&
                string.Equals(snapshot.ModelName ?? string.Empty, context.ModelName ?? string.Empty, StringComparison.Ordinal) &&
                snapshot.RigType == context.RigType &&
                snapshot.HasRootHeading == (sample != null && sample.hasRootHeading) &&
                Vector3Approximately(snapshot.KimodoRootPosition, sample != null ? sample.kimodoRootPosition : default) &&
                Vector2Approximately(snapshot.RootHeading, sample != null ? sample.rootHeading : default) &&
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
                sample.constraintType ?? string.Empty,
                FormatDouble(sample.sampleTime),
                sample.rigType.ToString(),
                sample.hasRootHeading ? "1" : "0",
                FormatVector3(sample.kimodoRootPosition),
                FormatVector2(sample.rootHeading),
                BuildStringListSignature(sample.jointNames),
                BuildVector3ListSignature(sample.localAxisAngles),
                BuildIntListSignature(sample.sampledJointIndices));
        }

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

