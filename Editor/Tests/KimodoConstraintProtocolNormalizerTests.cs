using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintProtocolNormalizerTests
    {
        [Test]
        public void SameFrameRoot2D_IsFoldedIntoFullBodyWithHeadingAndTiltPreserved()
        {
            Quaternion sourceUnityRoot = Quaternion.Euler(20f, 40f, -15f);
            Vector3 sourceAxisAngle = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(
                ToKimodoRotation(sourceUnityRoot));
            const float targetYaw = 130f;
            var constraints = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(10, 30),
                    ["smooth_root_2d"] = new JArray(new JArray(4f, 7f), new JArray(8f, 9f)),
                    ["global_root_heading"] = new JArray(Heading(targetYaw), Heading(15f))
                },
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(10, 20),
                    ["smooth_root_2d"] = new JArray(new JArray(-2f, 3f), new JArray(-5f, 6f)),
                    ["root_positions"] = new JArray(new JArray(-2f, 1.25f, 3f), new JArray(-5f, 2.5f, 6f)),
                    ["local_joints_rot"] = new JArray(
                        new JArray(new JArray(sourceAxisAngle.x, sourceAxisAngle.y, sourceAxisAngle.z)),
                        new JArray(new JArray(0f, 0f, 0f)))
                }
            };

            JArray normalized = KimodoConstraintProtocolNormalizer.NormalizeRoot2DIntoFullBody(constraints);

            JObject root2D = (JObject)normalized[0];
            JObject fullBody = (JObject)normalized[1];
            Assert.That(root2D["frame_indices"].Values<int>(), Is.EqualTo(new[] { 30 }));
            Assert.That(fullBody["root_positions"][0][0].Value<float>(), Is.EqualTo(4f));
            Assert.That(fullBody["root_positions"][0][1].Value<float>(), Is.EqualTo(1.25f));
            Assert.That(fullBody["root_positions"][0][2].Value<float>(), Is.EqualTo(7f));
            Assert.That(fullBody["smooth_root_2d"][0][0].Value<float>(), Is.EqualTo(4f));
            Assert.That(fullBody["smooth_root_2d"][0][1].Value<float>(), Is.EqualTo(7f));

            Vector3 mergedAxisAngle = ReadVector3(fullBody["local_joints_rot"][0][0]);
            Quaternion mergedUnityRoot = FromKimodoRotation(
                KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(mergedAxisAngle));
            Quaternion sourceYaw = ResolvePlanarRotation(sourceUnityRoot);
            Quaternion expected = Quaternion.AngleAxis(targetYaw, Vector3.up) *
                Quaternion.Inverse(sourceYaw) * sourceUnityRoot;
            Assert.That(Quaternion.Angle(mergedUnityRoot, expected), Is.LessThan(1e-3f));
        }

        [Test]
        public void SameFrameRoot2D_IsFoldedIntoEndEffectorWithoutMovingItsWorldTarget()
        {
            var constraints = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(10),
                    ["smooth_root_2d"] = new JArray(new JArray(4f, 7f)),
                    ["global_root_heading"] = new JArray(Heading(90f))
                },
                new JObject
                {
                    ["type"] = "left-hand",
                    ["frame_indices"] = new JArray(10),
                    ["joint_names"] = new JArray("LeftHand"),
                    ["smooth_root_2d"] = new JArray(new JArray(-2f, 3f)),
                    ["root_positions"] = new JArray(new JArray(-2f, 1.25f, 3f)),
                    ["local_joints_rot"] = new JArray(new JArray(new JArray(0f, 0f, 0f))),
                    ["target_positions"] = new JArray(new JArray(9f, 8f, 7f))
                }
            };

            JArray normalized = KimodoConstraintProtocolNormalizer.NormalizeRoot2DIntoFullBody(constraints);

            Assert.That(normalized, Has.Count.EqualTo(1));
            JObject hand = (JObject)normalized[0];
            Assert.That(hand.Value<string>("type"), Is.EqualTo("left-hand"));
            Assert.That(hand["root_positions"][0][0].Value<float>(), Is.EqualTo(4f));
            Assert.That(hand["root_positions"][0][1].Value<float>(), Is.EqualTo(1.25f));
            Assert.That(hand["root_positions"][0][2].Value<float>(), Is.EqualTo(7f));
            Assert.That(hand["smooth_root_2d"][0][0].Value<float>(), Is.EqualTo(4f));
            Assert.That(hand["smooth_root_2d"][0][1].Value<float>(), Is.EqualTo(7f));
            Vector3 rootAxisAngle = ReadVector3(hand["local_joints_rot"][0][0]);
            Quaternion rootRotation = FromKimodoRotation(
                KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(rootAxisAngle));
            Assert.That(Quaternion.Angle(rootRotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(1e-3f));
            Assert.That(hand["target_positions"][0][0].Value<float>(), Is.EqualTo(9f));
            Assert.That(hand["target_positions"][0][1].Value<float>(), Is.EqualTo(8f));
            Assert.That(hand["target_positions"][0][2].Value<float>(), Is.EqualTo(7f));
        }

        private static JArray Heading(float unityYawDegrees)
        {
            float radians = unityYawDegrees * Mathf.Deg2Rad;
            return new JArray(Mathf.Cos(radians), -Mathf.Sin(radians));
        }

        private static Vector3 ReadVector3(JToken value)
        {
            return new Vector3(value[0].Value<float>(), value[1].Value<float>(), value[2].Value<float>());
        }

        private static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        private static Quaternion ToKimodoRotation(Quaternion unityRotation)
        {
            return new Quaternion(unityRotation.x, -unityRotation.y, -unityRotation.z, unityRotation.w);
        }

        private static Quaternion FromKimodoRotation(Quaternion kimodoRotation)
        {
            return new Quaternion(kimodoRotation.x, -kimodoRotation.y, -kimodoRotation.z, kimodoRotation.w);
        }
    }
}
