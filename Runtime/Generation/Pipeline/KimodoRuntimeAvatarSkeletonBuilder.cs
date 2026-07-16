using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    public static class KimodoRuntimeAvatarSkeletonBuilder
    {
        public static string ResolveAvatarResourceName(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return "SOMAAvatar";
            }

            string normalized = modelName.Trim().ToLowerInvariant();
            if (normalized.Contains("smplx"))
            {
                return "SMPLXAvatar";
            }

            if (normalized.Contains("g1"))
            {
                return "G1Avatar";
            }

            if (normalized.Contains("ardy-core") || normalized.Contains("cskel27"))
            {
                return "SOMA_cskel27Avatar";
            }

            return "SOMAAvatar";
        }

        public static bool TryLoadAvatarByModelName(string modelName, out Avatar avatar, out string error)
        {
            error = string.Empty;
            avatar = null;

            string avatarResourceName = ResolveAvatarResourceName(modelName);
            Avatar loaded = Resources.Load<Avatar>(avatarResourceName);
            if (loaded == null || !loaded.isValid || !loaded.isHuman)
            {
                error = $"Runtime Resources avatar '{avatarResourceName}' not found or invalid humanoid avatar.";
                return false;
            }

            avatar = loaded;
            return true;
        }

        public static bool TryBuildHierarchyFromAvatarSkeleton(Avatar avatar, Transform root, out string error)
        {
            error = string.Empty;
            if (avatar == null || root == null)
            {
                error = "Avatar or root is null while building sampling hierarchy.";
                return false;
            }

            SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0)
            {
                error = "Avatar humanDescription.skeleton is empty.";
                return false;
            }

            int rootBoneIndex = FindRootBoneIndex(skeleton);
            if (rootBoneIndex < 0 || rootBoneIndex >= skeleton.Length)
            {
                error = "Avatar skeleton root bone could not be resolved.";
                return false;
            }

            SkeletonBone rootBone = skeleton[rootBoneIndex];
            string rootBoneName = string.IsNullOrWhiteSpace(rootBone.name) ? $"Bone_{rootBoneIndex}" : rootBone.name;
            string originalRootName = string.IsNullOrWhiteSpace(root.name) ? "SkeletonRoot" : root.name;
            root.name = $"{originalRootName}_{rootBoneName}";
            root.localPosition = rootBone.position;
            root.localRotation = rootBone.rotation;
            root.localScale = rootBone.scale;

            var nodes = new List<SkeletonBuildNode>(Mathf.Max(0, skeleton.Length - 1));
            var firstByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            firstByName[rootBoneName] = root;

            for (int i = 0; i < skeleton.Length; i++)
            {
                if (i == rootBoneIndex)
                {
                    continue;
                }

                SkeletonBone bone = skeleton[i];
                string name = string.IsNullOrWhiteSpace(bone.name) ? $"Bone_{i}" : bone.name;
                string parentName = AvatarRuntimeAccess.GetSkeletonBoneParentNameOrEmpty(bone);

                var go = new GameObject(name);
                go.hideFlags = HideFlags.HideAndDontSave;

                Transform t = go.transform;
                nodes.Add(new SkeletonBuildNode
                {
                    ParentName = parentName,
                    LocalPosition = bone.position,
                    LocalRotation = bone.rotation,
                    LocalScale = bone.scale,
                    Transform = t
                });

                if (!firstByName.ContainsKey(name))
                {
                    firstByName[name] = t;
                }
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                SkeletonBuildNode node = nodes[i];
                Transform parent = root;
                if (!string.IsNullOrWhiteSpace(node.ParentName) &&
                    firstByName.TryGetValue(node.ParentName, out Transform resolvedParent) &&
                    resolvedParent != null)
                {
                    parent = resolvedParent;
                }

                node.Transform.SetParent(parent, false);
                node.Transform.localPosition = node.LocalPosition;
                node.Transform.localRotation = node.LocalRotation;
                node.Transform.localScale = node.LocalScale;
            }

            return true;
        }

        private static int FindRootBoneIndex(SkeletonBone[] skeleton)
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

        private sealed class SkeletonBuildNode
        {
            public string ParentName;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public Transform Transform;
        }
    }
}
