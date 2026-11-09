using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRuntimeEffectorConstraintTests
    {
        [Test]
        public void BuildEndEffectorFromProjectedPose_ExportsProfilePoseWithoutProjector()
        {
            var projected = new KimodoConstraintProjectedPose
            {
                profileRootPosition = new Vector3(1f, 2f, 3f),
                jointNames = new[] { "Hips", "RightHand" },
                jointPositions = new[]
                {
                    new Vector3(1f, 2f, 3f),
                    new Vector3(4f, 5f, 6f)
                },
                jointRotations = new[] { Quaternion.identity, Quaternion.identity },
                localJointAngles = new List<Vector3> { Vector3.zero, Vector3.up }
            };

            KimodoConstraintJson json = KimodoConstraintJsonExporter.BuildEndEffectorFromProjectedPose(
                projected,
                "right-hand",
                1.0,
                0.0,
                null,
                30.0);
            JObject payload = JObject.Parse(JsonConvert.SerializeObject(
                json,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

            Assert.That(payload["type"]?.Value<string>(), Is.EqualTo("right-hand"));
            Assert.That(payload["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(30));
            AssertVector(payload["root_positions"]?[0], new Vector3(-1f, 2f, 3f));
            AssertVector(payload["target_positions"]?[0], new Vector3(-4f, 5f, 6f));
            Assert.That(((JArray)payload["local_joints_rot"]?[0]).Count, Is.EqualTo(2));
        }

        [Test]
        public void KimodoEncoder_UsesSharedAbsoluteConstraintSnapshotAndMapsSegmentTime()
        {
            var constraints = new KimodoRuntimeConstraints();
            float[] sourceTarget = { 1f, 2f, 3f };
            constraints.StageEffector(new KimodoRuntimeEffectorConstraint
            {
                sampleTime = 2.0,
                json = new KimodoConstraintJson
                {
                    type = "right-hand",
                    frame_indices = new List<int> { 0 },
                    target_positions = new List<float[]> { sourceTarget }
                }
            });
            Assert.That(constraints.Commit(), Is.True);

            KimodoRuntimeConstraintSnapshot snapshot = constraints.CaptureSnapshot();
            string encoded = KimodoRuntimeConstraintEncoder.BuildKimodo(snapshot, 3f, null);
            JObject result = (JObject)JArray.Parse(encoded)[0];

            Assert.That(snapshot.Effectors, Has.Count.EqualTo(1));
            Assert.That(snapshot.Effectors[0].sampleTime, Is.EqualTo(2.0));
            Assert.That(snapshot.Effectors[0].json.target_positions[0], Is.Not.SameAs(sourceTarget));
            Assert.That(result["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(60));
            Assert.That(result["target_positions"]?[0]?[2]?.Value<float>(), Is.EqualTo(3f));
        }

        [Test]
        public void ArdyEncoder_MapsTheSameSnapshotFromPlaybackCursor()
        {
            var constraints = new KimodoRuntimeConstraints();
            constraints.StageEffector(new KimodoRuntimeEffectorConstraint
            {
                sampleTime = 11.0,
                json = new KimodoConstraintJson
                {
                    type = "right-hand",
                    frame_indices = new List<int> { 0 }
                }
            });
            Assert.That(constraints.Commit(), Is.True);

            KimodoRuntimeConstraintSnapshot snapshot = constraints.CaptureSnapshot();
            string encoded = KimodoRuntimeConstraintEncoder.BuildArdy(snapshot, 10.0, 3f, 30f);
            JObject result = (JObject)JArray.Parse(encoded)[0];

            Assert.That(snapshot.Effectors[0].sampleTime, Is.EqualTo(11.0));
            Assert.That(result["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(30));
        }

        [Test]
        public void Solver_UsesSourceAvatarIkThenBuildsProfileEffectorRaw()
        {
            const string armaturePath =
                "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/Armature.fbx";
            GameObject asset = AssetDatabase.LoadMainAssetAtPath(armaturePath) as GameObject;
            Assert.That(asset, Is.Not.Null, $"Missing humanoid test Avatar at {armaturePath}.");
            Animator assetAnimator = asset.GetComponentInChildren<Animator>(true);
            Assert.That(assetAnimator, Is.Not.Null);
            Assert.That(KimodoRetargetCoreUtility.IsValidHumanoid(assetAnimator.avatar), Is.True);

            GameObject instance = Object.Instantiate(asset);
            var solver = new KimodoRuntimeEffectorConstraintSolver();
            try
            {
                Animator source = instance.GetComponentInChildren<Animator>(true);
                source.runtimeAnimatorController = null;
                source.avatar = assetAnimator.avatar;
                source.Rebind();
                source.Update(0f);
                Assert.That(source.GetBoneTransform(HumanBodyBones.RightHand), Is.Not.Null);

                Vector3 target = source.GetBoneTransform(HumanBodyBones.RightHand).position +
                    Vector3.forward * 0.05f;
                bool built = solver.TryBuild(
                    source,
                    KimodoMotionModelProfiles.DefaultModelName,
                    HumanBodyBones.RightHand,
                    target,
                    Quaternion.identity,
                    false,
                    1.0,
                    out KimodoRuntimeEffectorConstraint result,
                    out string error);

                Assert.That(built, Is.True, error);
                Assert.That(result?.json?.type, Is.EqualTo("right-hand"));
                Assert.That(result.json.frame_indices, Is.EqualTo(new[] { 30 }));
                Assert.That(result.json.root_positions, Has.Count.EqualTo(1));
                Assert.That(result.json.local_joints_rot, Has.Count.EqualTo(1));
                Assert.That(result.json.target_positions, Has.Count.EqualTo(1));
                Assert.That(result.json.target_positions[0], Has.Length.EqualTo(3));
            }
            finally
            {
                solver.Dispose();
                Object.DestroyImmediate(instance);
            }
        }

        private static void AssertVector(JToken token, Vector3 expected)
        {
            Assert.That(token, Is.TypeOf<JArray>());
            Assert.That(token[0].Value<float>(), Is.EqualTo(expected.x).Within(1e-5f));
            Assert.That(token[1].Value<float>(), Is.EqualTo(expected.y).Within(1e-5f));
            Assert.That(token[2].Value<float>(), Is.EqualTo(expected.z).Within(1e-5f));
        }
    }
}
