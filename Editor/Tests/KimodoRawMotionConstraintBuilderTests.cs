using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRawMotionConstraintBuilderTests
    {
        [Test]
        public void BuildLoopConstraintJson_UsesRawMotionWithoutCharacterSpaceSamples()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 3;
            const float frameRate = 20f;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[]
            {
                new Vector3(2f, 0f, 3f),
                new Vector3(4f, 0f, 5f),
                new Vector3(6f, 0f, 7f)
            };
            Quaternion firstRootRotation = Quaternion.Euler(20f, 10f, 30f);
            Quaternion tailRootRotation = Quaternion.Euler(-40f, 70f, 80f);
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 && frame == 0 ? firstRootRotation :
                        joint == 0 && frame == frameCount - 1 ? tailRootRotation : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }

            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                frameRate,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopConstraintJson(
                motion, model, runtimeTrimStartFrame: 1, targetFrameCount: frameCount,
                runtimeFrameCount: 5, frameRate: frameRate));

            Assert.That(constraints, Has.Count.EqualTo(2));
            Assert.That((string)constraints[0]["type"], Is.EqualTo("root2d"));
            Assert.That((int)constraints[1]["frame_indices"][0], Is.EqualTo(1));
            Assert.That((float)constraints[1]["root_positions"][0][0], Is.EqualTo(-2f));
            Assert.That((int)constraints[1]["frame_indices"][1], Is.EqualTo(3));
            Assert.That((float)constraints[1]["root_positions"][1][0], Is.EqualTo(-6f));
            Assert.That((int)constraints[0]["frame_indices"][1], Is.EqualTo(4));
            Assert.That((float)constraints[0]["smooth_root_2d"][1][0], Is.EqualTo(-8f));
            Assert.That(
                ((JArray)constraints[1]["smooth_root_2d"]).Count,
                Is.EqualTo(((JArray)constraints[1]["frame_indices"]).Count));
            JArray terminalRoot = (JArray)constraints[1]["local_joints_rot"][1][0];
            Quaternion protocolRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(new Vector3(
                (float)terminalRoot[0], (float)terminalRoot[1], (float)terminalRoot[2]));
            Quaternion terminalRotation = new Quaternion(
                protocolRotation.x, -protocolRotation.y, -protocolRotation.z, protocolRotation.w);
            Quaternion expectedTerminalRotation = Planar(tailRootRotation) *
                Quaternion.Inverse(Planar(firstRootRotation)) * firstRootRotation;
            Assert.That(Quaternion.Angle(terminalRotation, expectedTerminalRotation), Is.LessThan(1e-3f));

            JArray root2D = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildRoot2DConstraintsJson(
                motion, model, new[] { 0, 2 }));
            Assert.That((float)root2D[0]["smooth_root_2d"][1][0], Is.EqualTo(-6f));
        }

        [Test]
        public void BuildPathAngleConstraintJson_SamplesBoundariesAndExistingFullBodyFrames()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 5;
            const float frameRate = 20f;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                roots[frame] = new Vector3(0f, 1f, frame);
                for (int joint = 0; joint < names.Length; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                frameRate,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);
            string existing = new JArray(new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = new JArray(4),
                ["root_positions"] = new JArray(new JArray(9f, 8f, 7f)),
                ["local_joints_rot"] = new JArray(new JArray())
            }).ToString();

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                motion,
                model,
                pathBeginAngleDegrees: 0f,
                pathEndAngleDegrees: 90f,
                runtimeTrimStartFrame: 2,
                targetFrameCount: frameCount,
                runtimeFrameCount: 9,
                frameRate: frameRate,
                existingConstraintsJson: existing,
                regularFrameInterval: 3));

            CollectionAssert.AreEqual(
                new[] { 0, 2, 3, 4, 6, 8 },
                constraints[0]["frame_indices"].Values<int>());
            float radius = 4f / (Mathf.PI * 0.5f);
            Assert.That((float)constraints[0]["smooth_root_2d"][4][0], Is.EqualTo(-radius).Within(0.001f));
            Assert.That((float)constraints[0]["smooth_root_2d"][4][1], Is.EqualTo(radius).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][4][0], Is.EqualTo(0f).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][4][1], Is.EqualTo(-1f).Within(0.001f));

            JArray shaped = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                motion,
                model,
                pathBeginAngleDegrees: 90f,
                pathEndAngleDegrees: 180f,
                runtimeTrimStartFrame: 2,
                targetFrameCount: frameCount,
                runtimeFrameCount: 9,
                frameRate: frameRate,
                existingConstraintsJson: existing,
                regularFrameInterval: 3));
            Assert.That((float)shaped[0]["global_root_heading"][1][0], Is.EqualTo(0f).Within(0.001f));
            Assert.That((float)shaped[0]["global_root_heading"][1][1], Is.EqualTo(-1f).Within(0.001f));
            Assert.That((float)shaped[0]["global_root_heading"][4][0], Is.EqualTo(-1f).Within(0.001f));
            Assert.That((float)shaped[0]["global_root_heading"][4][1], Is.EqualTo(0f).Within(0.001f));
            Assert.That(
                JToken.DeepEquals(shaped[0]["smooth_root_2d"], constraints[0]["smooth_root_2d"]),
                Is.False);

            JToken pathPositions = constraints[0]["smooth_root_2d"].DeepClone();
            JArray overridden = JArray.Parse(KimodoRawMotionConstraintBuilder.OverrideRoot2DHeadingsJson(
                constraints.ToString(),
                headingDegrees: 0f));
            Assert.That(overridden, Has.Count.EqualTo(1));
            Assert.That(JToken.DeepEquals(overridden[0]["smooth_root_2d"], pathPositions), Is.True);
            foreach (JArray heading in overridden[0]["global_root_heading"].Children<JArray>())
            {
                Assert.That((float)heading[0], Is.EqualTo(1f).Within(0.001f));
                Assert.That((float)heading[1], Is.EqualTo(0f).Within(0.001f));
            }
        }

        [Test]
        public void BuildHeadingOverrideConstraintJson_PreservesLastRootPositionsAndReplacesHeadings()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 62;
            const float frameRate = 20f;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * names.Length * 4);
            Quaternion sourceHeading = Quaternion.Euler(0f, 45f, 0f);
            for (int frame = 0; frame < frameCount; frame++)
            {
                roots[frame] = Vector3.forward * frame;
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 ? sourceHeading : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                frameRate,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);
            string existing = new JArray(
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(1),
                    ["root_positions"] = new JArray(new JArray(100f, 0f, 200f))
                },
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(1),
                    ["smooth_root_2d"] = new JArray(new JArray(-9f, 11f)),
                    ["global_root_heading"] = new JArray(new JArray(1f, 0f))
                }).ToString();

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildHeadingOverrideConstraintJson(
                motion,
                headingDegrees: 0f,
                runtimeTrimStartFrame: 0,
                targetFrameCount: frameCount,
                runtimeFrameCount: frameCount,
                frameRate: frameRate,
                existingConstraintsJson: existing));

            CollectionAssert.AreEqual(
                new[] { 0, 1, 30, 60, 61 },
                constraints[0]["frame_indices"].Values<int>());
            Assert.That((float)constraints[0]["smooth_root_2d"][1][0], Is.EqualTo(-9f));
            Assert.That((float)constraints[0]["smooth_root_2d"][1][1], Is.EqualTo(11f));
            foreach (JArray heading in constraints[0]["global_root_heading"].Children<JArray>())
            {
                Assert.That((float)heading[0], Is.EqualTo(1f).Within(0.001f));
                Assert.That((float)heading[1], Is.EqualTo(0f).Within(0.001f));
            }
        }

        private static Quaternion Planar(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
    }
}
