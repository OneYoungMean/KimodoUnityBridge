using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace TimelineInject
{
    public static class KimodoTimelinePreviewRefreshUtility
    {
        public static void InitializeSceneOffset(AnimationTrack track, Animator animator)
        {
            if (track == null || animator == null || track.trackOffset != TrackOffset.ApplySceneOffsets) return;
            // Match AnimationTrack.GatherProperties when no Timeline window has
            // initialized the editor preview cache yet. Runtime reads the binding.
            track.sceneOffsetPosition = animator.transform.localPosition;
            track.sceneOffsetRotation = animator.transform.localEulerAngles;
        }

        private static readonly GUIContent TransformOffsetTitle = EditorGUIUtility.TrTextContent(
            "Clip Transform Offsets",
            "Use this to offset the root transform position and rotation relative to the track when playing this clip");

        private static readonly GUIContent RotationText = EditorGUIUtility.TrTextContent("Rotation");
        private static readonly GUIContent MatchTargetFieldsTitle = EditorGUIUtility.TrTextContent(
            "Offsets Match Fields",
            "Fields to apply when matching offsets on clips. The defaults can be set on the track.");

        private static readonly GUIContent UseDefaultsText = EditorGUIUtility.TrTextContent("Use defaults");
        private static readonly GUIContent RemoveStartOffsetText = EditorGUIUtility.TrTextContent(
            "Remove Start Offset",
            "Makes playback of the clip play relative to first key of the root transform");

        public static void RefreshIfPreviewing()
        {
            if (TimelineEditor.inspectedAsset == null)
            {
                return;
            }

            var state = TimelineEditor.state;
            if (state == null || !state.previewMode)
            {
                return;
            }

            state.previewMode = false;
            state.previewMode = true;
            TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        public static void RefreshEditorWorkflow(RefreshReason reason)
        {
            EditorApplication.QueuePlayerLoopUpdate();
            TimelineEditor.Refresh(reason | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
            SceneView.RepaintAll();
        }

        public static bool TryEnablePreview()
        {
            var state = TimelineEditor.state;
            if (state == null)
            {
                return false;
            }

            state.previewMode = true;
            return true;
        }

        public static void DisablePreview()
        {
            var state = TimelineEditor.state;
            if (state == null || !state.previewMode)
            {
                return;
            }

            state.previewMode = false;
            TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        public static void DisablePreview(PlayableDirector director)
        {
            TryDisablePreview(director);
        }

        public static bool TryDisablePreview(PlayableDirector director)
        {
            var state = TimelineEditor.state;
            if (director == null || TimelineEditor.inspectedDirector != director || state == null || !state.previewMode)
            {
                return false;
            }

            DisablePreview();
            return true;
        }

        public static GameObject InstantiateForAnimatorPreview(Object original)
        {
            return EditorUtility.InstantiateForAnimatorPreview(original) as GameObject;
        }

        public static Vector3 GetBodyPosition(Animator animator)
        {
            return animator != null ? animator.bodyPositionInternal : Vector3.zero;
        }

        public static void ApplyWireMaterial()
        {
            HandleUtility.ApplyWireMaterial();
        }

        public static AnimationClip[] GetAnimationClipsFlattened(UnityEditor.Animations.BlendTree blendTree)
        {
            return blendTree.GetAnimationClipsFlattened();
        }

        public static string CalculateBestFittingPreviewGameObject(ModelImporter modelImporter)
        {
            return modelImporter.CalculateBestFittingPreviewGameObject();
        }

        public static void SetPreview(ModelImporterAnimationType type, GameObject go)
        {
            UnityEditor.AvatarPreviewSelection.SetPreview(type, go);
        }

        public static int GetPreviewCullingLayer()
        {
            return Camera.PreviewCullingLayer;
        }

        public static int TimelineTimeToFrame(double time, double frameRate)
        {
            return TimeUtility.ToFrames(time, frameRate);
        }

        public static double TimelineFrameToTime(int frame, double frameRate)
        {
            return TimeUtility.FromFrames(frame, frameRate);
        }

        public static bool ApproximatelyTimelineTime(double left, double right)
        {
            return Mathf.Approximately((float)left, (float)right);
        }

        public static bool GetTImelineWindowLockState()
        {
            return TimelineEditor.window.locked;
        }

        public static void SetTimelineWindowLockState(bool locked)
        {
            TimelineEditor.window.locked = locked;
        }

        public static void DrawAnimationPlayableAssetClipOffsetSettings(
            SerializedProperty positionProperty,
            SerializedProperty rotationProperty,
            SerializedProperty useTrackMatchFieldsProperty,
            SerializedProperty matchTargetFieldsProperty,
            SerializedProperty removeStartOffsetProperty)
        {
            if (positionProperty == null ||
                rotationProperty == null ||
                useTrackMatchFieldsProperty == null ||
                matchTargetFieldsProperty == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(TransformOffsetTitle);
            EditorGUI.indentLevel++;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(positionProperty);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(rotationProperty, RotationText);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
            EditorGUI.indentLevel--;

            DrawAnimationPlayableAssetMatchFields(useTrackMatchFieldsProperty, matchTargetFieldsProperty);

            if (removeStartOffsetProperty != null)
            {
                EditorGUILayout.PropertyField(removeStartOffsetProperty, RemoveStartOffsetText);
            }
        }

        /// <summary>
        /// Draws the six Kimodo loop lock booleans with the same two-row layout
        /// and axis hit regions used by Timeline's MatchTargetsFieldGUI.
        /// </summary>
        public static void DrawLoopLockFields(
            SerializedProperty matchTargetFieldsProxy,
            SerializedProperty positionX,
            SerializedProperty positionY,
            SerializedProperty positionZ,
            SerializedProperty rotationX,
            SerializedProperty rotationY,
            SerializedProperty rotationZ)
        {
            if (matchTargetFieldsProxy == null ||
                positionX == null || positionY == null || positionZ == null ||
                rotationX == null || rotationY == null || rotationZ == null)
            {
                return;
            }

            int flags = 0;
            if (positionX.boolValue) flags |= (int)MatchTargetFields.PositionX;
            if (positionY.boolValue) flags |= (int)MatchTargetFields.PositionY;
            if (positionZ.boolValue) flags |= (int)MatchTargetFields.PositionZ;
            if (rotationX.boolValue) flags |= (int)MatchTargetFields.RotationX;
            if (rotationY.boolValue) flags |= (int)MatchTargetFields.RotationY;
            if (rotationZ.boolValue) flags |= (int)MatchTargetFields.RotationZ;

            int previousProxyValue = matchTargetFieldsProxy.intValue;
            matchTargetFieldsProxy.intValue = flags;
            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue =
                positionX.hasMultipleDifferentValues || positionY.hasMultipleDifferentValues || positionZ.hasMultipleDifferentValues ||
                rotationX.hasMultipleDifferentValues || rotationY.hasMultipleDifferentValues || rotationZ.hasMultipleDifferentValues;
            AnimationTrackInspector.MatchTargetsFieldGUI(matchTargetFieldsProxy);
            EditorGUI.showMixedValue = previousShowMixedValue;

            int updatedFlags = matchTargetFieldsProxy.intValue;
            if (updatedFlags == flags)
            {
                matchTargetFieldsProxy.intValue = previousProxyValue;
                return;
            }

            positionX.boolValue = (updatedFlags & (int)MatchTargetFields.PositionX) != 0;
            positionY.boolValue = (updatedFlags & (int)MatchTargetFields.PositionY) != 0;
            positionZ.boolValue = (updatedFlags & (int)MatchTargetFields.PositionZ) != 0;
            rotationX.boolValue = (updatedFlags & (int)MatchTargetFields.RotationX) != 0;
            rotationY.boolValue = (updatedFlags & (int)MatchTargetFields.RotationY) != 0;
            rotationZ.boolValue = (updatedFlags & (int)MatchTargetFields.RotationZ) != 0;
        }

        private static void DrawAnimationPlayableAssetMatchFields(
            SerializedProperty useTrackMatchFieldsProperty,
            SerializedProperty matchTargetFieldsProperty)
        {
            Rect rect = EditorGUILayout.GetControlRect(true);
            EditorGUI.BeginProperty(rect, MatchTargetFieldsTitle, useTrackMatchFieldsProperty);
            rect = EditorGUI.PrefixLabel(rect, MatchTargetFieldsTitle);

            int oldIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            EditorGUI.BeginChangeCheck();
            bool useDefaults = useTrackMatchFieldsProperty.boolValue;
            useDefaults = EditorGUI.ToggleLeft(rect, UseDefaultsText, useDefaults);
            if (EditorGUI.EndChangeCheck())
            {
                useTrackMatchFieldsProperty.boolValue = useDefaults;
            }

            EditorGUI.indentLevel = oldIndent;
            EditorGUI.EndProperty();

            if (!useDefaults || useTrackMatchFieldsProperty.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                AnimationTrackInspector.MatchTargetsFieldGUI(matchTargetFieldsProperty);
                EditorGUI.indentLevel--;
            }
        }

        public static int GetDirtyIndex(TrackAsset trackAsset)
        {
            return trackAsset != null ? trackAsset.DirtyIndex : -1;
        }

        public static void ResolveAnimationTrackOffset(
            AnimationTrack track,
            Animator animator,
            out Vector3 position,
            out Quaternion rotation)
        {
            ResolveAnimationTrackOffset(
                track,
                animator,
                out position,
                out rotation,
                out _);
        }

        public static void ResolveAnimationTrackOffset(
            AnimationTrack track,
            Animator animator,
            out Vector3 position,
            out Quaternion rotation,
            out bool isSceneOffset)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            isSceneOffset = false;
            if (track == null)
            {
                return;
            }

            bool useTransformOffset = track.trackOffset == TrackOffset.ApplyTransformOffsets ||
                (track.trackOffset == TrackOffset.Auto &&
                 (animator == null || animator.runtimeAnimatorController == null));
            isSceneOffset = !useTransformOffset;
            position = useTransformOffset ? track.position : track.sceneOffsetPosition;
            rotation = useTransformOffset
                ? track.rotation
                : Quaternion.Euler(track.sceneOffsetRotation);
            rotation.Normalize();

            Transform parent = animator != null ? animator.transform.parent : null;
            if (parent != null)
            {
                position = parent.TransformPoint(position);
                rotation = (parent.rotation * rotation).normalized;
            }
        }
    }
}
