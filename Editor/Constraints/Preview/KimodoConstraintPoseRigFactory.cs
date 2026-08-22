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
            public RetargetSkeleton TargetCache;
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
            if (KimodoEditorObjectIdUtility.ObjectFromId(animatorId) is not Animator)
            {
                error = "Timeline binding Animator is missing.";
                return false;
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    modelName,
                    out Avatar targetAvatar,
                    out error))
            {
                return false;
            }

            RetargetSkeleton targetCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryCreateVirtualSkeleton(
                        targetAvatar,
                        $"__KimodoConstraintAvatar_{clipId}_{animatorId}",
                        animatorEnabled: true,
                        applyRootMotion: true,
                        out GameObject targetRoot,
                        out Animator targetAnimator,
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

                targetCache.root.name = $"__KimodoConstraintAvatar_{clipId}_{animatorId}";
                targetCache.root.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                instance = new PoseRigInstance
                {
                    Root = targetCache.root,
                    TargetCache = targetCache,
                    GeneratedMaterials = new List<Material>()
                };
                targetCache = null;
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
            }
        }
    }
}
