using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace TimelineInject
{
    public static class KimodoTimelinePreviewRefreshUtility
    {
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

        public static Vector3 InstantiateForAnimatorPreview(Animator animator)
        {
            return animator.bodyPositionInternal;
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

        public static bool TimelineMatchClipsToPrevious(TimelineClip clip,out string error)
        {
            error=string.Empty;
            try
            {
                UnityEditor.Timeline.AnimationOffsetMenu.MatchClipsToPrevious(new TimelineClip[] { clip });
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return false;
            }
            return true;

        }
    }
}
