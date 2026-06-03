using System;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools
{
    internal static class KimodoEditorClipUtility
    {
        public static bool CanApplyClipDirectlyToHierarchy(
            AnimationClip clip,
            GameObject bindingObject,
            out string error)
        {
            error = string.Empty;
            if (clip == null)
            {
                error = "Clip is null.";
                return false;
            }

            if (bindingObject == null)
            {
                error = "Binding object is null.";
                return false;
            }

            if (clip.isHumanMotion)
            {
                error = "Humanoid clip requires avatar.";
                return false;
            }

            Transform bindingRoot = bindingObject.transform;
            if (bindingRoot == null)
            {
                error = "Binding root is null.";
                return false;
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                if (!CanResolveBindingOnHierarchy(bindingRoot, bindings[i].path, bindings[i].type, out error))
                {
                    return false;
                }
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                if (!CanResolveBindingOnHierarchy(bindingRoot, objectBindings[i].path, objectBindings[i].type, out error))
                {
                    return false;
                }
            }

            return true;
        }

        public static void CopyClipData(AnimationClip sourceClip, AnimationClip targetClip, bool forceNoLoopKeepY = false)
        {
            if (sourceClip == null || targetClip == null)
            {
                return;
            }

            targetClip.ClearCurves();
            targetClip.frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : targetClip.frameRate;
            if (forceNoLoopKeepY)
            {
                AnimationUtility.SetAnimationClipSettings(
                    targetClip,
                    new AnimationClipSettings
                    {
                        loopTime = false,
                        keepOriginalPositionY = true
                    });
            }
            else
            {
                AnimationUtility.SetAnimationClipSettings(targetClip, AnimationUtility.GetAnimationClipSettings(sourceClip));
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(sourceClip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                AnimationCurve curve = AnimationUtility.GetEditorCurve(sourceClip, binding);
                if (curve != null)
                {
                    targetClip.SetCurve(binding.path, binding.type, binding.propertyName, curve);
                }
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(sourceClip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding binding = objectBindings[i];
                ObjectReferenceKeyframe[] curve = AnimationUtility.GetObjectReferenceCurve(sourceClip, binding);
                if (curve != null)
                {
                    AnimationUtility.SetObjectReferenceCurve(targetClip, binding, curve);
                }
            }
        }

        private static bool CanResolveBindingOnHierarchy(
            Transform bindingRoot,
            string bindingPath,
            Type bindingType,
            out string error)
        {
            error = string.Empty;

            if (bindingType == typeof(Animator))
            {
                error = $"Binding '{bindingPath}' targets Animator and still requires avatar support.";
                return false;
            }

            if (!TryResolveBindingTransform(bindingRoot, bindingPath, out Transform targetTransform, out bool ambiguous))
            {
                error = ambiguous
                    ? $"Binding path '{bindingPath}' matches multiple transforms under '{bindingRoot.name}'."
                    : $"Binding path '{bindingPath}' was not found under '{bindingRoot.name}'.";
                return false;
            }

            if (bindingType == null || bindingType == typeof(Transform) || bindingType == typeof(GameObject))
            {
                return true;
            }

            if (targetTransform.GetComponent(bindingType) != null)
            {
                return true;
            }

            error = $"Binding '{bindingPath}' requires component '{bindingType.Name}' which is missing on '{targetTransform.name}'.";
            return false;
        }

        private static bool TryResolveBindingTransform(
            Transform bindingRoot,
            string bindingPath,
            out Transform targetTransform,
            out bool ambiguous)
        {
            targetTransform = null;
            ambiguous = false;

            if (bindingRoot == null)
            {
                return false;
            }

            string normalizedPath = NormalizeBindingPath(bindingPath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                targetTransform = bindingRoot;
                return true;
            }

            if (string.Equals(bindingRoot.name, normalizedPath, StringComparison.Ordinal))
            {
                targetTransform = bindingRoot;
                return true;
            }

            Transform direct = bindingRoot.Find(normalizedPath);
            if (direct != null)
            {
                targetTransform = direct;
                return true;
            }

            Transform[] allTransforms = bindingRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allTransforms.Length; i++)
            {
                Transform candidate = allTransforms[i];
                string relativePath = NormalizeBindingPath(AnimationUtility.CalculateTransformPath(candidate, bindingRoot));
                if (!IsCompatibleBindingPath(relativePath, normalizedPath))
                {
                    continue;
                }

                if (targetTransform != null && targetTransform != candidate)
                {
                    targetTransform = null;
                    ambiguous = true;
                    return false;
                }

                targetTransform = candidate;
            }

            return targetTransform != null;
        }

        private static bool IsCompatibleBindingPath(string hierarchyPath, string bindingPath)
        {
            if (string.Equals(hierarchyPath, bindingPath, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(hierarchyPath) &&
                hierarchyPath.EndsWith("/" + bindingPath, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(bindingPath) &&
                bindingPath.EndsWith("/" + hierarchyPath, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static string NormalizeBindingPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }
    }
}
