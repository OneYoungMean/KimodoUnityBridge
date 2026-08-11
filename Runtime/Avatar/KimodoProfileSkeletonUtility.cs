using System;
using UnityEngine;

namespace KimodoBridge
{
    public static class KimodoProfileSkeletonUtility
    {
        public static bool TryResolveProfileSkeleton(
            string modelName,
            SkeletonCache cache,
            out string[] jointNames,
            out int[] parentIndices,
            out Transform[] jointTransforms,
            out string error)
        {
            jointTransforms = Array.Empty<Transform>();
            if (!TryResolveProfileLayout(modelName, out jointNames, out parentIndices, out error))
            {
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out error))
            {
                return false;
            }

            return TryResolveJointTransforms(
                jointNames,
                cache.skeletonRoot,
                (string jointName, out Transform jointTransform, out bool ambiguous) =>
                    KimodoRetargetAvatarUtility.TryGetUniqueCachedTransformByName(
                        cache,
                        jointName,
                        out jointTransform,
                        out ambiguous),
                out jointTransforms,
                out error);
        }

        public static bool TryResolveProfileSkeleton(
            string modelName,
            Transform root,
            out string[] jointNames,
            out int[] parentIndices,
            out Transform[] jointTransforms,
            out string error)
        {
            jointTransforms = Array.Empty<Transform>();
            if (!TryResolveProfileLayout(modelName, out jointNames, out parentIndices, out error))
            {
                return false;
            }

            if (root == null)
            {
                error = "Skeleton root is null.";
                return false;
            }

            return TryResolveJointTransforms(
                jointNames,
                root,
                (string jointName, out Transform jointTransform, out bool ambiguous) =>
                    KimodoRetargetAvatarUtility.TryFindUniqueTransformByName(
                        root,
                        jointName,
                        out jointTransform,
                        out ambiguous),
                out jointTransforms,
                out error);
        }

        private delegate bool TryResolveJointTransform(
            string jointName,
            out Transform jointTransform,
            out bool ambiguous);

        private static bool TryResolveProfileLayout(
            string modelName,
            out string[] jointNames,
            out int[] parentIndices,
            out string error)
        {
            error = string.Empty;
            KimodoRigProfileDatabase.ResolveProfile(modelName, out _, out jointNames, out parentIndices);
            if (jointNames == null || jointNames.Length == 0)
            {
                error = $"Profile joint layout not found for '{modelName}'.";
                return false;
            }

            return true;
        }

        private static bool TryResolveJointTransforms(
            string[] jointNames,
            Transform root,
            TryResolveJointTransform tryResolveJointTransform,
            out Transform[] jointTransforms,
            out string error)
        {
            error = string.Empty;

            jointTransforms = new Transform[jointNames.Length];
            for (int i = 0; i < jointNames.Length; i++)
            {
                string jointName = jointNames[i];
                if (string.IsNullOrWhiteSpace(jointName))
                {
                    error = $"Profile joint at index {i} is empty.";
                    return false;
                }

                if (!tryResolveJointTransform(jointName, out jointTransforms[i], out bool ambiguous))
                {
                    error = ambiguous
                        ? $"Profile joint '{jointName}' matches multiple transforms under '{root.name}'."
                        : $"Profile joint '{jointName}' was not found under '{root.name}'.";
                    jointTransforms = Array.Empty<Transform>();
                    return false;
                }
            }

            return true;
        }
    }
}
