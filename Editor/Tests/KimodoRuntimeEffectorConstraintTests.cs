using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using KimodoUnityBridge;
using TimelineInject;
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
        public void Solver_RetargetsSourceFkAndBuildsProfileEffectorRaw()
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

        [Test]
        public void Solver_ConvertsEndpointDeltaAndSolvesOnProfileSkeleton()
        {
            const string armaturePath =
                "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/Armature.fbx";
            GameObject asset = AssetDatabase.LoadMainAssetAtPath(armaturePath) as GameObject;
            Assert.That(asset, Is.Not.Null);
            Animator assetAnimator = asset.GetComponentInChildren<Animator>(true);
            Assert.That(assetAnimator, Is.Not.Null);

            GameObject instance = Object.Instantiate(asset);
            var solver = new KimodoRuntimeEffectorConstraintSolver();
            RetargetSkeleton sourceCache = null;
            RetargetSkeleton profileCache = null;
            HumanPoseHandler sourcePoseHandler = null;
            try
            {
                Animator source = instance.GetComponentInChildren<Animator>(true);
                source.runtimeAnimatorController = null;
                source.avatar = assetAnimator.avatar;
                source.transform.SetPositionAndRotation(
                    new Vector3(1.5f, 0.25f, -0.75f),
                    Quaternion.Euler(0f, 23f, 0f));
                source.Rebind();
                source.Update(0f);
                Transform sourceHand = source.GetBoneTransform(HumanBodyBones.RightHand);
                Assert.That(sourceHand, Is.Not.Null);

                Vector3 target = sourceHand.position + new Vector3(0.03f, -0.01f, 0.03f);
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

                Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    source.avatar,
                    "KimodoRuntimeEffectorTestSource",
                    out sourceCache,
                    out error), Is.True, error);
                Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoMotionModelProfiles.DefaultModelName,
                    out Avatar profileAvatar,
                    out error), Is.True, error);
                Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    profileAvatar,
                    "KimodoRuntimeEffectorTestProfile",
                    out profileCache,
                    out error), Is.True, error);

                sourcePoseHandler = new HumanPoseHandler(source.avatar, source.transform);
                var pose = new HumanPose();
                sourcePoseHandler.GetHumanPose(ref pose);
                sourceCache.root.transform.SetPositionAndRotation(
                    source.transform.position,
                    source.transform.rotation);
                sourceCache.root.transform.localScale = source.transform.lossyScale;
                sourceCache.animator.Rebind();
                sourceCache.animator.Update(0f);
                sourceCache.poseHandler.SetHumanPose(ref pose);
                sourceCache.animator.Update(0f);
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    sourceCache,
                    out MuscleSample sourceSample,
                    out error), Is.True, error);
                Assert.That(KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                    sourceSample,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(
                        KimodoMotionModelProfiles.DefaultModelName),
                    profileCache,
                    out _,
                    out MuscleSample profileBaselineSample,
                    out error), Is.True, error);

                Assert.That(KimodoRuntimeEffectorConstraintSolver.TryConvertEndpointDeltaGoal(
                    source.transform,
                    source.GetBoneTransform(HumanBodyBones.RightHand).position,
                    sourceCache.humanScale,
                    profileCache,
                    HumanBodyBones.RightHand,
                    target,
                    out Vector3 profileGoal,
                    out error), Is.True, error);
                Assert.That(profileCache.GetBonePose(
                    HumanBodyBones.Hips,
                    out Vector3 profileRootPosition,
                    out Quaternion profileRootRotation), Is.True);
                Assert.That(profileCache.GetBonePose(
                    HumanBodyBones.RightHand,
                    out Vector3 profileBaselineEnd,
                    out _), Is.True);
                var profileSample = new KimodoMarkerSampleResult
                {
                    sampleData = profileBaselineSample,
                    sampleValid = true,
                    enabled = true,
                    constraintMode = "effector",
                    enableMask = KimodoConstraintMask.ForType("right-hand"),
                    rootOverride = new KimodoRigidTransform
                    {
                        t = profileRootPosition,
                        q = profileRootRotation,
                        space = KimodoConstraintSpace.World
                    },
                    effectors = new KimodoConstraintEffectors()
                };
                profileSample.effectors.rightHand.t = profileGoal;
                profileSample.effectors.rightHand.enableRotation = false;
                profileSample.effectors.rightHand.space = KimodoConstraintSpace.World;
                Assert.That(KimodoConstraintPosePipeline.TryApplyCanonical(
                    profileSample,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(
                        KimodoMotionModelProfiles.DefaultModelName),
                    profileCache,
                    out _,
                    out _,
                    out error), Is.True, error);
                Assert.That(profileCache.GetBonePose(
                    HumanBodyBones.RightHand,
                    out Vector3 expectedProfileEnd,
                    out _), Is.True);
                Assert.That(Vector3.Distance(expectedProfileEnd, profileBaselineEnd), Is.GreaterThan(0.001f));

                float[] exportedTarget = result.json.target_positions[0];
                Vector3 exportedProfileEnd = new Vector3(
                    -exportedTarget[0],
                    exportedTarget[1],
                    exportedTarget[2]);
                Assert.That(Vector3.Distance(exportedProfileEnd, expectedProfileEnd), Is.LessThan(1e-4f));
                Assert.That(
                    Vector3.Distance(expectedProfileEnd, profileGoal),
                    Is.LessThan(0.01f),
                    $"Profile IK endpoint did not reach converted goal. goal={profileGoal}, end={expectedProfileEnd}");
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                profileCache?.Dispose();
                sourceCache?.Dispose();
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
