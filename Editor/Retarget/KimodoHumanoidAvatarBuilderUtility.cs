using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoHumanoidAvatarBuilderUtility
    {
        internal static bool TryLoadImporterAvatar(GameObject gameObject, out Avatar avatar, out string modelImporterPath)
        {
            return AvatarSetupToolExtension.TryLoadImporterAvatar(gameObject, out avatar, out modelImporterPath);
        }

        internal static Avatar GenerateHumanoidAvatar(
            GameObject sourceRoot,
            out string error)
        {
            error = string.Empty;
            if (sourceRoot == null)
            {
                error = "Avatar root object is null.";
                return null;
            }

            try
            {
                GameObject rootObject = sourceRoot;
                if (sourceRoot.TryGetComponent(out Animator animator))
                {
                    rootObject = GetAnimatorAvatarRootGameObject(animator);
                }

                return AvatarSetupToolExtension.AutoGenerateHumanoidAvatarFromModelOrThrow(rootObject, forceReimport: true);
            }
            catch (Exception e)
            {
                error = $"GenerateHumanoidAvatar failed: {e.Message}";
                return null;
            }
        }

        private static GameObject GetAnimatorAvatarRootGameObject(Animator animator)
        {
            if (animator == null)
            {
                return null;
            }

#if UNITY_2022_1_OR_NEWER
            if (animator.avatarRoot != null)
            {
                return animator.avatarRoot.gameObject;
            }
#endif
            return animator.gameObject;
        }
    }
}
