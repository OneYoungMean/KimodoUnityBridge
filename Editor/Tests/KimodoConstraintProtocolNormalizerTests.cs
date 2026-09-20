using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintProtocolNormalizerTests
    {
        [TestCase(false), TestCase(true), Category("BridgeRegression")]
        public void PayloadSerialization_FoldsRootOverrideAndPreservesOtherFrames(bool rootFirst)
        {
            Quaternion original = Quaternion.Euler(20f, 15f, 30f);
            Vector3 axis = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(ToKimodoRotation(original));
            var body = new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = new JArray(0, 2),
                ["root_positions"] = new JArray(new JArray(1f, 2f, 3f), new JArray(4f, 5f, 6f)),
                ["local_joints_rot"] = new JArray(
                    new JArray(new JArray(axis.x, axis.y, axis.z), new JArray(0.1f, 0.2f, 0.3f)),
                    new JArray(new JArray(0f, 0f, 0f)))
            };
            var root = new JObject
            {
                ["type"] = "root2d",
                ["frame_indices"] = new JArray(0, 1),
                ["smooth_root_2d"] = new JArray(new JArray(-8f, 9f), new JArray(-10f, 11f)),
                ["global_root_heading"] = new JArray(Heading(90f), Heading(120f))
            };
            var payload = new KimodoConstraintPayload
            {
                json = (rootFirst ? new JArray(root, body) : new JArray(body, root)).ToString()
            };
            string originalJson = payload.json;
            JArray result = JArray.Parse(payload.Serialize(KimodoMotionModelProfiles.DefaultModelName,
                new System.Collections.Generic.List<byte[]>()));
            JObject merged = result[rootFirst ? 1 : 0] as JObject;
            JObject remaining = result[rootFirst ? 0 : 1] as JObject;
            Assert.That(ReadVector3(merged["root_positions"][0]), Is.EqualTo(new Vector3(-8f, 2f, 9f)));
            Assert.That(JToken.DeepEquals(merged["root_positions"][1], body["root_positions"][1]), Is.True);
            Assert.That(JToken.DeepEquals(merged["local_joints_rot"][0][1], body["local_joints_rot"][0][1]), Is.True);
            Quaternion actual = FromKimodoRotation(KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(
                ReadVector3(merged["local_joints_rot"][0][0])));
            Quaternion expected = Quaternion.Euler(0f, 90f, 0f) * Quaternion.Inverse(ResolvePlanarRotation(original)) * original;
            Assert.That(Quaternion.Angle(actual, expected), Is.LessThan(0.05f));
            CollectionAssert.AreEqual(new[] { 1 }, remaining["frame_indices"].Values<int>());
            Assert.That((JArray)remaining["smooth_root_2d"], Has.Count.EqualTo(1));
            Assert.That((JArray)remaining["global_root_heading"], Has.Count.EqualTo(1));
            Assert.That(payload.json, Is.EqualTo(originalJson));
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
