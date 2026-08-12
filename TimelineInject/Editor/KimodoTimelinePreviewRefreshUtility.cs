using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace TimelineInject
{
    public static class KimodoTimelinePreviewRefreshUtility
    {
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
