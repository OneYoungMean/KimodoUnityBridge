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
    }
}
