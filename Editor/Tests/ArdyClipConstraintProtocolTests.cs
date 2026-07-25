using System.Collections.Generic;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class ArdyClipConstraintProtocolTests
    {
        [Test]
        public void SerializeFuture_UsesFlatMaskInArdyJointOrder()
        {
            byte[] kmb = CreateKmb(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                27,
                20f,
                40);
            KimodoArdyConstraintMask mask = KimodoArdyConstraintMask.UpperBody(
                KimodoMotionModelProfiles.ArdyCoreModelName);
            var attachments = new List<byte[]>();
            string json = KimodoArdyClipConstraintProtocol.SerializeFuture(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                new List<KimodoArdyClipConstraint>
                {
                    new KimodoArdyClipConstraint
                    {
                        motionBytes = kmb,
                        startFrame = 2,
                        endFrameExclusive = 10,
                        mask = mask
                    }
                },
                attachments);

            Assert.That(attachments, Has.Count.EqualTo(1));
            Assert.That(json, Does.Contain("\"format\":\"kmb_attachment_v1\""));
            Assert.That(json, Does.Contain("\"attachment\":0"));
            Assert.That(json, Does.Contain("\"is_history\":false"));
            Assert.That(json, Does.Contain("\"start_frame\":2"));
            Assert.That(json, Does.Contain("\"end_frame_exclusive\":10"));
            int maskStart = json.IndexOf("\"mask\":[", System.StringComparison.Ordinal) + 8;
            int maskEnd = json.IndexOf(']', maskStart);
            string[] flat = json.Substring(maskStart, maskEnd - maskStart).Split(',');
            Assert.That(flat.Length, Is.EqualTo(4 + 26 * 3));
            Assert.That(flat[4], Is.EqualTo("true")); // Spine.x
            Assert.That(flat[4 + 18 * 3], Is.EqualTo("false")); // RightUpLeg.x
        }

        [Test]
        public void MaskHelpers_RejectNonArdyModel()
        {
            Assert.That(
                () => KimodoArdyConstraintMask.UpperBody("Kimodo-SOMA-RP-v1"),
                Throws.InvalidOperationException.With.Message.Contains("not a registered ARDY rig"));
        }

        [Test]
        public void SerializeHistory_UsesCompleteKmbAttachment()
        {
            byte[] kmb = CreateKmb(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                27,
                20f,
                160);

            var attachments = new List<byte[]>();
            string json = KimodoArdyClipConstraintProtocol.SerializeHistory(kmb, attachments);

            Assert.That(attachments, Has.Count.EqualTo(1));
            Assert.That(json, Does.Contain("\"attachment\":0"));
            Assert.That(json, Does.Contain("\"start_frame\":0"));
            Assert.That(json, Does.Contain("\"end_frame_exclusive\":160"));
            Assert.That(json, Does.Contain("\"is_history\":true"));
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
