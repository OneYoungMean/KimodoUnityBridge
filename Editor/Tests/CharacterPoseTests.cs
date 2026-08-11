using System;
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

        private static void AssertTransform(CharacterPoseTransform actual, Vector3 expectedT, Quaternion expectedQ)
        {
            Assert.That(Vector3.Distance(actual.t, expectedT), Is.LessThan(1e-6f));
            Assert.That(Quaternion.Angle(actual.q, expectedQ), Is.LessThan(1e-4f));
        }
    }
}
