#if false
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
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

        [Test]
        public void LoopTerminalConstraintUsesFirstPoseAndTailHeading()
        {
            var first = new KimodoMarkerSampleResult
            {
                characterPose = new CharacterPose
                {
                    root = new KimodoRigidTransform
                    {
                        t = new Vector3(1f, 2f, 3f),
                        q = Quaternion.Euler(0f, 10f, 0f)
                    }
                },
                mask = KimodoConstraintMask.ForType("fullbody")
            };
            var tail = new KimodoMarkerSampleResult
            {
                characterPose = new CharacterPose
                {
                    root = new KimodoRigidTransform
                    {
                        t = new Vector3(9f, 2f, 8f),
                        q = Quaternion.Euler(0f, 75f, 0f)
                    }
                },
                mask = KimodoConstraintMask.ForType("root2d")
            };

            KimodoMarkerSampleResult result =
                KimodoClipConstraintBakeUtility.BuildLoopTerminalConstraintSample(first, tail, 3.0);

            Assert.That(result.sampleTime, Is.EqualTo(3.0));
            Assert.That(result.constraintType, Is.EqualTo("fullbody"));
            Assert.That(result.characterPose.root.t, Is.EqualTo(new Vector3(9f, 2f, 8f)));
            Assert.That(
                Quaternion.Angle(result.characterPose.root.q, Quaternion.Euler(0f, 75f, 0f)),
                Is.LessThan(0.001f));
            Assert.That(result.hasRoot2DOverride, Is.False);
        }

        [Test]
        public void LoopConstraintSamplesExtrapolateSparseRootAnchors()
        {
            var first = new KimodoMarkerSampleResult
            {
                characterPose = new CharacterPose
                {
                    root = new KimodoRigidTransform
                    {
                        t = new Vector3(1f, 2f, 3f),
                        q = Quaternion.Euler(0f, 10f, 0f)
                    }
                },
                mask = KimodoConstraintMask.ForType("fullbody")
            };
            var tail = new KimodoMarkerSampleResult
            {
                characterPose = new CharacterPose
                {
                    root = new KimodoRigidTransform
                    {
                        t = new Vector3(5f, 4f, 11f),
                        q = Quaternion.Euler(0f, 50f, 0f)
                    }
                },
                mask = KimodoConstraintMask.ForType("root2d")
            };

            List<KimodoMarkerSampleResult> samples =
                KimodoClipConstraintBakeUtility.BuildLoopConstraintSamples(
                    first,
                    tail,
                    runtimeTrimStartFrame: 34,
                    targetFrameCount: 68,
                    runtimeFrameCount: 136,
                    frameRate: 30f);

            float ratio = 34f / 67f;
            Assert.That(samples, Has.Count.EqualTo(4));
            Assert.That(samples[0].sampleTime, Is.Zero);
            Assert.That(samples[1].sampleTime, Is.EqualTo(34.0 / 30.0).Within(1e-8));
            Assert.That(samples[2].sampleTime, Is.EqualTo(101.0 / 30.0).Within(1e-8));
            Assert.That(samples[3].sampleTime, Is.EqualTo(135.0 / 30.0).Within(1e-8));
            Assert.That(
                Vector3.Distance(
                    samples[0].root2DOverride.t,
                    new Vector3(1f - 4f * ratio, 2f, 3f - 8f * ratio)),
                Is.LessThan(1e-5f));
            Assert.That(samples[1].constraintType, Is.EqualTo("fullbody"));
            Assert.That(samples[1].hasRoot2DOverride, Is.False);
            Assert.That(samples[1].characterPose.root.t, Is.EqualTo(first.characterPose.root.t));
            Assert.That(
                Quaternion.Angle(samples[1].characterPose.root.q, first.characterPose.root.q),
                Is.LessThan(0.001f));
            Assert.That(samples[2].constraintType, Is.EqualTo("fullbody"));
            Assert.That(samples[2].hasRoot2DOverride, Is.False);
            Assert.That(samples[2].characterPose.root.t, Is.EqualTo(new Vector3(5f, 2f, 11f)));
            Assert.That(
                Quaternion.Angle(samples[2].characterPose.root.q, Quaternion.Euler(0f, 50f, 0f)),
                Is.LessThan(0.001f));
            Assert.That(
                Vector3.Distance(
                    samples[3].root2DOverride.t,
                    new Vector3(5f + 4f * ratio, 4f, 11f + 8f * ratio)),
                Is.LessThan(1e-5f));
            Assert.That(
                Quaternion.Angle(samples[0].root2DOverride.q, Quaternion.Euler(0f, 10f - 40f * ratio, 0f)),
                Is.LessThan(0.001f));
            Assert.That(
                Quaternion.Angle(samples[3].root2DOverride.q, Quaternion.Euler(0f, 50f + 40f * ratio, 0f)),
                Is.LessThan(0.001f));
        }

        [Test, Ignore("RawData was removed from SampleResult; rewrite against sampleData protocol.")]
        public void LoopConstraintJsonContainsSparseRootAndVisibleFullBodyBoundaries()
        {
            string modelName = KimodoMotionModelProfiles.DefaultModelName;
            int jointCount = KimodoRigProfileDatabase.GetJointNamesForModel(modelName).Length;
            var localAxisAngles = new List<Vector3>(jointCount);
            for (int joint = 0; joint < jointCount; joint++)
            {
                localAxisAngles.Add(Vector3.zero);
            }
            localAxisAngles[1] = new Vector3(0.1f, 0.2f, 0.3f);

            var first = new KimodoMarkerSampleResult
            {
                characterPose = new CharacterPose
                {
                    root = new KimodoRigidTransform
                    {
                        t = new Vector3(0.25f, 0.9f, -0.5f),
                        q = Quaternion.Euler(0f, 10f, 0f)
                    }
                },
                mask = KimodoConstraintMask.ForType("fullbody")
            };
            var tail = new KimodoMarkerSampleResult
            {
                characterPose = new CharacterPose
                {
                    root = new KimodoRigidTransform
                    {
                        t = new Vector3(0.5f, 0.9f, 1.7f),
                        q = Quaternion.Euler(0f, 20f, 0f)
                    }
                },
                mask = KimodoConstraintMask.ForType("root2d")
            };

            JArray constraints = JArray.Parse(KimodoClipConstraintBakeUtility.BuildLoopConstraintJson(
                first,
                tail,
                modelName,
                runtimeTrimStartFrame: 34,
                targetFrameCount: 68,
                runtimeFrameCount: 136,
                frameRate: 30f));
            JObject root2D = null;
            JObject fullBody = null;
            foreach (JObject constraint in constraints)
            {
                string type = constraint.Value<string>("type");
                if (type == "root2d") root2D = constraint;
                if (type == "fullbody") fullBody = constraint;
            }

            Assert.That(root2D, Is.Not.Null);
            Assert.That(fullBody, Is.Not.Null);
            Assert.That(root2D["frame_indices"].Values<int>(), Is.EqualTo(new[] { 0, 135 }));
            Assert.That(root2D["smooth_root_2d"], Has.Count.EqualTo(2));
            Assert.That(root2D["global_root_heading"], Has.Count.EqualTo(2));
            Assert.That(root2D["dense_path"], Is.Null);
            Assert.That(fullBody["frame_indices"].Values<int>(), Is.EqualTo(new[] { 34, 101 }));
            Assert.That(fullBody["root_positions"][0][0].Value<float>(), Is.EqualTo(-0.25f).Within(1e-5f));
            Assert.That(fullBody["root_positions"][0][1].Value<float>(), Is.EqualTo(0.9f).Within(1e-5f));
            Assert.That(fullBody["root_positions"][0][2].Value<float>(), Is.EqualTo(-0.5f).Within(1e-5f));
            Assert.That(fullBody["root_positions"][1][0].Value<float>(), Is.EqualTo(-0.5f).Within(1e-5f));
            Assert.That(fullBody["root_positions"][1][1].Value<float>(), Is.EqualTo(0.9f).Within(1e-5f));
            Assert.That(fullBody["root_positions"][1][2].Value<float>(), Is.EqualTo(1.7f).Within(1e-5f));
            Quaternion tailRoot = Quaternion.Euler(0f, 20f, 0f);
            Vector3 expectedRootAxisAngle = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(
                new Quaternion(tailRoot.x, -tailRoot.y, -tailRoot.z, tailRoot.w));
            Assert.That(fullBody["local_joints_rot"][1][0][0].Value<float>(), Is.EqualTo(expectedRootAxisAngle.x).Within(1e-5f));
            Assert.That(fullBody["local_joints_rot"][1][0][1].Value<float>(), Is.EqualTo(expectedRootAxisAngle.y).Within(1e-5f));
            Assert.That(fullBody["local_joints_rot"][1][0][2].Value<float>(), Is.EqualTo(expectedRootAxisAngle.z).Within(1e-5f));
            for (int joint = 1; joint < jointCount; joint++)
            {
                Assert.That(
                    JToken.DeepEquals(
                        fullBody["local_joints_rot"][0][joint],
                        fullBody["local_joints_rot"][1][joint]),
                    Is.True,
                    $"Joint {joint} changed between loop boundaries.");
            }
        }

        [Test]
        public void RawLoopSamplesPreserveRootTrajectory()
        {
            var roots = new[]
            {
                new Vector3(0.25f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 1.7f)
            };
            var rotations = new List<float>();
            AppendWireQuaternion(rotations, Quaternion.Euler(0f, 10f, 0f));
            AppendWireQuaternion(rotations, Quaternion.identity);
            AppendWireQuaternion(rotations, Quaternion.Euler(0f, 75f, 0f));
            AppendWireQuaternion(rotations, Quaternion.identity);
            var motion = new KimodoRawMotionData(
                roots.Length,
                2,
                30f,
                new[] { "Hips", "Spine" },
                new[] { -1, 0 },
                roots,
                rotations,
                0);

            Assert.That(
                KimodoRawMotionUtility.TryExtractMarkerSample(
                    motion,
                    KimodoMotionModelProfiles.DefaultModelName,
                    0,
                    out KimodoMarkerSampleResult first,
                    out string firstError,
                    "fullbody",
                    allowPartialJoints: true),
                Is.True,
                firstError);
            Assert.That(
                KimodoRawMotionUtility.TryExtractMarkerSample(
                    motion,
                    KimodoMotionModelProfiles.DefaultModelName,
                    motion.FrameCount - 1,
                    out KimodoMarkerSampleResult tail,
                    out string tailError,
                    "root2d",
                    allowPartialJoints: true),
                Is.True,
                tailError);

            Assert.That(first.characterPose.root.t, Is.EqualTo(roots[0]));
            Assert.That(tail.characterPose.root.t, Is.EqualTo(roots[1]));
            KimodoMarkerSampleResult terminal =
                KimodoClipConstraintBakeUtility.BuildLoopTerminalConstraintSample(first, tail, 3.0);
            Assert.That(terminal.constraintType, Is.EqualTo("fullbody"));
            Assert.That(terminal.hasRoot2DOverride, Is.False);
            Assert.That(terminal.characterPose.root.t, Is.EqualTo(new Vector3(roots[1].x, roots[0].y, roots[1].z)));
            Assert.That(
                Quaternion.Angle(terminal.characterPose.root.q, Quaternion.Euler(0f, 75f, 0f)),
                Is.LessThan(0.001f));
        }

        [Test]
        public void RawConstraintData_UsesProfileOrderAndPreservesLocalPose()
        {
            string modelName = KimodoMotionModelProfiles.DefaultModelName;
            string[] profileJointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            var motionJointNames = new string[profileJointNames.Length];
            for (int i = 0; i < motionJointNames.Length; i++)
            {
                motionJointNames[i] = profileJointNames[motionJointNames.Length - 1 - i];
            }
            var rotations = new List<float>(profileJointNames.Length * 4);
            for (int joint = 0; joint < motionJointNames.Length; joint++)
            {
                AppendWireQuaternion(
                    rotations,
                    motionJointNames[joint] == profileJointNames[1]
                        ? Quaternion.Euler(0f, 25f, 0f)
                        : Quaternion.identity);
            }

            var motion = new KimodoRawMotionData(
                1,
                profileJointNames.Length,
                30f,
                motionJointNames,
                KimodoRigProfileDatabase.GetParentIndicesForModel(modelName),
                new[] { new Vector3(0.25f, 0.5f, 1.7f) },
                rotations,
                0);

            Assert.That(
                KimodoRawMotionUtility.TryBuildConstraintRawData(
                    motion,
                    modelName,
                    0,
                    out KimodoConstraintRawData rawData,
                    out string error),
                Is.True,
                error);
            Assert.That(rawData.rootPosition, Is.EqualTo(new Vector3(0.25f, 0.5f, 1.7f)));
            Assert.That(rawData.localJointAxisAngles, Has.Count.EqualTo(profileJointNames.Length));
            Assert.That(rawData.localJointAxisAngles[1].magnitude, Is.GreaterThan(0.1f));
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

#endif
