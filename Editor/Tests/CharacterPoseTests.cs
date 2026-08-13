using System;
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
        public void SameFrameComposition_UsesFullBodyThenRootAndEndEffectors()
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
            Assert.That(samples[0].characterPose.root.t, Is.EqualTo(root.root.t));
            Assert.That(samples[0].characterPose.hands.left.t, Is.EqualTo(leftHand.hands.left.t));
            Assert.That(samples[0].characterPose.feet.right.t, Is.EqualTo(rightFoot.feet.right.t));
            Assert.That(samples[1].characterPose.root.t, Is.EqualTo(root.root.t));
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

            Assert.That(samples[0].characterPose.root.t, Is.EqualTo(root2D.root.t));
            Assert.That(Quaternion.Angle(samples[0].characterPose.root.q, fullBody.root.q), Is.LessThan(1e-4f));

            var merged = KimodoMarkerSamplingUtility.MergeAsUnifiedConstraintSamples(samples, 60.0);
            Assert.That(merged[0].hasRootHeading, Is.False);
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
            Assert.That(merged[0].characterPose.root.t, Is.EqualTo(root.root.t));
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
                marker.useOverride = false;
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
