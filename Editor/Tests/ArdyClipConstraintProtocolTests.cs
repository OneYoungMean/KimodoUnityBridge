using System.Collections.Generic;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoClipConstraintProtocolTests
    {
        [Test]
        public void Serialize_UsesTargetTimeAndNamedMask()
        {
            byte[] kmb = CreateKmb(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                27,
                20f,
                40);
            KimodoClipConstraintMask mask = KimodoClipConstraintMask.UpperBody(
                KimodoMotionModelProfiles.ArdyCoreModelName);
            var attachments = new List<byte[]>();
            string json = KimodoClipConstraintProtocol.Serialize(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                new List<KimodoClipConstraint>
                {
                    new KimodoClipConstraint
                    {
                        motionBytes = kmb,
                        startTime = 0.5f,
                        duration = 2f,
                        mask = mask
                    }
                },
                attachments);

            Assert.That(attachments, Has.Count.EqualTo(1));
            Assert.That(json, Does.Contain("\"format\":\"kmb_attachment_v1\""));
            Assert.That(json, Does.Contain("\"attachment\":0"));
            Assert.That(json, Does.Not.Contain("is_history"));
            Assert.That(json, Does.Contain("\"start_time\":0.5"));
            Assert.That(json, Does.Contain("\"duration\":2.0"));
            Assert.That(json, Does.Contain("\"mask\":{\"root_position\":[false,false,false]"));
            Assert.That(json, Does.Contain("\"joint_name\":\"Spine\""));
            Assert.That(json, Does.Contain("\"joint_name\":\"RightUpLeg\""));
            Assert.That(json, Does.Contain("\"rotation\":true"));
        }

        [Test]
        public void MaskHelpers_SelectKnownGenericModel()
        {
            KimodoClipConstraintMask mask = KimodoClipConstraintMask.UpperBody("Kimodo-SOMA-RP-v1");
            Assert.That(mask.joints, Is.Not.Empty);
        }

        [Test]
        public void Serialize_NegativeTimeRepresentsHistoryWithoutHistoryType()
        {
            byte[] kmb = CreateKmb(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                27,
                20f,
                160);

            var attachments = new List<byte[]>();
            string json = KimodoClipConstraintProtocol.Serialize(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                new List<KimodoClipConstraint>
                {
                    new KimodoClipConstraint
                    {
                        motionBytes = kmb,
                        startTime = -8f,
                        duration = 8f,
                        mask = null
                    }
                },
                attachments);

            Assert.That(attachments, Has.Count.EqualTo(1));
            Assert.That(json, Does.Contain("\"attachment\":0"));
            Assert.That(json, Does.Contain("\"start_time\":-8.0"));
            Assert.That(json, Does.Contain("\"duration\":8.0"));
            Assert.That(json, Does.Not.Contain("is_history"));
            Assert.That(json, Does.Not.Contain("mask"));
        }

        private static byte[] CreateKmb(
            string modelName,
            int jointCount,
            float fps,
            int frames)
        {
            var rotations = new List<float>(frames * jointCount * 4);
            for (int frame = 0; frame < frames; frame++)
            {
                for (int joint = 0; joint < jointCount; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            var motion = new KimodoRawMotionData(
                frames,
                jointCount,
                fps,
                names,
                new int[jointCount],
                new UnityEngine.Vector3[frames],
                rotations,
                0);
            return KimodoRawMotionUtility.ToFlatBuffer(motion, modelName);
        }
    }
}
