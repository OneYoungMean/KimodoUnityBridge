using System.Collections.Generic;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class ArdyClipConstraintProtocolTests
    {
        [Test]
        public void SerializeFuture_UsesFlatMaskInArdyJointOrder()
        {
            AnimationHandleOperator handle = CreateHandle(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                27,
                20f,
                40);
            KimodoArdyConstraintMask mask = KimodoArdyConstraintMask.UpperBody(
                KimodoMotionModelProfiles.ArdyCoreModelName);
            string json = KimodoArdyClipConstraintProtocol.SerializeFuture(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                new List<KimodoArdyClipConstraint>
                {
                    new KimodoArdyClipConstraint
                    {
                        animation = handle,
                        startFrame = 2,
                        endFrameExclusive = 10,
                        mask = mask
                    }
                });

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
        public void MergeHandles_UsesCompleteHistoryHandle()
        {
            AnimationHandleOperator handle = CreateHandle(
                KimodoMotionModelProfiles.ArdyCoreModelName,
                27,
                20f,
                160);

            string json = ArdyClipConstraintSerializer.MergeHandles(
                new List<AnimationHandleInfo> { handle.Info },
                maxHandles: 4,
                futureConstraintsJson: string.Empty);

            Assert.That(json, Does.Contain("\"start_frame\":0"));
            Assert.That(json, Does.Contain("\"end_frame_exclusive\":160"));
            Assert.That(json, Does.Contain("\"is_history\":true"));
        }

        private static AnimationHandleOperator CreateHandle(
            string modelName,
            int jointCount,
            float fps,
            int frames)
        {
            var info = new AnimationHandleInfo
            {
                Handle = "animation:test",
                ModelName = modelName,
                JointCount = jointCount,
                Fps = fps,
                FrameCount = frames
            };
            return new AnimationHandleOperator(KimodoBridgeService.Shared, info);
        }
    }
}
