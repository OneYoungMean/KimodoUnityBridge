using System;
using KimodoUnityMotionTools.ProjectEditor.Manager;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [CustomEditor(typeof(KimodoRoot2DConstraintMarker))]
    internal sealed class KimodoRoot2DConstraintMarkerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "Purpose: constrain the character root trajectory on the ground plane (X/Z) at a key frame. Optional heading constraint is supported.\\n" +
                "Recommended for path following, locomotion route control, and turn direction control.",
                MessageType.Info);
            EditorGUILayout.Space(4f);

            DrawCommonHeader("Root2D");
            DrawAutoFrameIndices();
            SerializedProperty overrideProp = serializedObject.FindProperty("useOverride");
            bool useOverride = overrideProp != null && overrideProp.boolValue;
            if (!useOverride)
            {
                if (!KimodoConstraintExportUtility.TryBuildAutoConstraintPreview(target as KimodoConstraintMarkerBase, out KimodoConstraintJson preview, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
                else
                {
                    KimodoConstraintMarkerEditorUtility.ApplyRoot2DPreview(serializedObject, preview);
                }
            }

            DrawRoot2DFields(!useOverride);

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(target as KimodoConstraintMarkerBase);
            }
        }

        private void DrawCommonHeader(string type)
        {
            EditorGUILayout.LabelField($"Kimodo Constraint Marker ({type})", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("useOverride"));
            EditorGUILayout.Space(4f);
        }

        private void DrawAutoFrameIndices()
        {
            if (!KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(target as IMarker, out TimelineClip clipRange))
            {
                EditorGUILayout.HelpBox("Owning AnimationPlayableAsset not found. frame_indices will use the currently stored value.", MessageType.Warning);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("frameIndices"));
                return;
            }

            double localSeconds = KimodoConstraintMarkerEditorUtility.GetLocalSecondsInClip(clipRange, ((IMarker)target).time);
            int maxFrameIndex = KimodoConstraintMarkerEditorUtility.GetMaxKimodoFrameIndex(clipRange);
            int autoFrame = KimodoConstraintMarkerEditorUtility.TimeToKimodoFrameIndex(clipRange, ((IMarker)target).time);

            SerializedProperty framesProp = serializedObject.FindProperty("frameIndices");
            KimodoConstraintMarkerEditorUtility.EnsureSingleFrame(framesProp, autoFrame);
            framesProp.GetArrayElementAtIndex(0).intValue = autoFrame;

            int currentFrame = framesProp.GetArrayElementAtIndex(0).intValue;
            int editedFrame = EditorGUILayout.IntField(new GUIContent("Frame Index (Auto from Marker)"), currentFrame);
            EditorGUILayout.LabelField($"Marker Local Time: {localSeconds:F3}s   Clip Start: {clipRange.start:F3}s", EditorStyles.miniLabel);
            if (editedFrame != currentFrame)
            {
                int clampedFrame = Mathf.Clamp(editedFrame, 0, maxFrameIndex);
                framesProp.GetArrayElementAtIndex(0).intValue = clampedFrame;
                KimodoConstraintMarkerEditorUtility.MoveMarkerToFrame(target as IMarker, clipRange, clampedFrame);
            }
        }

        private void DrawRoot2DFields(bool readOnly)
        {
            if (readOnly)
            {
                EditorGUILayout.HelpBox("Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(readOnly);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothRoot2D"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("includeGlobalHeading"));

            SerializedProperty includeGlobalHeadingProp = serializedObject.FindProperty("includeGlobalHeading");
            if (includeGlobalHeadingProp != null && includeGlobalHeadingProp.boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalRootHeading"), true);
            }

            EditorGUI.EndDisabledGroup();
        }
    }

    [CustomEditor(typeof(KimodoFullBodyConstraintMarker))]
    internal sealed class KimodoFullBodyConstraintMarkerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "Purpose: apply a strong full-body pose constraint at a key frame (root position + local joint rotations).\\n" +
                "Recommended when you need the generated motion to match a specific target pose at that frame.",
                MessageType.Info);
            EditorGUILayout.Space(4f);

            DrawCommonHeader("FullBody");
            DrawAutoFrameIndices();
            SerializedProperty overrideProp = serializedObject.FindProperty("useOverride");
            bool useOverride = overrideProp != null && overrideProp.boolValue;
            if (!useOverride)
            {
                if (!KimodoConstraintExportUtility.TryBuildAutoConstraintPreview(target as KimodoConstraintMarkerBase, out KimodoConstraintJson preview, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
                else
                {
                    KimodoConstraintMarkerEditorUtility.ApplyFullBodyPreview(serializedObject, preview);
                }
            }

            DrawFullBodyFields(!useOverride);

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(target as KimodoConstraintMarkerBase);
            }
        }

        private void DrawCommonHeader(string type)
        {
            EditorGUILayout.LabelField($"Kimodo Constraint Marker ({type})", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("useOverride"));
            EditorGUILayout.Space(4f);
        }

        private void DrawAutoFrameIndices()
        {
            if (!KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(target as IMarker, out TimelineClip clipRange))
            {
                EditorGUILayout.HelpBox("Owning AnimationPlayableAsset not found. frame_indices will use the currently stored value.", MessageType.Warning);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("frameIndices"));
                return;
            }

            double localSeconds = KimodoConstraintMarkerEditorUtility.GetLocalSecondsInClip(clipRange, ((IMarker)target).time);
            int maxFrameIndex = KimodoConstraintMarkerEditorUtility.GetMaxKimodoFrameIndex(clipRange);
            int autoFrame = KimodoConstraintMarkerEditorUtility.TimeToKimodoFrameIndex(clipRange, ((IMarker)target).time);

            SerializedProperty framesProp = serializedObject.FindProperty("frameIndices");
            KimodoConstraintMarkerEditorUtility.EnsureSingleFrame(framesProp, autoFrame);
            framesProp.GetArrayElementAtIndex(0).intValue = autoFrame;

            int currentFrame = framesProp.GetArrayElementAtIndex(0).intValue;
            int editedFrame = EditorGUILayout.IntField(new GUIContent("Frame Index (Auto from Marker)"), currentFrame);
            EditorGUILayout.LabelField($"Marker Local Time: {localSeconds:F3}s   Clip Start: {clipRange.start:F3}s", EditorStyles.miniLabel);
            if (editedFrame != currentFrame)
            {
                int clampedFrame = Mathf.Clamp(editedFrame, 0, maxFrameIndex);
                framesProp.GetArrayElementAtIndex(0).intValue = clampedFrame;
                KimodoConstraintMarkerEditorUtility.MoveMarkerToFrame(target as IMarker, clipRange, clampedFrame);
            }
        }

        private void DrawFullBodyFields(bool readOnly)
        {
            if (readOnly)
            {
                EditorGUILayout.HelpBox("Override disabled. Showing sampled result (read-only).", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(readOnly);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothRoot2D"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("rootPositions"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localJointRots"), true);
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
                // end-effector has no manual override mode
                overrideProp.boolValue = false;
                EditorGUILayout.Toggle(new GUIContent("useOverride"), false);
            }
            else
            {
                EditorGUILayout.PropertyField(overrideProp);
            }

            DrawAutoFrameIndices();
            bool useOverride = !isCustomEndEffector && overrideProp != null && overrideProp.boolValue;

            if (!useOverride)
            {
                if (!KimodoConstraintExportUtility.TryBuildAutoConstraintPreview(target as KimodoConstraintMarkerBase, out KimodoConstraintJson preview, out string error))
                {
                    EditorGUILayout.HelpBox($"Auto preview unavailable: {error}", MessageType.Warning);
                }
                else
                {
                    KimodoConstraintMarkerEditorUtility.ApplyEndEffectorPreview(serializedObject, preview, isCustomEndEffector);
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
        }

        private void DrawAutoFrameIndices()
        {
            if (!KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(target as IMarker, out TimelineClip clipRange))
            {
                EditorGUILayout.HelpBox("Owning AnimationPlayableAsset not found. frame_indices will use the currently stored value.", MessageType.Warning);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("frameIndices"));
                return;
            }

            double localSeconds = KimodoConstraintMarkerEditorUtility.GetLocalSecondsInClip(clipRange, ((IMarker)target).time);
            int maxFrameIndex = KimodoConstraintMarkerEditorUtility.GetMaxKimodoFrameIndex(clipRange);
            int autoFrame = KimodoConstraintMarkerEditorUtility.TimeToKimodoFrameIndex(clipRange, ((IMarker)target).time);

            SerializedProperty framesProp = serializedObject.FindProperty("frameIndices");
            KimodoConstraintMarkerEditorUtility.EnsureSingleFrame(framesProp, autoFrame);
            framesProp.GetArrayElementAtIndex(0).intValue = autoFrame;

            int currentFrame = framesProp.GetArrayElementAtIndex(0).intValue;
            int editedFrame = EditorGUILayout.IntField(new GUIContent("Frame Index (Auto from Marker)"), currentFrame);
            EditorGUILayout.LabelField($"Marker Local Time: {localSeconds:F3}s   Clip Start: {clipRange.start:F3}s", EditorStyles.miniLabel);
            if (editedFrame != currentFrame)
            {
                int clampedFrame = Mathf.Clamp(editedFrame, 0, maxFrameIndex);
                framesProp.GetArrayElementAtIndex(0).intValue = clampedFrame;
                KimodoConstraintMarkerEditorUtility.MoveMarkerToFrame(target as IMarker, clipRange, clampedFrame);
            }
        }

        private static string GetTipByType(string typeName)
        {
            switch (typeName)
            {
                case "left-hand":
                    return "Purpose: constrain the left-hand end-effector chain position/orientation at a key frame.\\nRecommended for grab, wave, and pointing control.";
                case "right-hand":
                    return "Purpose: constrain the right-hand end-effector chain position/orientation at a key frame.\\nRecommended for grab, wave, and pointing control.";
                case "left-foot":
                    return "Purpose: constrain the left-foot end-effector chain position/orientation at a key frame.\\nRecommended for foot placement, stepping targets, and anti-sliding control.";
                case "right-foot":
                    return "Purpose: constrain the right-foot end-effector chain position/orientation at a key frame.\\nRecommended for foot placement, stepping targets, and anti-sliding control.";
                default:
                    return "Purpose: custom end-effector constraint (joint_names can include LeftHand/RightHand/LeftFoot/RightFoot/Hips).\\n" +
                           "Recommended for mixed multi-target constraints (for example, hand and foot targets at the same time).";
            }
        }

        private void DrawEEFields(string typeName, bool readOnly)
        {
            EditorGUI.BeginDisabledGroup(readOnly);
            SerializedProperty jointNamesProp = serializedObject.FindProperty("jointNames");
            if (jointNamesProp != null && typeName == "end-effector")
            {
                EditorGUILayout.PropertyField(jointNamesProp, true);
            }
            else if (typeName != "end-effector")
            {
                EditorGUILayout.HelpBox("Fixed joint group marker type; joint_names is determined by marker class.", MessageType.None);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothRoot2D"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("rootPositions"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localJointRots"), true);
            EditorGUI.EndDisabledGroup();
        }
    }

    internal static class KimodoConstraintMarkerEditorUtility
    {
        public const double KimodoFps = 30.0;
        private const double TimeEpsilon = 1e-14;

        public static double GetLocalSecondsInClip(TimelineClip clipRange, double globalTime)
        {
            if (clipRange == null)
            {
                return 0.0;
            }

            // Reuse Timeline's public conversion utility for global->clip local time.
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

        public static int GetMaxKimodoFrameIndex(TimelineClip clipRange)
        {
            if (clipRange == null)
            {
                return 0;
            }

            int frameCount = ToFramesTimelineStyle(clipRange.duration, KimodoFps);
            return Math.Max(0, frameCount - 1);
        }

        public static int TimeToKimodoFrameIndex(TimelineClip clipRange, double globalTime)
        {
            if (clipRange == null)
            {
                return 0;
            }

            double localSeconds = GetLocalSecondsInClip(clipRange, globalTime);
            int frame = ToFramesTimelineStyle(localSeconds, KimodoFps);
            int maxIndex = GetMaxKimodoFrameIndex(clipRange);
            return Mathf.Clamp(frame, 0, maxIndex);
        }

        private static int ToFramesTimelineStyle(double timeSeconds, double fps)
        {
            // Mirrors Unity Timeline's TimeUtility.ToFrames positive-time path (with tolerance),
            // while remaining callable from this assembly.
            double tolerance = Math.Max(Math.Abs(timeSeconds), 1.0) * fps * TimeEpsilon;
            return (int)Math.Floor(timeSeconds * fps + tolerance);
        }

        public static void ApplyRoot2DPreview(SerializedObject so, KimodoConstraintJson preview)
        {
            if (so == null || preview == null)
            {
                return;
            }

            SetIntList(so.FindProperty("frameIndices"), preview.frame_indices);
            SetVector2List(so.FindProperty("smoothRoot2D"), preview.smooth_root_2d);

            SerializedProperty includeHeadingProp = so.FindProperty("includeGlobalHeading");
            if (includeHeadingProp != null)
            {
                bool hasHeading = preview.global_root_heading != null && preview.global_root_heading.Count > 0;
                includeHeadingProp.boolValue = hasHeading;
                if (hasHeading)
                {
                    SetVector2List(so.FindProperty("globalRootHeading"), preview.global_root_heading);
                }
            }
        }

        public static void ApplyFullBodyPreview(SerializedObject so, KimodoConstraintJson preview)
        {
            if (so == null || preview == null)
            {
                return;
            }

            SetIntList(so.FindProperty("frameIndices"), preview.frame_indices);
            SetVector2List(so.FindProperty("smoothRoot2D"), preview.smooth_root_2d);
            SetVector3List(so.FindProperty("rootPositions"), preview.root_positions);
            SetAxisAngleFrames(so.FindProperty("localJointRots"), preview.local_joints_rot);
        }

        public static void ApplyEndEffectorPreview(SerializedObject so, KimodoConstraintJson preview, bool allowJointNames)
        {
            if (so == null || preview == null)
            {
                return;
            }

            SetIntList(so.FindProperty("frameIndices"), preview.frame_indices);
            if (allowJointNames)
            {
                SetStringList(so.FindProperty("jointNames"), preview.joint_names);
            }
            SetVector2List(so.FindProperty("smoothRoot2D"), preview.smooth_root_2d);
            SetVector3List(so.FindProperty("rootPositions"), preview.root_positions);
            SetAxisAngleFrames(so.FindProperty("localJointRots"), preview.local_joints_rot);
        }

        private static void SetIntList(SerializedProperty prop, System.Collections.Generic.List<int> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            int count = values != null ? values.Count : 0;
            prop.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                prop.GetArrayElementAtIndex(i).intValue = values[i];
            }
        }

        private static void SetStringList(SerializedProperty prop, System.Collections.Generic.List<string> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            int count = values != null ? values.Count : 0;
            prop.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                prop.GetArrayElementAtIndex(i).stringValue = values[i] ?? string.Empty;
            }
        }

        private static void SetVector2List(SerializedProperty prop, System.Collections.Generic.List<float[]> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            int count = values != null ? values.Count : 0;
            prop.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                SerializedProperty element = prop.GetArrayElementAtIndex(i);
                float[] v = values[i];
                element.vector2Value = (v != null && v.Length >= 2) ? new Vector2(v[0], v[1]) : Vector2.zero;
            }
        }

        private static void SetVector3List(SerializedProperty prop, System.Collections.Generic.List<float[]> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            int count = values != null ? values.Count : 0;
            prop.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                SerializedProperty element = prop.GetArrayElementAtIndex(i);
                float[] v = values[i];
                element.vector3Value = (v != null && v.Length >= 3) ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;
            }
        }

        private static void SetAxisAngleFrames(SerializedProperty prop, System.Collections.Generic.List<float[][]> frames)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            int frameCount = frames != null ? frames.Count : 0;
            prop.arraySize = frameCount;
            for (int i = 0; i < frameCount; i++)
            {
                SerializedProperty frameProp = prop.GetArrayElementAtIndex(i);
                SerializedProperty jointsProp = frameProp.FindPropertyRelative("joints");
                if (jointsProp == null || !jointsProp.isArray)
                {
                    continue;
                }

                float[][] joints = frames[i];
                int jointCount = joints != null ? joints.Length : 0;
                jointsProp.arraySize = jointCount;
                for (int j = 0; j < jointCount; j++)
                {
                    SerializedProperty jointProp = jointsProp.GetArrayElementAtIndex(j);
                    float[] axis = joints[j];
                    jointProp.vector3Value = (axis != null && axis.Length >= 3)
                        ? new Vector3(axis[0], axis[1], axis[2])
                        : Vector3.zero;
                }
            }
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

        public static void EnsureSingleFrame(SerializedProperty framesProp, int fallbackValue)
        {
            if (framesProp == null)
            {
                return;
            }

            if (!framesProp.isArray)
            {
                return;
            }

            if (framesProp.arraySize == 0)
            {
                framesProp.InsertArrayElementAtIndex(0);
                framesProp.GetArrayElementAtIndex(0).intValue = fallbackValue;
            }
            else if (framesProp.arraySize > 1)
            {
                int first = framesProp.GetArrayElementAtIndex(0).intValue;
                framesProp.arraySize = 1;
                framesProp.GetArrayElementAtIndex(0).intValue = first;
            }
            else
            {
                int current = framesProp.GetArrayElementAtIndex(0).intValue;
                if (current < 0)
                {
                    framesProp.GetArrayElementAtIndex(0).intValue = fallbackValue;
                }
            }
        }

        public static void MoveMarkerToFrame(IMarker marker, TimelineClip clipRange, int frameIndex)
        {
            if (marker == null || clipRange == null)
            {
                return;
            }

            int clampedFrame = Mathf.Clamp(frameIndex, 0, GetMaxKimodoFrameIndex(clipRange));
            double localSeconds = clampedFrame / KimodoFps;
            double absTime = clipRange.start + localSeconds;
            absTime = Math.Min(clipRange.end, absTime);

            Undo.RecordObject(marker as UnityEngine.Object, "Move Kimodo Constraint Marker");
            marker.time = absTime;
            EditorUtility.SetDirty(marker as UnityEngine.Object);
            KimodoEditorCommandManager.Dispatch(
                new ConstraintSnapshotRefreshCommand());
        }

        public static void NotifyInspectorChanged(KimodoConstraintMarkerBase marker)
        {
            if (marker != null)
            {
                EditorUtility.SetDirty(marker);
            }

            KimodoEditorCommandManager.Dispatch(new ConstraintSnapshotRefreshCommand());
            SceneView.RepaintAll();
        }
    }
}


