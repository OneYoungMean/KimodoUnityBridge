using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngineInternal;

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

        public static bool TimelineMatchClipsToPrevious(TimelineClip clip,out string error)
        {
            error=string.Empty;
            try
            {
                PlayableDirector director = TimelineEditor.inspectedDirector;
                if (clip == null || director == null)
                {
                    error = "Timeline clip or inspected director is null.";
                    return false;
                }

                TimelineEditor.state.previewMode = true;
                if (!TimelineEditor.state.previewMode)
                {
                    error = "Timeline preview mode could not be enabled.";
                    return false;
                }

                GameObject sceneObject = TimelineUtility.GetSceneGameObject(director, clip.GetParentTrack());
                if (sceneObject == null)
                {
                    error = "Timeline animation track has no scene binding.";
                    return false;
                }

                Transform matchPoint = ResolveHumanoidHipsMatchPoint(sceneObject) ?? sceneObject.transform;
                TimelineAnimationUtilities.MatchPrevious(clip, matchPoint, director);
                InspectorWindow.RepaintAllInspectors();
                TimelineEditor.Refresh(RefreshReason.ContentsModified);
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return false;
            }
            return true;

        }

        public static bool TimelineMatchClipToWorldHips(
            TimelineClip clip,
            Vector3 targetPosition,
            Quaternion targetRotation,
            bool planarOnly,
            out string error)
        {
            error = string.Empty;
            PlayableDirector director = TimelineEditor.inspectedDirector;
            AnimationTrack track = clip?.GetParentTrack() as AnimationTrack;
            AnimationPlayableAsset asset = clip?.asset as AnimationPlayableAsset;
            if (clip == null || director == null || track == null || asset == null)
            {
                error = "Timeline clip, AnimationTrack, AnimationPlayableAsset, or inspected director is null.";
                return false;
            }

            GameObject sceneObject = TimelineUtility.GetSceneGameObject(director, track);
            Transform hips = ResolveHumanoidHipsMatchPoint(sceneObject);
            if (hips == null)
            {
                error = "Timeline binding Humanoid Hips is unavailable.";
                return false;
            }

            MatchTargetFields fields = asset.useTrackMatchFields
                ? track.matchTargetFields
                : asset.matchTargetFields;
            if (planarOnly)
            {
                fields &= MatchTargetFields.PositionX |
                    MatchTargetFields.PositionZ |
                    MatchTargetFields.RotationY;
            }
            if (fields == MatchTargetFieldConstants.None)
            {
                return true;
            }

            const double timeEpsilon = 0.00001d;
            double cachedTime = director.time;
            TimelineClip previous = TimelineAnimationUtilities.GetPreviousClip(clip);
            bool previousRemoved = false;
            double blendIn = clip.blendInDuration;
            double previousBlendOut = previous != null ? previous.blendOutDuration : 0.0;
            try
            {
                TimelineEditor.state.previewMode = true;
                director.Evaluate();
                clip.blendInDuration = 0.0;
                if (previous != null && previous != clip)
                {
                    previous.blendOutDuration = 0.0;
                    track.RemoveClip(previous);
                    previousRemoved = true;
                    director.RebuildGraph();
                }

                director.time = clip.start + timeEpsilon;
                director.Evaluate();
                TimelineAnimationUtilities.RigidTransform match =
                    TimelineAnimationUtilities.UpdateClipOffsets(
                        asset,
                        track,
                        hips,
                        targetPosition,
                        targetRotation);
                WriteMatchFields(asset, match, fields);
                InspectorWindow.RepaintAllInspectors();
                TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate);
                return true;
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                clip.blendInDuration = blendIn;
                if (previous != null)
                {
                    previous.blendOutDuration = previousBlendOut;
                }
                if (previousRemoved)
                {
                    track.AddClip(previous);
                }
                director.RebuildGraph();
                director.time = cachedTime;
                director.Evaluate();
            }
        }

        private static void WriteMatchFields(
            AnimationPlayableAsset asset,
            TimelineAnimationUtilities.RigidTransform result,
            MatchTargetFields fields)
        {
            Vector3 position = asset.position;
            position.x = fields.HasAny(MatchTargetFields.PositionX) ? result.position.x : position.x;
            position.y = fields.HasAny(MatchTargetFields.PositionY) ? result.position.y : position.y;
            position.z = fields.HasAny(MatchTargetFields.PositionZ) ? result.position.z : position.z;
            asset.position = position;

            if (!fields.HasAny(MatchTargetFieldConstants.Rotation))
            {
                return;
            }

            Vector3 eulers = asset.eulerAngles;
            Vector3 resultEulers = result.rotation.eulerAngles;
            eulers.x = fields.HasAny(MatchTargetFields.RotationX) ? resultEulers.x : eulers.x;
            eulers.y = fields.HasAny(MatchTargetFields.RotationY) ? resultEulers.y : eulers.y;
            eulers.z = fields.HasAny(MatchTargetFields.RotationZ) ? resultEulers.z : eulers.z;
            asset.eulerAngles = AnimationUtility.GetClosestEuler(
                Quaternion.Euler(eulers),
                asset.eulerAngles,
                RotationOrder.OrderZXY);
        }

        internal static Transform ResolveHumanoidHipsMatchPoint(GameObject sceneObject)
        {
            Animator animator = sceneObject != null
                ? sceneObject.GetComponent<Animator>() ?? sceneObject.GetComponentInChildren<Animator>(true)
                : null;
            return animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Hips)
                : null;
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
