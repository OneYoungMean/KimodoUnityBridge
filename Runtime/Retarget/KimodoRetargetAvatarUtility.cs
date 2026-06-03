using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoUnityMotionTools
{
    public static class KimodoRetargetAvatarUtility
    {
        public static string ResolveSkeletonRootBoneName(Avatar avatar)
        {
            if (!KimodoRetargetTools.IsValidHumanoid(avatar))
            {
                return "Hips";
            }

            SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0)
            {
                return "Hips";
            }

            int rootIndex = FindSkeletonRootIndex(skeleton);
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

        public static bool TryBuildBoneNameTable(Transform root, string rootBoneName, out string[] boneNames, out string error)
        {
            error = string.Empty;
            boneNames = null;
            if (root == null)
            {
                error = "Target root is null.";
                return false;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var names = new List<string>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                string path = CalculateTransformPath(all[i], root, rootBoneName);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                names.Add(path);
            }

            boneNames = names.ToArray();
            return true;
        }

        public static Transform[] BuildBoneTransforms(Transform root, string[] bonePaths, string rootBoneName)
        {
            if (bonePaths == null)
            {
                return Array.Empty<Transform>();
            }

            var transforms = new Transform[bonePaths.Length];
            for (int i = 0; i < bonePaths.Length; i++)
            {
                transforms[i] = FindByPath(root, bonePaths[i], rootBoneName);
            }

            return transforms;
        }

        public static Dictionary<string, Transform> BuildPathMap(Transform current, Transform root, string rootBoneName)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (current == null || root == null)
            {
                return map;
            }

            Transform[] all = current.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                string path = CalculateTransformPath(t, root, rootBoneName);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (!map.ContainsKey(path))
                {
                    map.Add(path, t);
                }
            }

            return map;
        }

        public static BoneSample CaptureBoneSample(Transform root, string[] boneNames, string rootBoneName)
        {
            var boneMap = BuildPathMap(root, root, rootBoneName);
            var frame = new BoneSample
            {
                boneNames = boneNames,
                localPositions = new Vector3[boneNames.Length],
                localRotations = new Quaternion[boneNames.Length]
            };

            for (int i = 0; i < boneNames.Length; i++)
            {
                string path = boneNames[i];
                if (!boneMap.TryGetValue(path, out Transform t) || t == null)
                {
                    frame.localPositions[i] = Vector3.zero;
                    frame.localRotations[i] = Quaternion.identity;
                    continue;
                }

                frame.localPositions[i] = t.localPosition;
                frame.localRotations[i] = t.localRotation;
            }

            return frame;
        }

        public static Transform FindByPath(Transform root, string path, string rootBoneName)
        {
            if (root == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (string.Equals(root.name, path, StringComparison.Ordinal) || string.Equals(rootBoneName, path, StringComparison.Ordinal))
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

                if (i == 0 && (string.Equals(current.name, segments[i], StringComparison.Ordinal) || string.Equals(rootBoneName, segments[i], StringComparison.Ordinal)))
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

        public static string CalculateTransformPath(Transform target, Transform root, string rootBoneName)
        {
            if (target == null || root == null)
            {
                return null;
            }

            if (target == root)
            {
                return string.IsNullOrWhiteSpace(rootBoneName) ? target.name : rootBoneName;
            }

            var names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }

            if (current != root)
            {
                return null;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static int FindSkeletonRootIndex(SkeletonBone[] skeleton)
        {
            if (skeleton == null || skeleton.Length == 0)
            {
                return -1;
            }

            for (int i = 0; i < skeleton.Length; i++)
            {
                string parentName = AvatarRuntimeAccess.GetSkeletonBoneParentNameOrEmpty(skeleton[i]);
                if (string.IsNullOrWhiteSpace(parentName))
                {
                    return i;
                }
            }

            return 0;
        }
    }
}
