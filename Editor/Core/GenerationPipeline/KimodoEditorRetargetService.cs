using UnityEditor;
using UnityEngine;
using KimodoUnityMotionTools.ProjectEditor;
namespace KimodoUnityMotionTools.ProjectEditor.GenerationPipeline
{
    internal sealed class KimodoEditorRetargetService
    {
        public bool TryRetarget(
            AnimationClip targetClip,
            Avatar originAvatar,
            Avatar targetAvatar,
            out string details)
        {
            details = string.Empty;

            if (targetClip == null)
            {
                details = "Target clip is null.";
                return false;
            }

            if (originAvatar == null || !originAvatar.isValid || !originAvatar.isHuman)
            {
                details = "Origin avatar is null/invalid/non-humanoid.";
                Debug.Log($"[Kimodo] {details}");
                return false;
            }

            if (targetAvatar == null || !targetAvatar.isValid || !targetAvatar.isHuman)
            {
                details = "Target avatar is null/invalid/non-humanoid.";
                Debug.Log($"[Kimodo] {details}");
                return false;
            }

            bool retargetOk = KimodoRetargetPipeline.TryRetargetClip(
                targetClip,
                originAvatar,
                targetAvatar,
                out AnimationClip retargetedClip,
                out KimodoRetargetResultMode retargetMode,
                out string retargetDetails);

            if (retargetOk)
            {
                if (retargetedClip != null && retargetedClip != targetClip)
                {
                    CopyClipCurves(retargetedClip, targetClip);
                }
                details = $"Retarget success ({retargetMode}). {retargetDetails}";
                Debug.Log($"[Kimodo] {details}");
            }
            else
            {
                details = $"Retarget failed. {retargetDetails}";
                Debug.Log($"[Kimodo] {details}");
                return false;
            }

            return true;
        }

        private static void CopyClipCurves(AnimationClip source, AnimationClip destination)
        {
            destination.ClearCurves();
            destination.frameRate = source.frameRate;
            destination.legacy = source.legacy;

            EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(source);
            for (int i = 0; i < floatBindings.Length; i++)
            {
                EditorCurveBinding binding = floatBindings[i];
                AnimationCurve curve = AnimationUtility.GetEditorCurve(source, binding);
                AnimationUtility.SetEditorCurve(destination, binding, curve);
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(source);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding binding = objectBindings[i];
                ObjectReferenceKeyframe[] keyframes = AnimationUtility.GetObjectReferenceCurve(source, binding);
                AnimationUtility.SetObjectReferenceCurve(destination, binding, keyframes);
            }

            destination.EnsureQuaternionContinuity();
        }
    }
}

