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

        private static Quaternion Planar(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
    }
}
