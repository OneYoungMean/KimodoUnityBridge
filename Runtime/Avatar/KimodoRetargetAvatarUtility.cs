using System;
using System.Collections.Generic;
using System.Linq;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    public static class KimodoRetargetAvatarUtility
    {
        internal static bool TryCreateVirtualSkeleton(
            Avatar avatar,
            string rootName,
            bool animatorEnabled,
            bool applyRootMotion,
            out GameObject root,
            out Animator animator,
            out string error)
        {
            root = null;
            animator = null;
            error = string.Empty;

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            root = new GameObject(string.IsNullOrWhiteSpace(rootName) ? "KimodoTemporaryHumanoidRoot" : rootName);
            root.hideFlags = HideFlags.HideAndDontSave;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, root.transform, out error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                root = null;
                return false;
            }

            KimodoRetargetClipSamplingUtility.SetHierarchyHideFlags(root.transform, HideFlags.HideAndDontSave);

            animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }

            animator.avatar = avatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = applyRootMotion;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = true;
            animator.Rebind();
            animator.Update(0f);
            animator.enabled = animatorEnabled;
            return true;
        }

        internal static bool TryBuildRetargetSkeleton(
            Avatar avatar,
            string rootName,
            out RetargetSkeleton cache,
            out string error)
        {
            cache = null;
            error = string.Empty;

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!TryCreateVirtualSkeleton(
                    avatar,
                    string.IsNullOrWhiteSpace(rootName) ? "KimodoRetargetSkeleton" : rootName,
                    animatorEnabled: true,
                    applyRootMotion: true,
                    out GameObject root,
                    out Animator animator,
                    out error))
            {
                return false;
            }

            return TryBuildOwnedRetargetSkeleton(avatar, root, animator, out cache, out error);
        }

        internal static bool TryBuildSkeletonInstance(
            Avatar avatar,
            string rootName,
            out KimodoSkeletonInstance skeleton,
            out string error)
        {
            skeleton = null;
            if (!TryBuildRetargetSkeleton(avatar, rootName, out RetargetSkeleton cache, out error))
            {
                return false;
            }

            skeleton = new KimodoSkeletonInstance(cache);
            return true;
        }

        internal static bool TryBuildOwnedRetargetSkeleton(
            GameObject root,
            Animator animator,
            out RetargetSkeleton cache,
            out string error)
        {
            cache = null;
            error = string.Empty;
            if (root == null || animator == null || animator.gameObject != root)
            {
                error = "Owned humanoid root or Animator is invalid.";
                return false;
            }

            Avatar avatar = animator.avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            return TryBuildOwnedRetargetSkeleton(avatar, root, animator, out cache, out error);
        }

        internal static bool TryBuildOwnedSkeletonInstance(
            GameObject root,
            Animator animator,
            out KimodoSkeletonInstance skeleton,
            out string error)
        {
            skeleton = null;
            if (!TryBuildOwnedRetargetSkeleton(root, animator, out RetargetSkeleton cache, out error))
            {
                return false;
            }

            skeleton = new KimodoSkeletonInstance(cache);
            return true;
        }

        private static bool TryBuildOwnedRetargetSkeleton(
            Avatar avatar,
            GameObject root,
            Animator animator,
            out RetargetSkeleton cache,
            out string error)
        {
            cache = null;
            error = string.Empty;

            string canonicalRootBoneName = ResolveSkeletonRootBoneName(avatar);
            if (!TryBuildTransformCaches(
                    root.transform,
                    canonicalRootBoneName,
                    out string[] bonePaths,
                    out Transform[] boneTransforms,
                    out Dictionary<string, Transform> bonePathMap,
                    out Dictionary<string, Transform> uniqueNameMap,
                    out HashSet<string> ambiguousNames,
                    out error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            if (bonePaths == null || bonePaths.Length == 0)
            {
                error = "Skeleton cache bone table is empty.";
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            if (!TryResolveHumanScale(avatar, root, out float humanScale, out error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            cache = new RetargetSkeleton
            {
                avatar = avatar,
                humanScale = humanScale,
                root = root,
                skeletonRoot = root.transform,
                rootLocalPosition = root.transform.localPosition,
                rootLocalRotation = root.transform.localRotation,
                rootLocalScale = root.transform.localScale,
                canonicalRootBoneName = canonicalRootBoneName,
                animator = animator,
                poseHandler = new HumanPoseHandler(avatar, root.transform),
                bonePaths = bonePaths,
                boneTransforms = boneTransforms,
                bonePathMap = bonePathMap,
                uniqueNameMap = uniqueNameMap,
                ambiguousNames = ambiguousNames,
                humanBoneTransforms = BuildHumanBoneTransformMap(avatar, animator, uniqueNameMap),
                boneCount = bonePaths.Length
            };

            KimodoRetargetClipSamplingUtility.CaptureSkeletonBindPose(cache);
            return true;
        }

        private static bool TryResolveHumanScale(
            Avatar avatar,
            GameObject root,
            out float humanScale,
            out string error)
        {
            humanScale = 1f;
            error = string.Empty;
            GameObject probe = null;
            try
            {
                probe = UnityEngine.Object.Instantiate(root);
                probe.name = "__KimodoHumanScaleProbe";
                probe.hideFlags = HideFlags.HideAndDontSave;
                probe.SetActive(true);
                Animator scaleAnimator = probe.GetComponent<Animator>() ?? probe.AddComponent<Animator>();
                scaleAnimator.avatar = avatar;
                scaleAnimator.runtimeAnimatorController = null;
                scaleAnimator.enabled = true;
                scaleAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                scaleAnimator.Rebind();
                scaleAnimator.Update(0f);

                humanScale = scaleAnimator.humanScale;
                if (float.IsNaN(humanScale) || float.IsInfinity(humanScale) || humanScale <= 1e-6f)
                {
                    error = "Humanoid scale probe returned an invalid value.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"Resolve humanoid scale failed: {ex.Message}";
                return false;
            }
            finally
            {
                if (probe != null)
                {
                    UnityEngine.Object.DestroyImmediate(probe);
                }
            }
        }

        internal static bool ValidateRetargetSkeleton(RetargetSkeleton cache, out string error)
        {
            error = string.Empty;

            if (cache == null)
            {
                error = "Skeleton cache is null.";
                return false;
            }

            if (!cache.IsReady)
            {
                error = "Skeleton cache is not initialized.";
                return false;
            }

            if (cache.avatar == null)
            {
                error = "Skeleton cache avatar is null.";
                return false;
            }

            if (cache.bonePaths == null || cache.boneTransforms == null)
            {
                error = "Skeleton cache bone mapping is missing.";
                return false;
            }

            if (cache.bonePaths.Length == 0 || cache.bonePaths.Length != cache.boneTransforms.Length)
            {
                error = "Skeleton cache bone mapping is invalid.";
                return false;
            }

            return true;
        }

        public static string ResolveSkeletonRootBoneName(Avatar avatar)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                return "Hips";
            }

            SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0)
            {
                return "Hips";
            }

            int rootIndex = KimodoRuntimeAvatarSkeletonBuilder.ResolveRootBoneIndex(skeleton);
            if (rootIndex >= 0 && rootIndex < skeleton.Length)
            {
                string name = skeleton[rootIndex].name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }

            return "Hips";
        }

        private static bool TryBuildTransformCaches(
            Transform root,
            string canonicalRootBoneName,
            out string[] bonePaths,
            out Transform[] boneTransforms,
            out Dictionary<string, Transform> bonePathMap,
            out Dictionary<string, Transform> uniqueNameMap,
            out HashSet<string> ambiguousNames,
            out string error)
        {
            error = string.Empty;
            bonePaths = Array.Empty<string>();
            boneTransforms = Array.Empty<Transform>();
            bonePathMap = new Dictionary<string, Transform>(StringComparer.Ordinal);
            uniqueNameMap = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null)
            {
                error = "Target root is null.";
                return false;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var allDic= all.ToDictionary( x=>x.transform, x => x.name);
            var paths = new List<string>(all.Length);
            var transforms = new List<Transform>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                Transform current = all[i];
                string path = CalculateTransformPath(current, root, canonicalRootBoneName, allDic);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                paths.Add(path);
                transforms.Add(current);
                bonePathMap[path] = current;

                string name = current.name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (ambiguousNames.Contains(name))
                {
                    continue;
                }

                if (uniqueNameMap.TryGetValue(name, out Transform existing) && existing != current)
                {
                    uniqueNameMap.Remove(name);
                    ambiguousNames.Add(name);
                    continue;
                }

                uniqueNameMap[name] = current;
            }

            bonePaths = paths.ToArray();
            boneTransforms = transforms.ToArray();
            return true;
        }

        public static Transform[] BuildBoneTransforms(Transform root, string[] bonePaths, string canonicalRootBoneName)
        {
            if (bonePaths == null)
            {
                return Array.Empty<Transform>();
            }

            var transforms = new Transform[bonePaths.Length];
            for (int i = 0; i < bonePaths.Length; i++)
            {
                transforms[i] = FindByPath(root, bonePaths[i], canonicalRootBoneName);
            }

            return transforms;
        }


        public static Transform FindByPath(Transform root, string path, string canonicalRootBoneName)
        {
            if (root == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (string.Equals(root.name, path, StringComparison.Ordinal) || string.Equals(canonicalRootBoneName, path, StringComparison.Ordinal))
            {
                return root;
            }

            string[] segments = path.Split('/');
            Transform current = root;
            for (int i = 0; i < segments.Length; i++)
            {
                if (current == null)
                {
                    return null;
                }

                if (i == 0 && (string.Equals(current.name, segments[i], StringComparison.Ordinal) || string.Equals(canonicalRootBoneName, segments[i], StringComparison.Ordinal)))
                {
                    continue;
                }

                current = current.Find(segments[i]);
            }

            return current;
        }

        public static Transform FindTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    stack.Push(current.GetChild(i));
                }
            }

            return null;
        }

        internal static bool TryGetUniqueCachedTransformByName(
            RetargetSkeleton cache,
            string name,
            out Transform result,
            out bool ambiguous)
        {
            result = null;
            ambiguous = false;
            if (cache?.uniqueNameMap == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (cache.ambiguousNames != null && cache.ambiguousNames.Contains(name))
            {
                ambiguous = true;
                return false;
            }

            return cache.uniqueNameMap.TryGetValue(name, out result) && result != null;
        }

        public static bool TryFindUniqueTransformByName(
            Transform root,
            string name,
            out Transform result,
            out bool ambiguous)
        {
            result = null;
            ambiguous = false;
            if (root == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform candidate = all[i];
                if (candidate == null || !string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (result != null && result != candidate)
                {
                    result = null;
                    ambiguous = true;
                    return false;
                }

                result = candidate;
            }

            return result != null;
        }

        private static Dictionary<HumanBodyBones, Transform> BuildHumanBoneTransformMap(
            Avatar avatar,
            Animator animator,
            Dictionary<string, Transform> uniqueNameMap)
        {
            var map = new Dictionary<HumanBodyBones, Transform>();
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                return map;
            }

            Array values = Enum.GetValues(typeof(HumanBodyBones));
            for (int i = 0; i < values.Length; i++)
            {
                HumanBodyBones bone = (HumanBodyBones)values.GetValue(i);
                if (bone == HumanBodyBones.LastBone)
                {
                    continue;
                }

                Transform transform = animator != null && animator.avatar != null
                    ? animator.GetBoneTransform(bone)
                    : null;
                if (transform != null)
                {
                    map[bone] = transform;
                }
            }

            HumanBone[] humanBones = avatar.humanDescription.human;
            for (int i = 0; i < humanBones.Length; i++)
            {
                HumanBone humanBone = humanBones[i];
                if (!Enum.TryParse(humanBone.humanName, out HumanBodyBones bone) || bone == HumanBodyBones.LastBone)
                {
                    continue;
                }

                if (map.ContainsKey(bone))
                {
                    continue;
                }

                if (uniqueNameMap != null &&
                    !string.IsNullOrWhiteSpace(humanBone.boneName) &&
                    uniqueNameMap.TryGetValue(humanBone.boneName, out Transform transform) &&
                    transform != null)
                {
                    map[bone] = transform;
                }
            }

            return map;
        }

        public static string CalculateTransformPath(Transform target, Transform root, string canonicalRootBoneName, Dictionary<Transform,string> allDic)
        {
            if (target == null || root == null)
            {
                return null;
            }

            if (target == root)
            {
                return string.IsNullOrWhiteSpace(canonicalRootBoneName) ? target.name : canonicalRootBoneName;
            }

            var names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(allDic[current]);
                current = current.parent;
            }

            if (current != root)
            {
                return null;
            }

            names.Reverse();
            return string.Join("/", names);
        }

    }
}
