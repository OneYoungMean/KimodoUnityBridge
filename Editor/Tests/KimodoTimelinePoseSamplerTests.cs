using System;
using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoTimelinePoseSamplerTests
    {
        [Test]
        public void ResolveSourceHumanBone_UsesSourceAvatarWhenAnimatorAvatarIsNull()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);

            HumanBone hips = Array.Find(
                avatar.humanDescription.human,
                bone => bone.humanName == HumanBodyBones.Hips.ToString());
            var root = new GameObject("Root");
            try
            {
                Animator animator = root.AddComponent<Animator>();
                Transform expected = new GameObject(hips.boneName).transform;
                expected.SetParent(root.transform);

                Assert.That(animator.avatar, Is.Null);
                Assert.That(
                    KimodoTimelinePoseSampler.ResolveSourceHumanBone(animator, avatar, HumanBodyBones.Hips),
                    Is.SameAs(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ArdyHistoryRoot_UsesSamePlanarAnchorAsConstraints()
        {
            var source = new ArdyEditorHistorySource
            {
                NormalizeRootToAnchor = true,
                AnchorRootPosition = new Vector3(10f, 0f, 20f),
                AnchorRootRotation = Quaternion.Euler(0f, 90f, 0f)
            };
            Vector3 position = new Vector3(12f, 3f, 25f);
            Quaternion rotation = Quaternion.Euler(0f, 120f, 0f);
            Quaternion inverseAnchor = Quaternion.Inverse(source.AnchorRootRotation);

            KimodoConstraintNormalizationUtility.NormalizeRootPose(
                source.AnchorRootPosition,
                source.AnchorRootRotation,
                ref position,
                ref rotation);

            Assert.That(
                Vector3.Distance(position, inverseAnchor * new Vector3(2f, 3f, 5f)),
                Is.LessThan(1e-5f));
            Assert.That(
                Quaternion.Angle(rotation, inverseAnchor * Quaternion.Euler(0f, 120f, 0f)),
                Is.LessThan(1e-4f));
        }

        [Test]
        public void HumanoidIkGoals_AreInvariantToSkeletonRootWorldPose()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineFootIkRootSpaceTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample before = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);

                cache.skeletonRoot.SetPositionAndRotation(
                    new Vector3(7f, 2f, -3f),
                    Quaternion.Euler(0f, 73f, 0f));
                MuscleSample after = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);

                Assert.That(Vector3.Distance(before.leftFootPosition, after.leftFootPosition), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(before.rightFootPosition, after.rightFootPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(before.leftFootRotation, after.leftFootRotation), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(before.rightFootRotation, after.rightFootRotation), Is.LessThan(1e-4f));
            }
            finally
            {
                cache.Dispose();
            }
        }
    }
}
