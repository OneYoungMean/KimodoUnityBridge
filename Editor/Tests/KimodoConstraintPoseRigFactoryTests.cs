using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintPoseRigFactoryTests
    {
        [Test]
        public void PoseRig_UsesModelTargetSkeleton()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoMotionModelProfiles.DefaultModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar,
                    "KimodoStaticMeshPreviewTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            KimodoConstraintPoseRigFactory.PoseRigInstance rig = null;
            try
            {
                Assert.That(
                    KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                        KimodoMotionModelProfiles.DefaultModelName,
                        clipId: 1,
                        animatorId: KimodoUnityObjectIdUtility.IdHash(source.animator),
                        out rig,
                        out error),
                    Is.True,
                    error);

                Assert.That(rig.TargetCache, Is.Not.Null);
                Assert.That(rig.TargetCache.avatar, Is.Not.Null);
                Assert.That(rig.TargetCache.animator.GetBoneTransform(HumanBodyBones.Hips), Is.Not.Null);
            }
            finally
            {
                rig?.TargetCache?.Dispose();
                if (rig?.GeneratedMaterials != null)
                {
                    for (int i = 0; i < rig.GeneratedMaterials.Count; i++)
                    {
                        Object.DestroyImmediate(rig.GeneratedMaterials[i]);
                    }
                }
                source.Dispose();
            }
        }
    }
}
