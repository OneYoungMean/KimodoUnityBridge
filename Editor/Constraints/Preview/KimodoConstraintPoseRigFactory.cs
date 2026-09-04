using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintPoseRigFactory
    {
        internal sealed class PoseRigInstance
        {
            public GameObject Root;
            public RetargetSkeleton TargetCache;
            // Preview-only material instances; never reuse source/shared materials.
            public List<Material> GeneratedMaterials = new List<Material>();
        }

        internal static bool TryCreatePoseRig(
            string modelName,
            int clipId,
            int animatorId,
            Avatar sourceAvatar,
            out PoseRigInstance instance,
            out string error)
        {
            instance = null;
            error = string.Empty;
            Animator sourceAnimator = KimodoEditorObjectIdUtility.ObjectFromId(animatorId) as Animator;
            if (sourceAnimator == null || sourceAnimator.gameObject == null)
            {
                error = "Timeline binding Animator is missing.";
                return false;
            }
            if (!TryCreatePoseRig(modelName, sourceAnimator, sourceAvatar, out instance, out error))
            {
                return false;
            }

            instance.Root.name = $"__KimodoConstraintAvatar_{clipId}_{animatorId}";
            return true;
        }

        internal static bool TryCreatePoseRig(
            string modelName,
            Animator sourceAnimator,
            Avatar sourceAvatar,
            out PoseRigInstance instance,
            out string error)
        {
            instance = null;
            error = string.Empty;
            if (sourceAnimator == null)
            {
                error = "Timeline binding Animator is missing.";
                return false;
            }

            Avatar resolvedSourceAvatar = KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar)
                ? sourceAvatar
                : sourceAnimator.avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(resolvedSourceAvatar))
            {
                error = "Timeline binding Avatar is null/invalid/non-humanoid.";
                return false;
            }

            RetargetSkeleton targetCache = null;
            List<Material> generatedMaterials = null;
            try
            {
                if (!TryCreateVisualClone(
                        sourceAnimator,
                        resolvedSourceAvatar,
                        0,
                        sourceAnimator.GetInstanceID(),
                        out GameObject targetRoot,
                        out Animator targetAnimator,
                        out generatedMaterials,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetAvatarUtility.TryBuildOwnedRetargetSkeleton(
                        targetRoot,
                        targetAnimator,
                        out targetCache,
                        out error))
                {
                    return false;
                }
                targetAnimator.enabled = false;
                targetCache.root.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                instance = new PoseRigInstance
                {
                    Root = targetCache.root,
                    TargetCache = targetCache,
                    GeneratedMaterials = generatedMaterials
                };
                targetCache = null;
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
                DestroyMaterials(generatedMaterials);
            }
        }

        internal static bool TryApplyPose(
            PoseRigInstance instance,
            KimodoMarkerSampleResult sample,
            string modelName,
            out string error)
        {
            error = string.Empty;
            if (sample == null || instance?.TargetCache == null)
            {
                error = "Constraint target skeleton is unavailable.";
                return false;
            }

            try
            {
                ApplySampleToPreviewRig(instance.TargetCache, sample);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void ApplySampleToPreviewRig(RetargetSkeleton cache, KimodoMarkerSampleResult sample)
        {
            if (cache?.animator == null || cache.avatar == null ||
                sample?.sampleData == null || !sample.sampleData.IsValid)
            {
                throw new InvalidOperationException("Preview pose input is invalid.");
            }

            using (var handler = new HumanPoseHandler(cache.avatar, cache.animator.transform))
            {
                HumanPose pose = KimodoMuscleSampleHumanPoseAdapter.ToHumanPose(sample.sampleData);
                handler.SetHumanPose(ref pose);
            }

            if (sample.validMask?.rootPosition != true || sample.rootOverride == null)
            {
                return;
            }

            Transform hips = cache.animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null)
            {
                cache.animator.transform.position = KimodoMotionMath.ApplyPlanarPosition(
                    cache.animator.transform.position,
                    sample.rootOverride.t);
                cache.animator.transform.rotation = ResolvePreviewHipsRotation(
                    sample,
                    cache.animator.transform.rotation);
                return;
            }

            Pose rootPose = new Pose(cache.animator.transform.position, cache.animator.transform.rotation);
            Pose hipsPose = new Pose(hips.position, hips.rotation);
            Quaternion relativeRotation = Quaternion.Inverse(rootPose.rotation) * hipsPose.rotation;
            Vector3 relativePosition = Quaternion.Inverse(rootPose.rotation) * (hipsPose.position - rootPose.position);
            Quaternion desiredHipsRotation = ResolvePreviewHipsRotation(sample, hipsPose.rotation);
            Quaternion newRootRotation = desiredHipsRotation * Quaternion.Inverse(relativeRotation);
            Vector3 newRootPosition = sample.rootOverride.t - newRootRotation * relativePosition;
            cache.animator.transform.SetPositionAndRotation(newRootPosition, newRootRotation);
        }

        // FullBody samples capture a complete Hips world rotation. Root2D and
        // Mix samples intentionally supply only a planar heading.
        internal static Quaternion ResolvePreviewHipsRotation(
            KimodoMarkerSampleResult sample,
            Quaternion evaluatedHipsRotation)
        {
            if (sample?.validMask?.rootHeading != true || sample.rootOverride == null)
            {
                return evaluatedHipsRotation;
            }

            string mode = KimodoConstraintInternal.NormalizeMode(sample.constraintMode);
            return mode == "root2d" || mode == "mix"
                ? KimodoMotionMath.ApplyPlanarHeading(evaluatedHipsRotation, sample.rootOverride.q)
                : sample.rootOverride.q.normalized;
        }

        internal static void DisposePoseRig(PoseRigInstance instance)
        {
            if (instance == null) return;
            RetargetSkeleton targetSkeleton = instance.TargetCache;
            instance.TargetCache = null;
            targetSkeleton?.Dispose();
            if (targetSkeleton == null && instance.Root != null)
            {
                UnityEngine.Object.DestroyImmediate(instance.Root);
            }
            instance.Root = null;
            DestroyMaterials(instance.GeneratedMaterials);
        }

        private static bool TryCreateVisualClone(
            Animator sourceAnimator,
            Avatar sourceAvatar,
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
                // Preview retargeting receives muscle-space absolute rootTQ.
                // Keep the preview skeleton root neutral so scene placement or
                // lossyScale cannot add a second root transform to that value.
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                root.transform.localScale = Vector3.one;

                CopyMeshes(sourceAnimator.transform, transformMap, generatedMaterials, out error);
                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Object.DestroyImmediate(root);
                    root = null;
                    return false;
                }

                animator = root.AddComponent<Animator>();
                animator.avatar = sourceAvatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.enabled = true;
                animator.Rebind();
                animator.Update(0f);
                root.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
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

        private static void CopyMeshes(
            Transform sourceRoot,
            Dictionary<Transform, Transform> transformMap,
            List<Material> generatedMaterials,
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
                EditorUtility.CopySerialized(source, target);
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
                target.updateWhenOffscreen = true;
                target.skinnedMotionVectors = false;
                target.probeAnchor = ResolveCloneTransform(source.probeAnchor, transformMap);
                target.sharedMaterials = CloneMaterials(source.sharedMaterials, generatedMaterials);

                Mesh mesh = source.sharedMesh;
                if (mesh != null)
                {
                    for (int blendShapeIndex = 0; blendShapeIndex < mesh.blendShapeCount; blendShapeIndex++)
                    {
                        target.SetBlendShapeWeight(blendShapeIndex, source.GetBlendShapeWeight(blendShapeIndex));
                    }
                }
            }

            MeshFilter[] staticFilters = sourceRoot.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < staticFilters.Length; i++)
            {
                MeshFilter sourceFilter = staticFilters[i];
                MeshRenderer source = sourceFilter != null ? sourceFilter.GetComponent<MeshRenderer>() : null;
                if (source == null || sourceFilter.sharedMesh == null ||
                    !transformMap.TryGetValue(source.transform, out Transform targetTransform))
                {
                    continue;
                }

                MeshFilter targetFilter = targetTransform.gameObject.GetComponent<MeshFilter>();
                if (targetFilter == null)
                {
                    targetFilter = targetTransform.gameObject.AddComponent<MeshFilter>();
                }
                EditorUtility.CopySerialized(sourceFilter, targetFilter);
                MeshRenderer target = targetTransform.gameObject.GetComponent<MeshRenderer>();
                if (target == null)
                {
                    target = targetTransform.gameObject.AddComponent<MeshRenderer>();
                }
                EditorUtility.CopySerialized(source, target);
                target.probeAnchor = ResolveCloneTransform(source.probeAnchor, transformMap);
                target.sharedMaterials = CloneMaterials(source.sharedMaterials, generatedMaterials);
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

        private static Material[] CloneMaterials(Material[] sourceMaterials, List<Material> generatedMaterials)
        {
            if (sourceMaterials == null)
            {
                return Array.Empty<Material>();
            }

            var result = new Material[sourceMaterials.Length];
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                Material source = sourceMaterials[i];
                if (source == null)
                {
                    continue;
                }

                Material clone = new Material(source)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    name = source.name + " (Kimodo Preview)"
                };
                result[i] = clone;
                generatedMaterials?.Add(clone);
            }
            return result;
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
            materials.Clear();
        }

    }
}
