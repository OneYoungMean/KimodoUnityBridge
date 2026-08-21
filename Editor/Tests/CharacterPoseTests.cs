#if false
using System;
using System.Collections.Generic;
using System.Linq;
using CharacterAnimationCli.Unity;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace CharacterAnimationCli.Unity.Editor.Tests
{
    public sealed class CharacterPoseTests
    {
        private static readonly int[] ExpectedUnityMuscleIndices =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
            21, 22, 23, 24, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36,
            37, 38, 39, 40, 41, 42, 43, 44, 45,
            46, 47, 48, 49, 50, 51, 52, 53, 54
        };

        [Test]
        public void MuscleSampleRoundTrip_Uses49BodyMusclesAndNativeTqChannels()
        {
            var unityMuscles = new float[HumanTrait.MuscleCount];
            for (int i = 0; i < unityMuscles.Length; i++)
            {
                unityMuscles[i] = i + 0.25f;
            }

            var source = new KimodoBridge.MuscleSample
            {
                pose = new HumanPose
                {
                    bodyPosition = new Vector3(1f, 2f, 3f),
                    bodyRotation = Quaternion.Euler(10f, 20f, 30f),
                    muscles = unityMuscles
                },
                leftHandPosition = new Vector3(4f, 5f, 6f),
                leftHandRotation = Quaternion.Euler(1f, 2f, 3f),
                rightHandPosition = new Vector3(7f, 8f, 9f),
                rightHandRotation = Quaternion.Euler(4f, 5f, 6f),
                leftFootPosition = new Vector3(10f, 11f, 12f),
                leftFootRotation = Quaternion.Euler(7f, 8f, 9f),
                rightFootPosition = new Vector3(13f, 14f, 15f),
                rightFootRotation = Quaternion.Euler(10f, 11f, 12f)
            };

            CharacterPose pose = CharacterPoseMuscleAdapter.FromMuscleSample(source);

            Assert.That(pose.muscles, Has.Length.EqualTo(CharacterPose.MuscleCount));
            for (int i = 0; i < ExpectedUnityMuscleIndices.Length; i++)
            {
                Assert.That(pose.muscles[i], Is.EqualTo(unityMuscles[ExpectedUnityMuscleIndices[i]]));
            }
            AssertTransform(pose.root, source.pose.bodyPosition, source.pose.bodyRotation);
            AssertTransform(pose.hands.left, source.leftHandPosition, source.leftHandRotation);
            AssertTransform(pose.hands.right, source.rightHandPosition, source.rightHandRotation);
            AssertTransform(pose.feet.left, source.leftFootPosition, source.leftFootRotation);
            AssertTransform(pose.feet.right, source.rightFootPosition, source.rightFootRotation);

            KimodoBridge.MuscleSample roundTrip = CharacterPoseMuscleAdapter.ToMuscleSample(pose);
            for (int i = 0; i < ExpectedUnityMuscleIndices.Length; i++)
            {
                int unityIndex = ExpectedUnityMuscleIndices[i];
                Assert.That(roundTrip.pose.muscles[unityIndex], Is.EqualTo(unityMuscles[unityIndex]));
            }
        }

        [Test]
        public void JsonShape_IsNestedAndContainsNoLocatorMetadata()
        {
            CharacterPose pose = CharacterPoseMuscleAdapter.FromMuscleSample(new KimodoBridge.MuscleSample
            {
                pose = new HumanPose
                {
                    bodyRotation = Quaternion.identity,
                    muscles = new float[HumanTrait.MuscleCount]
                },
                leftHandRotation = Quaternion.identity,
                rightHandRotation = Quaternion.identity,
                leftFootRotation = Quaternion.identity,
                rightFootRotation = Quaternion.identity
            });

            JObject json = CharacterPoseJson.ToJson(pose);

            Assert.That(json["muscles"], Is.TypeOf<JArray>());
            Assert.That(json["root"]?["t"], Is.TypeOf<JArray>());
            Assert.That(json["root"]?["q"], Is.TypeOf<JArray>());
            Assert.That(json["hands"]?["left"]?["t"], Is.TypeOf<JArray>());
            Assert.That(json["feet"]?["right"]?["q"], Is.TypeOf<JArray>());
            Assert.That(json["source"], Is.Null);
            Assert.That(json["frame"], Is.Null);

            CharacterPose parsed = CharacterPoseJson.Parse(json);
            AssertTransform(parsed.root, pose.root.t, pose.root.q);
        }

        [Test]
        public void Validation_PreservesFiniteMusclesOutsideUnitRange()
        {
            var pose = new CharacterPose();
            pose.muscles[0] = 2.5f;

            Assert.That(pose.TryValidate(out string error), Is.True, error);
            Assert.That(CharacterPoseMuscleAdapter.ToMuscleSample(pose).pose.muscles[0], Is.EqualTo(2.5f));
        }

        [Test]
        public void ApplyPatch_ChangesOnlyProvidedChannels()
        {
            var pose = new CharacterPose();
            pose.root.t = new Vector3(1f, 2f, 3f);

            CharacterPose patched = CharacterPoseJson.ApplyPatch(
                pose,
                JObject.Parse("{\"hands\":{\"right\":{\"t\":[4,5,6]}}}"));

            Assert.That(patched.root.t, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(patched.hands.right.t, Is.EqualTo(new Vector3(4f, 5f, 6f)));
            Assert.That(pose.hands.right.t, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void ApplyPatch_RejectsUnknownLegacyFields()
        {
            Assert.Throws<InvalidOperationException>(() => CharacterPoseJson.ApplyPatch(
                new CharacterPose(),
                JObject.Parse("{\"root\":{\"rotation_y\":30}}")));
        }

        [Test]
        public void MarkerClone_PreservesCanonicalPoseWithoutSharingIt()
        {
            var sample = new KimodoMarkerSampleResult { characterPose = new CharacterPose() };

            KimodoMarkerSampleResult clone = sample.Clone();
            clone.characterPose.muscles[0] = 0.75f;

            Assert.That(sample.characterPose.muscles[0], Is.Zero);
            Assert.That(clone.characterPose.muscles[0], Is.EqualTo(0.75f));
        }

        [Test]
        public void MarkerClone_PreservesConstraintType()
        {
            var sample = new KimodoMarkerSampleResult { constraintType = "left-hand" };

            Assert.That(sample.Clone().constraintType, Is.EqualTo("left-hand"));
        }

        [Test]
        public void SameFrameComposition_UsesFullBodyThenRoot2DAndEndEffectors()
        {
            var fullBody = new CharacterPose();
            fullBody.muscles[0] = 0.5f;
            var root = new CharacterPose();
            root.root.t = new Vector3(1f, 2f, 3f);
            var leftHand = new CharacterPose();
            leftHand.hands.left.t = new Vector3(4f, 5f, 6f);
            var rightFoot = new CharacterPose();
            rightFoot.feet.right.t = new Vector3(7f, 8f, 9f);
            var samples = new[]
            {
                new KimodoMarkerSampleResult { constraintType = "left_hand", sampleTime = 1.0, characterPose = leftHand },
                new KimodoMarkerSampleResult { constraintType = "fullbody", sampleTime = 1.0, characterPose = fullBody },
                new KimodoMarkerSampleResult { constraintType = "root2d", sampleTime = 1.0, characterPose = root },
                new KimodoMarkerSampleResult { constraintType = "right-foot", sampleTime = 1.0, characterPose = rightFoot }
            };

            KimodoMarkerSamplingUtility.ComposeCharacterPosesAtSameFrame(samples, 60.0);

            Assert.That(samples[0].characterPose.muscles[0], Is.EqualTo(0.5f));
            Assert.That(samples[0].characterPose.root.t, Is.EqualTo(new Vector3(1f, 0f, 3f)));
            Assert.That(samples[0].characterPose.hands.left.t, Is.Not.EqualTo(leftHand.hands.left.t));
            Assert.That(samples[0].characterPose.feet.right.t, Is.Not.EqualTo(rightFoot.feet.right.t));
            Assert.That(samples[1].characterPose.root.t, Is.EqualTo(new Vector3(1f, 0f, 3f)));
        }

        [Test]
        public void UnifiedConstraint_ExpandsToSharedProtocolPoseAndEndEffectorTarget()
        {
            var pose = new CharacterPose();
            pose.root.t = new Vector3(1f, 2f, 3f);
            pose.root.q = Quaternion.Euler(0f, 90f, 0f);
            pose.hands.left.t = new Vector3(2f, 0f, 0f);
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                sampleTime = 1.0,
                characterPose = pose,
                mask = new KimodoConstraintMask { muscle = true, rootPosition = true, rootHeading = true, leftHand = true }
            };

            JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(new[] { sample }, new KimodoConstraintExportContext(), exportFps: 30.0));
            JObject fullBody = (JObject)json.First(item => item.Value<string>("type") == "fullbody");
            JObject endEffector = (JObject)json.First(item => item.Value<string>("type") == "left-hand");
            JObject root2d = (JObject)json.First(item => item.Value<string>("type") == "root2d");

            Assert.That(endEffector["root_positions"]?[0]?[0]?.Value<float>(), Is.EqualTo(fullBody["root_positions"]?[0]?[0]?.Value<float>()));
            Assert.That(endEffector["local_joints_rot"]?[0]?.ToString(), Is.EqualTo(fullBody["local_joints_rot"]?[0]?.ToString()));
            Assert.That(endEffector["target_positions"]?[0]?[0]?.Value<float>(), Is.EqualTo(-1f).Within(1e-5f));
            Assert.That(endEffector["target_positions"]?[0]?[2]?.Value<float>(), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(root2d["smooth_root_2d"]?[0]?[0]?.Value<float>(), Is.EqualTo(-1f));
        }

        [Test]
        public void ConstraintExporter_UsesCompleteProjectedJointFrame()
        {
            var pose = BuildPose(1f);
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                characterPose = pose,
                sampleTime = 0.0
            };
            var context = new KimodoConstraintExportContext(
                1f,
                _ => Enumerable.Repeat(Vector3.zero, 30).ToList());

            JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { sample },
                context,
                exportFps: 30.0));

            JObject fullBody = (JObject)json.Single(item => item.Value<string>("type") == "fullbody");
            Assert.That(fullBody["local_joints_rot"]?[0], Has.Count.EqualTo(30));
        }

        [Test]
        [Ignore("RawData was removed from SampleResult; rewrite against sampleData protocol.")]
        public void ConstraintExporter_UsesRawAxisAngleWithoutMuscleProjection()
        {
            Quaternion unityRotation = Quaternion.Euler(0f, 30f, 0f);
            Vector3 unityAxisAngle = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(unityRotation);
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                characterPose = new CharacterPose(),
            };
            var context = new KimodoConstraintExportContext(
                2f,
                new Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose>(_ =>
                    throw new AssertionException("Raw FullBody export must not invoke the pose projector.")));

            JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { sample },
                context,
                exportFps: 30.0));

            JObject fullBody = (JObject)json.Single(item => item.Value<string>("type") == "fullbody");
            Assert.That(fullBody["root_positions"]?[0]?[0]?.Value<float>(), Is.EqualTo(-2f).Within(1e-5f));
            Assert.That(fullBody["root_positions"]?[0]?[1]?.Value<float>(), Is.EqualTo(4f).Within(1e-5f));
            Assert.That(fullBody["root_positions"]?[0]?[2]?.Value<float>(), Is.EqualTo(6f).Within(1e-5f));

            Quaternion kimodoRotation = new Quaternion(
                unityRotation.x,
                -unityRotation.y,
                -unityRotation.z,
                unityRotation.w);
            Vector3 expectedAxisAngle = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(kimodoRotation);
            Assert.That(fullBody["local_joints_rot"]?[0]?[0]?.Value<float>(), Is.EqualTo(expectedAxisAngle.x).Within(1e-5f));
            Assert.That(fullBody["local_joints_rot"]?[0]?[1]?.Value<float>(), Is.EqualTo(expectedAxisAngle.y).Within(1e-5f));
            Assert.That(fullBody["local_joints_rot"]?[0]?[2]?.Value<float>(), Is.EqualTo(expectedAxisAngle.z).Within(1e-5f));
        }

        [Test]
        public void UnifiedConstraint_MergedEndEffectorTargetsKeepEveryFrame()
        {
            KimodoMarkerSampleResult At(double time, float x) => new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                sampleTime = time,
                characterPose = BuildPose(x),
                mask = new KimodoConstraintMask { leftHand = true }
            };

            JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { At(0.0, 1f), At(1.0, 2f) },
                new KimodoConstraintExportContext(),
                exportFps: 30.0));
            JObject hand = (JObject)json.Single(item => item.Value<string>("type") == "left-hand");

            Assert.That(hand["frame_indices"], Has.Count.EqualTo(2));
            Assert.That(hand["target_positions"], Has.Count.EqualTo(2));
            Assert.That(hand["target_positions"]?[0]?[0]?.Value<float>(), Is.EqualTo(-1f).Within(1e-5f));
            Assert.That(hand["target_positions"]?[1]?[0]?.Value<float>(), Is.EqualTo(-2f).Within(1e-5f));
        }

        [Test]
        public void UnifiedConstraint_ProtocolPositionsApplyHumanScaleOnce()
        {
            CharacterPose pose = BuildPose(1f);
            pose.root.t = new Vector3(2f, 3f, 4f);
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                characterPose = pose,
                mask = new KimodoConstraintMask { muscle = true, rootPosition = true, leftHand = true }
            };

            JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(new[] { sample }, new KimodoConstraintExportContext(2f)));
            JObject body = (JObject)json.Single(item => item.Value<string>("type") == "fullbody");
            JObject hand = (JObject)json.Single(item => item.Value<string>("type") == "left-hand");
            JObject root = (JObject)json.Single(item => item.Value<string>("type") == "root2d");

            Assert.That(body["root_positions"]?[0]?[0]?.Value<float>(), Is.EqualTo(-4f).Within(1e-5f));
            Assert.That(root["smooth_root_2d"]?[0]?[1]?.Value<float>(), Is.EqualTo(8f).Within(1e-5f));
            Assert.That(hand["target_positions"]?[0]?[0]?.Value<float>(), Is.EqualTo(-6f).Within(1e-5f));
        }

        [Test]
        public void UnifiedRoot2D_ChangesOnlyCanonicalRootWithoutRoundTripDrift()
        {
            var pose = new CharacterPose();
            pose.root.t = new Vector3(3f, 4f, 5f);
            pose.root.q = Quaternion.Euler(0f, 45f, 0f);
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                characterPose = pose,
                mask = new KimodoConstraintMask { rootPosition = true, rootHeading = true }
            };

            KimodoMarkerSamplingUtility.ComposeCharacterPosesAtSameFrame(new[] { sample, sample.Clone() }, 60.0);

            Assert.That(sample.characterPose.root.t, Is.EqualTo(new Vector3(3f, 4f, 5f)));
            Assert.That(Quaternion.Angle(sample.characterPose.root.q, Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void SameFrameComposition_DoesNotOverwriteUnifiedConstraintAuthoredPose()
        {
            var authored = new CharacterPose();
            authored.root.t = new Vector3(1f, 2f, 3f);
            authored.root.q = Quaternion.Euler(10f, 20f, 30f);
            authored.hands.left.t = new Vector3(4f, 5f, 6f);
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                sampleTime = 1.0,
                characterPose = authored,
                hasRoot2DOverride = true,
                root2DOverride = new CharacterPoseTransform
                {
                    t = new Vector3(7f, 0f, 8f),
                    q = Quaternion.Euler(0f, 90f, 0f)
                },
                mask = new KimodoConstraintMask { muscle = true, rootPosition = true, rootHeading = true, leftHand = true }
            };

            KimodoMarkerSamplingUtility.ComposeCharacterPosesAtSameFrame(new[] { sample, sample.Clone() }, 60.0);

            Assert.That(sample.characterPose.root.t, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(Quaternion.Angle(sample.characterPose.root.q, Quaternion.Euler(10f, 20f, 30f)), Is.LessThan(1e-4f));
            Assert.That(sample.characterPose.hands.left.t, Is.EqualTo(new Vector3(4f, 5f, 6f)));
        }

        [Test]
        public void Root2DWithoutHeading_ChangesPositionButKeepsTheSolvedFkHeading()
        {
            var fullBody = new CharacterPose();
            fullBody.root.t = new Vector3(1f, 0f, 2f);
            fullBody.root.q = Quaternion.Euler(0f, 25f, 0f);
            var root2D = fullBody.Clone();
            root2D.root.t = new Vector3(8f, 0f, 9f);
            root2D.root.q = Quaternion.Euler(0f, 140f, 0f);
            var samples = new[]
            {
                new KimodoMarkerSampleResult { constraintType = "fullbody", sampleTime = 0.0, characterPose = fullBody },
                new KimodoMarkerSampleResult
                {
                    constraintType = "root2d",
                    sampleTime = 0.0,
                    characterPose = root2D,
                    hasRootHeading = false
                }
            };

            KimodoMarkerSamplingUtility.ComposeCharacterPosesAtSameFrame(samples, 60.0);

            Assert.That(samples[0].characterPose.root.t, Is.EqualTo(new Vector3(8f, 0f, 9f)));
            Assert.That(Quaternion.Angle(samples[0].characterPose.root.q, fullBody.root.q), Is.LessThan(1e-4f));

            var merged = KimodoMarkerSamplingUtility.MergeAsUnifiedConstraintSamples(samples, 60.0);
            Assert.That(merged[0].hasRootHeading, Is.False);
            Assert.That(merged[0].hasRoot2DOverride, Is.True);
        }

        [Test]
        public void UnifiedRoot2D_PreservesFullBodyVerticalAndTiltAndKeepsGoalsInWorldSpace()
        {
            var pose = new CharacterPose();
            pose.root.t = new Vector3(1f, 2f, 3f);
            pose.root.q = Quaternion.Euler(20f, 30f, 10f);
            pose.hands.left.t = new Vector3(2f, 1f, -1f);
            pose.hands.left.q = Quaternion.Euler(5f, 10f, 15f);
            Vector3 worldPosition = pose.root.t + pose.root.q * pose.hands.left.t;
            Quaternion worldRotation = pose.root.q * pose.hands.left.q;
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                characterPose = pose,
                hasRoot2DOverride = true,
                root2DOverride = new CharacterPoseTransform
                {
                    t = new Vector3(8f, 99f, 9f),
                    q = Quaternion.Euler(0f, 140f, 0f)
                },
                mask = new KimodoConstraintMask { muscle = true, rootPosition = true, rootHeading = true, leftHand = true }
            };

            KimodoMarkerSampleResult resolved = KimodoConstraintSampleComposer.ResolveUnifiedSample(sample);
            Assert.That(resolved.characterPose.root.t, Is.EqualTo(new Vector3(8f, 2f, 9f)));
            Vector3 forward = resolved.characterPose.root.q * Vector3.forward;
            Assert.That(Vector3.Angle(Vector3.ProjectOnPlane(forward, Vector3.up), Quaternion.Euler(0f, 140f, 0f) * Vector3.forward), Is.LessThan(1e-4f));
            Vector3 resolvedWorldPosition = resolved.characterPose.root.t + resolved.characterPose.root.q * resolved.characterPose.hands.left.t;
            Quaternion resolvedWorldRotation = resolved.characterPose.root.q * resolved.characterPose.hands.left.q;
            Assert.That(Vector3.Distance(resolvedWorldPosition, worldPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(resolvedWorldRotation, worldRotation), Is.LessThan(1e-4f));
        }

        [Test]
        public void UnifiedRoot2D_ComposesPlanarOverrideAndOnlyEnabledEffectorsLockWorld()
        {
            var pose = new CharacterPose();
            pose.root.t = new Vector3(3f, 2f, 5f);
            pose.root.q = Quaternion.Euler(20f, 30f, 10f);
            pose.hands.left.t = new Vector3(2f, 1f, -1f);
            pose.hands.right.t = new Vector3(-2f, 1f, -1f);
            Vector3 leftWorld = pose.root.t + pose.root.q * pose.hands.left.t;
            Vector3 rightWorld = pose.root.t + pose.root.q * pose.hands.right.t;
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                characterPose = pose,
                hasRoot2DOverride = true,
                root2DOverride = new CharacterPoseTransform
                {
                    t = new Vector3(8f, 99f, 9f),
                    q = Quaternion.Euler(0f, 140f, 0f)
                },
                mask = new KimodoConstraintMask { muscle = true, rootPosition = true, rootHeading = true, leftHand = true }
            };

            KimodoMarkerSampleResult resolved = KimodoConstraintSampleComposer.ResolveUnifiedSample(sample);

            Assert.That(resolved.characterPose.root.t, Is.EqualTo(new Vector3(8f, 2f, 9f)));
            Assert.That(Quaternion.Angle(sample.characterPose.root.q, Quaternion.Euler(20f, 30f, 10f)), Is.LessThan(1e-4f));
            Vector3 resolvedLeftWorld = resolved.characterPose.root.t + resolved.characterPose.root.q * resolved.characterPose.hands.left.t;
            Vector3 resolvedRightWorld = resolved.characterPose.root.t + resolved.characterPose.root.q * resolved.characterPose.hands.right.t;
            Assert.That(Vector3.Distance(resolvedLeftWorld, leftWorld), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(resolvedRightWorld, rightWorld), Is.GreaterThan(1e-3f));
        }

        [Test]
        public void UnifiedMerge_ProducesOneMarkerPerFrameWithAllEnabledChannels()
        {
            var fullBody = new CharacterPose();
            fullBody.muscles[0] = 0.25f;
            var leftHand = fullBody.Clone();
            leftHand.hands.left.t = new Vector3(1f, 2f, 3f);
            var root = fullBody.Clone();
            root.root.t = new Vector3(4f, 0f, 5f);

            var merged = KimodoMarkerSamplingUtility.MergeAsUnifiedConstraintSamples(
                new[]
                {
                    new KimodoMarkerSampleResult { constraintType = "root2d", sampleTime = 0.0, characterPose = root },
                    new KimodoMarkerSampleResult { constraintType = "left-hand", sampleTime = 0.0, characterPose = leftHand },
                    new KimodoMarkerSampleResult
                    {
                        constraintType = "fullbody",
                        sampleTime = 0.0,
                        characterPose = fullBody,
                    }
                },
                60.0);

            Assert.That(merged, Has.Count.EqualTo(1));
            Assert.That(merged[0].constraintType, Is.EqualTo("constraint"));
            Assert.That(merged[0].mask.muscle && merged[0].mask.leftHand && merged[0].mask.rootPosition, Is.True);
            Assert.That(merged[0].characterPose.hands.left.t, Is.EqualTo(leftHand.hands.left.t));
            Assert.That(merged[0].characterPose.root.t, Is.EqualTo(fullBody.root.t));
            Assert.That(merged[0].root2DOverride.t, Is.EqualTo(new Vector3(4f, 0f, 5f)));
            Assert.That(merged[0].hasRoot2DOverride, Is.True);
            KimodoMarkerSampleResult resolved = KimodoConstraintSampleComposer.ResolveUnifiedSample(merged[0]);
            Vector3 expectedWorldHand = fullBody.root.t + fullBody.root.q * leftHand.hands.left.t;
            Vector3 resolvedWorldHand = resolved.characterPose.root.t +
                resolved.characterPose.root.q * resolved.characterPose.hands.left.t;
            Assert.That(Vector3.Distance(resolvedWorldHand, expectedWorldHand), Is.LessThan(1e-5f));
            Assert.That(merged[0].characterPose.muscles, Has.Length.EqualTo(CharacterPose.MuscleCount));
        }

        [Test]
        public void UnifiedMask_ExplicitEmptyMaskStaysEmpty()
        {
            var empty = new KimodoConstraintMask();

            Assert.That(KimodoConstraintMask.Resolve(empty, "constraint"), Is.SameAs(empty));
            Assert.That(KimodoConstraintMask.Resolve(empty, "constraint").IsEmpty, Is.True);
            Assert.That(KimodoConstraintMask.Resolve(null, "constraint").muscle, Is.True);
        }

        [Test]
        public void SampledUnifiedMarker_KeepsItsAuthoredMaskAndHeadingSwitch()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = true;
                marker.SampleData = new KimodoMarkerSampleResult
                {
                    constraintType = "constraint",
                    hasRootHeading = false,
                    mask = new KimodoConstraintMask { leftHand = true, rootHeading = true }
                };
                var captured = new KimodoMarkerSampleResult
                {
                    constraintType = "fullbody",
                    characterPose = new CharacterPose(),
                    mask = KimodoConstraintMask.ForType("fullbody"),
                    hasRootHeading = true
                };

                KimodoMarkerSampleResult normalized = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(
                    marker,
                    captured);

                Assert.That(normalized.constraintType, Is.EqualTo("constraint"));
                Assert.That(normalized.mask.leftHand, Is.True);
                Assert.That(normalized.mask.muscle, Is.False);
                Assert.That(normalized.hasRootHeading, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ConstraintWriteback_PromotesDraggedIkChannelsIntoAuthoredMask()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.SampleData = new KimodoMarkerSampleResult
                {
                    constraintType = "constraint",
                    characterPose = new CharacterPose(),
                    mask = new KimodoConstraintMask()
                };
                KimodoMarkerSampleResult dragged = marker.SampleData.Clone();
                dragged.mask.leftHand = true;
                dragged.mask.rightFoot = true;
                dragged.characterPose.hands.left.t = new Vector3(1f, 2f, 3f);
                dragged.characterPose.feet.right.t = new Vector3(4f, 5f, 6f);

                Assert.That(
                    KimodoBridge.Editor.KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker,
                        dragged,
                        out string error,
                        writeSampledCharacterPose: true),
                    Is.True,
                    error);
                Assert.That(marker.SampleData.mask.leftHand, Is.True);
                Assert.That(marker.SampleData.mask.rightFoot, Is.True);
                Assert.That(marker.SampleData.characterPose.hands.left.t, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                List<KimodoMarkerSampleResult> samples = KimodoConstraintSampleComposer.ExpandProtocolSamples(
                    new[] { marker.SampleData },
                    30.0);
                Assert.That(
                    samples.Any(sample =>
                        sample.constraintType == "left-hand" &&
                        sample.characterPose.hands.left.t == new Vector3(1f, 2f, 3f)),
                    Is.True);
                Assert.That(
                    samples.Any(sample =>
                        sample.constraintType == "right-foot" &&
                        sample.characterPose.feet.right.t == new Vector3(4f, 5f, 6f)),
                    Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void AutoSampleFullBody_KeepsCommandRoot2DOverrideAndHandAuthored()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                var authored = new CharacterPose();
                authored.muscles[0] = 2f;
                authored.root.t = new Vector3(1f, 2f, 3f);
                authored.hands.left.t = new Vector3(4f, 5f, 6f);
                authored.hands.right.t = new Vector3(-4f, -5f, -6f);
                authored.feet.left.t = new Vector3(14f, 15f, 16f);
                authored.feet.right.t = new Vector3(-14f, -15f, -16f);
                marker.SampleData = new KimodoMarkerSampleResult
                {
                    characterPose = authored,
                    hasRoot2DOverride = true,
                    root2DOverride = new CharacterPoseTransform
                    {
                        t = new Vector3(7f, 0f, 8f),
                        q = Quaternion.Euler(0f, 30f, 0f)
                    },
                    mask = new KimodoConstraintMask { muscle = true, rootPosition = true, rootHeading = true, leftHand = true }
                };
                var sampledPose = new CharacterPose();
                sampledPose.muscles[0] = 9f;
                sampledPose.root.t = new Vector3(10f, 11f, 12f);
                sampledPose.root.q = Quaternion.Euler(0f, 90f, 0f);
                sampledPose.hands.left.t = new Vector3(40f, 50f, 60f);
                sampledPose.hands.right.t = new Vector3(41f, 51f, 61f);
                sampledPose.feet.left.t = new Vector3(42f, 52f, 62f);
                sampledPose.feet.right.t = new Vector3(43f, 53f, 63f);
                var sampled = new KimodoMarkerSampleResult { characterPose = sampledPose };

                marker.autoSample = false;
                KimodoMarkerSampleResult root2dOnly = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, sampled);
                Assert.That(root2dOnly.characterPose.muscles[0], Is.EqualTo(2f));
                Assert.That(root2dOnly.characterPose.root.t, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(root2dOnly.root2DOverride.t, Is.EqualTo(new Vector3(7f, 0f, 8f)));
                Assert.That(root2dOnly.characterPose.hands.left.t, Is.EqualTo(new Vector3(4f, 5f, 6f)));
                Assert.That(root2dOnly.characterPose.hands.right.t, Is.EqualTo(new Vector3(-4f, -5f, -6f)));

                marker.autoSample = true;
                KimodoMarkerSampleResult fullBodyOnly = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, sampled);
                Assert.That(fullBodyOnly.characterPose.muscles[0], Is.EqualTo(9f));
                Assert.That(fullBodyOnly.characterPose.root.t, Is.EqualTo(new Vector3(10f, 11f, 12f)));
                Assert.That(fullBodyOnly.root2DOverride.t, Is.EqualTo(new Vector3(7f, 0f, 8f)));
                Assert.That(fullBodyOnly.characterPose.hands.left.t, Is.EqualTo(new Vector3(4f, 5f, 6f)));
                Assert.That(fullBodyOnly.characterPose.hands.right.t, Is.EqualTo(new Vector3(41f, 51f, 61f)));
                Assert.That(fullBodyOnly.characterPose.feet.left.t, Is.EqualTo(new Vector3(42f, 52f, 62f)));
                Assert.That(fullBodyOnly.characterPose.feet.right.t, Is.EqualTo(new Vector3(43f, 53f, 63f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void MuscleEulerUi_UsesTheDistinctBonesBehindThe49Muscles()
        {
            HumanBodyBones[] bones = KimodoBridge.Editor.KimodoConstraintOverrideEditWindow.BuildMuscleEulerBones();

            Assert.That(bones, Is.Not.Empty);
            Assert.That(bones.Distinct().Count(), Is.EqualTo(bones.Length));
            CollectionAssert.DoesNotContain(bones, HumanBodyBones.Hips);
        }

        private static void AssertTransform(CharacterPoseTransform actual, Vector3 expectedT, Quaternion expectedQ)
        {
            Assert.That(Vector3.Distance(actual.t, expectedT), Is.LessThan(1e-6f));
            Assert.That(Quaternion.Angle(actual.q, expectedQ), Is.LessThan(1e-4f));
        }

        private static CharacterPose BuildPose(float leftHandX)
        {
            var pose = new CharacterPose();
            pose.hands.left.t = new Vector3(leftHandX, 0f, 0f);
            return pose;
        }
    }
}

#endif
