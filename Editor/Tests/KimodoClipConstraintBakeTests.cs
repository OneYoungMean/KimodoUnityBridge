using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoClipConstraintBakeTests
    {
        [Test]
        public void MergeMaskedMotionKeepsUnconstrainedRootAxesAndJoints()
        {
            KimodoRawMotionData baseline = CreateMotion(
                new[] { new Vector3(1f, 2f, 3f) },
                Quaternion.identity,
                Quaternion.identity);
            KimodoRawMotionData constrained = CreateMotion(
                new[] { new Vector3(9f, 8f, 7f) },
                Quaternion.Euler(0f, 45f, 0f),
                Quaternion.Euler(30f, 0f, 0f));

            var mask = new KimodoClipConstraintMask
            {
                rootPosition = new KimodoClipConstraintPositionMask { x = true },
                rootHeading = false,
                rootRotation = false,
                joints = new List<KimodoClipConstraintJointMask>
                {
                    new KimodoClipConstraintJointMask
                    {
                        jointName = "Spine",
                        rotation = true,
                        position = new KimodoClipConstraintPositionMask()
                    }
                }
            };

            KimodoRawMotionData merged = KimodoClipConstraintBakeUtility.MergeMaskedMotion(
                baseline,
                constrained,
                mask);

            Assert.That(merged.TryReadUnityRootPosition(0, out Vector3 root), Is.True);
            Assert.That(root, Is.EqualTo(new Vector3(9f, 2f, 3f)));
            Assert.That(merged.TryReadUnityLocalRotation(0, 0, 2, out Quaternion rootRotation), Is.True);
            Assert.That(Quaternion.Angle(rootRotation, Quaternion.identity), Is.LessThan(0.001f));
            Assert.That(merged.TryReadUnityLocalRotation(0, 1, 2, out Quaternion spineRotation), Is.True);
            Assert.That(Quaternion.Angle(spineRotation, Quaternion.Euler(30f, 0f, 0f)), Is.LessThan(0.001f));
        }

        [Test]
        public void AppendConstraintsJsonCombinesArrays()
        {
            string result = KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                "[{\"type\":\"root2d\"}]",
                "[{\"type\":\"fullbody\"}]");

            Assert.That(result, Does.Contain("root2d"));
            Assert.That(result, Does.Contain("fullbody"));
            Assert.That(result, Does.StartWith("["));
            Assert.That(result, Does.EndWith("]"));
        }

        [Test]
        public void AlignConstraintMotionDropsRuntimeGuardFrame()
        {
            KimodoRawMotionData baseline = CreateMotion(
                new[] { new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f) },
                Quaternion.identity,
                Quaternion.identity);
            KimodoRawMotionData constraint = CreateMotion(
                new[] { new Vector3(-1f, 0f, 0f), new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f) },
                Quaternion.identity,
                Quaternion.identity);

            KimodoRawMotionData aligned = KimodoClipConstraintBakeUtility.AlignConstraintMotion(
                baseline,
                constraint,
                trimStartFrame: 1);

            Assert.That(aligned.FrameCount, Is.EqualTo(2));
            Assert.That(aligned.TryReadUnityRootPosition(0, out Vector3 first), Is.True);
            Assert.That(aligned.TryReadUnityRootPosition(1, out Vector3 second), Is.True);
            Assert.That(first, Is.EqualTo(new Vector3(10f, 0f, 0f)));
            Assert.That(second, Is.EqualTo(new Vector3(20f, 0f, 0f)));
        }

        private static KimodoRawMotionData CreateMotion(
            IReadOnlyList<Vector3> roots,
            Quaternion rootRotation,
            Quaternion spineRotation)
        {
            var rotations = new List<float>();
            for (int frame = 0; frame < roots.Count; frame++)
            {
                AppendWireQuaternion(rotations, rootRotation);
                AppendWireQuaternion(rotations, spineRotation);
            }
            var copiedRoots = new Vector3[roots.Count];
            for (int frame = 0; frame < roots.Count; frame++)
            {
                copiedRoots[frame] = roots[frame];
            }
            return new KimodoRawMotionData(
                roots.Count,
                2,
                30f,
                new[] { "Hips", "Spine" },
                new[] { -1, 0 },
                copiedRoots,
                rotations,
                0);
        }

        private static void AppendWireQuaternion(List<float> output, Quaternion unityRotation)
        {
            unityRotation.Normalize();
            output.Add(unityRotation.w);
            output.Add(unityRotation.x);
            output.Add(-unityRotation.y);
            output.Add(-unityRotation.z);
        }
    }
}
