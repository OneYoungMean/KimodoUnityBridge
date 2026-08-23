using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintPoseRigFactoryTests
    {
        [Test]
        public void PoseRigClone_CopiesStaticMeshRenderer()
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
                GameObject sourceVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sourceVisual.name = "StaticVisual";
                sourceVisual.transform.SetParent(source.animator.transform, false);
                Mesh sourceMesh = sourceVisual.GetComponent<MeshFilter>().sharedMesh;
                sourceVisual.GetComponent<MeshRenderer>().sortingOrder = 7;

                Assert.That(
                    KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                        KimodoMotionModelProfiles.DefaultModelName,
                        clipId: 1,
                        animatorId: KimodoUnityObjectIdUtility.IdHash(source.animator),
                        sourceAvatar: avatar,
                        out rig,
                        out error),
                    Is.True,
                    error);

                Transform cloneVisual = rig.Root.transform.Find("StaticVisual");
                Assert.That(cloneVisual, Is.Not.Null);
                MeshRenderer cloneRenderer = cloneVisual.GetComponent<MeshRenderer>();
                Assert.That(cloneRenderer, Is.Not.Null);
                Assert.That(cloneVisual.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(sourceMesh));
                Assert.That(cloneRenderer.sortingOrder, Is.EqualTo(7));
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
