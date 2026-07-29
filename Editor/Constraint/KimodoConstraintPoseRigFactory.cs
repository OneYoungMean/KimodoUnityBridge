using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintPoseRigFactory
    {
        internal sealed class PoseRigInstance
        {
            public GameObject Root;
            public SkeletonCache TargetCache;
            public SkeletonCache ProfileCache;
            public List<Material> GeneratedMaterials;
        }

        internal static bool TryCreatePoseRig(
            string modelName,
            int clipId,
            int animatorId,
            out PoseRigInstance instance,
            out string error)
        {
            instance = null;
            error = string.Empty;
            Animator sourceAnimator = EditorUtility.InstanceIDToObject(animatorId) as Animator;
            if (sourceAnimator == null || sourceAnimator.gameObject == null)
            {
                error = "Timeline binding Animator is missing.";
                return false;
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAnimator.avatar))
            {
                error = "Timeline binding Avatar is null/invalid/non-humanoid.";
                return false;
            }

            SkeletonCache targetCache = null;
            SkeletonCache profileCache = null;
            List<Material> generatedMaterials = null;
            try
            {
                if (!TryCreateVisualClone(
                        sourceAnimator,
                        clipId,
                        animatorId,
                        out GameObject targetRoot,
                        out Animator targetAnimator,
                        out generatedMaterials,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetAvatarUtility.TryBuildOwnedSkeletonCache(
                        targetRoot,
                        targetAnimator,
                        out targetCache,
                        out error))
                {
                    return false;
                }

                if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        modelName,
                        out Avatar profileAvatar,
                        out error) ||
                    !KimodoRetargetCoreUtility.IsValidHumanoid(profileAvatar))
                {
                    error = string.IsNullOrWhiteSpace(error)
                        ? $"Profile Avatar is unavailable for '{modelName}'."
                        : error;
                    return false;
                }

                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        profileAvatar,
                        $"__KimodoConstraintProfile_{clipId}_{animatorId}",
                        out profileCache,
                        out error))
                {
                    return false;
                }

                targetCache.root.name = $"__KimodoConstraintAvatar_{clipId}_{animatorId}";
                targetCache.root.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
                profileCache.root.hideFlags = HideFlags.HideAndDontSave;
                instance = new PoseRigInstance
                {
                    Root = targetCache.root,
                    TargetCache = targetCache,
                    ProfileCache = profileCache,
                    GeneratedMaterials = generatedMaterials
                };
                targetCache = null;
                profileCache = null;
                generatedMaterials = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                targetCache?.Dispose();
                profileCache?.Dispose();
                DestroyMaterials(generatedMaterials);
            }
        }

        private static bool TryCreateVisualClone(
            Animator sourceAnimator,
            int clipId,
            int animatorId,
            out GameObject root,
            out Animator animator,
            out List<Material> generatedMaterials,
            out string error)
        {
            root = null;
            animator = null;
            generatedMaterials = new List<Material>();
            error = string.Empty;
            var transformMap = new Dictionary<Transform, Transform>();
            try
            {
                root = CloneTransformHierarchy(sourceAnimator.transform, null, transformMap).gameObject;
                root.name = $"__KimodoConstraintAvatar_{clipId}_{animatorId}";
                root.transform.SetPositionAndRotation(sourceAnimator.transform.position, sourceAnimator.transform.rotation);
                root.transform.localScale = sourceAnimator.transform.lossyScale;

                Material previewMaterial = CreatePreviewMaterial();
                if (previewMaterial != null)
                {
                    generatedMaterials.Add(previewMaterial);
                }
                CopySkinnedMeshes(sourceAnimator.transform, transformMap, previewMaterial, out error);
                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Object.DestroyImmediate(root);
                    root = null;
                    return false;
                }

                animator = root.AddComponent<Animator>();
                animator.avatar = sourceAnimator.avatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.enabled = true;
                animator.Rebind();
                animator.Update(0f);
                root.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                    root = null;
                }
                return false;
            }
        }

        private static Transform CloneTransformHierarchy(
            Transform source,
            Transform parent,
            Dictionary<Transform, Transform> transformMap)
        {
            var cloneObject = new GameObject(source.name);
            Transform clone = cloneObject.transform;
            if (parent != null)
            {
                clone.SetParent(parent, false);
                clone.localPosition = source.localPosition;
                clone.localRotation = source.localRotation;
                clone.localScale = source.localScale;
            }
            transformMap[source] = clone;
            cloneObject.SetActive(source.gameObject.activeSelf);
            for (int i = 0; i < source.childCount; i++)
            {
                CloneTransformHierarchy(source.GetChild(i), clone, transformMap);
            }
            return clone;
        }

        private static void CopySkinnedMeshes(
            Transform sourceRoot,
            Dictionary<Transform, Transform> transformMap,
            Material previewMaterial,
            out string error)
        {
            error = string.Empty;
            SkinnedMeshRenderer[] sourceRenderers = sourceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < sourceRenderers.Length; i++)
            {
                SkinnedMeshRenderer source = sourceRenderers[i];
                if (source == null || !transformMap.TryGetValue(source.transform, out Transform targetTransform))
                {
                    continue;
                }

                var target = targetTransform.gameObject.AddComponent<SkinnedMeshRenderer>();
                target.sharedMesh = source.sharedMesh;
                target.rootBone = ResolveCloneTransform(source.rootBone, transformMap);
                Transform[] sourceBones = source.bones;
                var targetBones = new Transform[sourceBones.Length];
                for (int boneIndex = 0; boneIndex < sourceBones.Length; boneIndex++)
                {
                    targetBones[boneIndex] = ResolveCloneTransform(sourceBones[boneIndex], transformMap);
                    if (sourceBones[boneIndex] != null && targetBones[boneIndex] == null)
                    {
                        error = $"Skinned mesh bone '{sourceBones[boneIndex].name}' is outside the bound Animator hierarchy.";
                        return;
                    }
                }
                target.bones = targetBones;
                target.localBounds = source.localBounds;
                target.quality = source.quality;
                target.updateWhenOffscreen = true;
                target.skinnedMotionVectors = false;
                target.shadowCastingMode = source.shadowCastingMode;
                target.receiveShadows = source.receiveShadows;
                target.lightProbeUsage = source.lightProbeUsage;
                target.reflectionProbeUsage = source.reflectionProbeUsage;
                target.probeAnchor = ResolveCloneTransform(source.probeAnchor, transformMap);
                target.enabled = source.enabled;

                Material[] sourceMaterials = source.sharedMaterials;
                var materials = new Material[sourceMaterials != null ? sourceMaterials.Length : 0];
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    materials[materialIndex] = previewMaterial != null
                        ? previewMaterial
                        : sourceMaterials[materialIndex];
                }
                target.sharedMaterials = materials;

                Mesh mesh = source.sharedMesh;
                if (mesh != null)
                {
                    for (int blendShapeIndex = 0; blendShapeIndex < mesh.blendShapeCount; blendShapeIndex++)
                    {
                        target.SetBlendShapeWeight(blendShapeIndex, source.GetBlendShapeWeight(blendShapeIndex));
                    }
                }
            }
        }

        private static Transform ResolveCloneTransform(
            Transform source,
            Dictionary<Transform, Transform> transformMap)
        {
            return source != null && transformMap.TryGetValue(source, out Transform clone)
                ? clone
                : null;
        }

        private static Material CreatePreviewMaterial()
        {
            Shader shader = Shader.Find("HDRP/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "__KimodoConstraintAvatarPreview"
            };
            SetMaterialColor(material, Color.white);
            return material;
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 0f);
            }
            if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 0f);
            }
            if (material.HasProperty("_SrcBlend"))
            {
                material.SetInt("_SrcBlend", (int)BlendMode.One);
            }
            if (material.HasProperty("_DstBlend"))
            {
                material.SetInt("_DstBlend", (int)BlendMode.Zero);
            }
            if (material.HasProperty("_ZWrite"))
            {
                material.SetInt("_ZWrite", 1);
            }
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = (int)RenderQueue.Geometry;
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHABLEND_ON");
        }

        private static void DestroyMaterials(List<Material> materials)
        {
            if (materials == null)
            {
                return;
            }
            for (int i = 0; i < materials.Count; i++)
            {
                if (materials[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(materials[i]);
                }
            }
        }
    }
}
