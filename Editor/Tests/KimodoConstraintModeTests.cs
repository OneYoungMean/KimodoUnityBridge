using System.Linq;
using CharacterAnimationCli.Unity;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintModeTests
    {
        [Test]
        public void MarkerModesKeepPayloadsIndependent()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.ConstraintMode = KimodoConstraintMode.Root2D;
                marker.Root2DData.root.t = new Vector3(1f, 2f, 3f);
                marker.Root2DData.allowHeading = false;

                marker.ConstraintMode = KimodoConstraintMode.FullBody;
                marker.FullBodyData.pose.muscles[0] = 0.75f;
                marker.FullBodyData.pose.root.t = new Vector3(4f, 5f, 6f);

                marker.ConstraintMode = KimodoConstraintMode.IK;
                marker.IkData.leftHand = true;
                marker.IkData.ikTargets.hands.left.t = new Vector3(7f, 8f, 9f);

                Assert.That(marker.Root2DData.root.t, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(marker.Root2DData.allowHeading, Is.False);
                Assert.That(marker.FullBodyData.pose.muscles[0], Is.EqualTo(0.75f));
                Assert.That(marker.FullBodyData.pose.root.t, Is.EqualTo(new Vector3(4f, 5f, 6f)));
                Assert.That(marker.IkData.ikTargets.hands.left.t, Is.EqualTo(new Vector3(7f, 8f, 9f)));
            }
            finally
            {
                Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ModeAwareExportKeepsFamiliesSeparateAndScalesRoot2D()
        {
            var rootPose = new CharacterPose();
            rootPose.root.t = new Vector3(2f, 1f, 3f);
            rootPose.root.q = Quaternion.Euler(0f, 90f, 0f);
            var bodyPose = new CharacterPose();
            bodyPose.root.t = new Vector3(10f, 4f, 20f);
            bodyPose.root.q = Quaternion.Euler(20f, 30f, -10f);

            var samples = new[]
            {
                new KimodoMarkerSampleResult
                {
                    constraintMode = "root2d",
                    constraintType = "constraint",
                    sampleTime = 0.0,
                    characterPose = rootPose,
                    hasRootHeading = true,
                    mask = KimodoConstraintMask.ForType("root2d")
                },
                new KimodoMarkerSampleResult
                {
                    constraintMode = "fullbody",
                    constraintType = "constraint",
                    sampleTime = 0.0,
                    characterPose = bodyPose,
                    mask = KimodoConstraintMask.ForType("fullbody")
                }
            };

            JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                new KimodoConstraintExportContext(2f),
                exportFps: 30.0));

            Assert.That(json.Select(item => item.Value<string>("type")),
                Is.EquivalentTo(new[] { "root2d", "fullbody" }));
            JObject root = (JObject)json.Single(item => item.Value<string>("type") == "root2d");
            JObject body = (JObject)json.Single(item => item.Value<string>("type") == "fullbody");
            Assert.That(root["smooth_root_2d"][0][0].Value<float>(), Is.EqualTo(-4f).Within(1e-5f));
            Assert.That(root["smooth_root_2d"][0][1].Value<float>(), Is.EqualTo(6f).Within(1e-5f));
            Assert.That(body["root_positions"][0][0].Value<float>(), Is.EqualTo(-4f).Within(1e-5f));
            Assert.That(body["root_positions"][0][1].Value<float>(), Is.EqualTo(8f).Within(1e-5f));
            Assert.That(body["root_positions"][0][2].Value<float>(), Is.EqualTo(6f).Within(1e-5f));
        }

        [Test]
        public void ModeAwareIkExportDoesNotEmitFullBodyOrRoot2D()
        {
            var pose = new CharacterPose();
            pose.root.t = new Vector3(1f, 2f, 3f);
            pose.hands.left.t = new Vector3(0.4f, 1.2f, 0.8f);
            var sample = new KimodoMarkerSampleResult
            {
                constraintMode = "ik",
                constraintType = "constraint",
                characterPose = pose,
                mask = new KimodoConstraintMask { leftHand = true }
            };

            JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { sample },
                new KimodoConstraintExportContext(),
                exportFps: 30.0));

            Assert.That(json.Select(item => item.Value<string>("type")), Is.EqualTo(new[] { "left-hand" }));
            Assert.That(json[0]["target_positions"], Is.Not.Null);
        }

        [Test]
        public void FullBodyIkTargetEditDoesNotChangeStoredMuscles()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.ConstraintMode = KimodoConstraintMode.FullBody;
                marker.FullBodyData.pose.muscles[0] = 0.42f;
                marker.FullBodyData.ikTargets.hands.left.t = new Vector3(1f, 2f, 3f);

                KimodoMarkerSampleResult editable = marker.SampleData;
                editable.characterPose.hands.left.t = new Vector3(4f, 5f, 6f);
                marker.CommitSampleData();

                Assert.That(marker.FullBodyData.pose.muscles[0], Is.EqualTo(0.42f));
                Assert.That(marker.FullBodyData.ikTargets.hands.left.t, Is.EqualTo(new Vector3(4f, 5f, 6f)));
                Assert.That(object.ReferenceEquals(
                    marker.FullBodyData.pose.hands.left,
                    marker.FullBodyData.ikTargets.hands.left), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void FullBodyAlwaysExportsAllIkTargetsWithoutChangingMuscles()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.ConstraintMode = KimodoConstraintMode.FullBody;
                marker.FullBodyData.pose.muscles[0] = 0.37f;
                marker.FullBodyData.pose.root.t = new Vector3(2f, 3f, 4f);
                marker.FullBodyData.ikTargets.hands.left.t = new Vector3(1f, 2f, 3f);

                KimodoMarkerSampleResult sample = marker.SampleData;
                Assert.That(sample.mask.leftHand && sample.mask.rightHand && sample.mask.leftFoot && sample.mask.rightFoot, Is.True);

                CharacterPose observedPose = null;
                JArray json = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    new KimodoConstraintExportContext(1f, value =>
                    {
                        observedPose = value.Clone();
                        return new System.Collections.Generic.List<Vector3> { Vector3.zero };
                    }),
                    exportFps: 30.0));

                Assert.That(json.Select(item => item.Value<string>("type")), Is.EqualTo(new[] { "fullbody" }));
                Assert.That(observedPose, Is.Not.Null);
                Assert.That(observedPose.hands.left.t, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(marker.FullBodyData.pose.muscles[0], Is.EqualTo(0.37f));
            }
            finally
            {
                Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ExportDoesNotMutateFullBodyMuscles()
        {
            var pose = new CharacterPose();
            pose.muscles[0] = 0.37f;
            var sample = new KimodoMarkerSampleResult
            {
                constraintMode = "fullbody",
                constraintType = "constraint",
                characterPose = pose,
                mask = KimodoConstraintMask.ForType("fullbody")
            };

            _ = KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { sample },
                new KimodoConstraintExportContext(1f, value => new System.Collections.Generic.List<Vector3>
                {
                    KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(value.root.q)
                }));

            Assert.That(sample.characterPose.muscles[0], Is.EqualTo(0.37f));
        }

        [Test]
        public void MarkerTimeDisplayStaysTimelineGlobal()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip source = new AnimationClip();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)clip.asset).clip = source;
                clip.start = 10.0;
                clip.duration = 3.0;

                KimodoConstraintMarker marker = track.CreateMarker<KimodoConstraintMarker>(11.5);
                Assert.That(
                    KimodoConstraintMarkerEditorUtility.GetMarkerTimeForDisplay(marker),
                    Is.EqualTo(11.5).Within(1e-9));
            }
            finally
            {
                Object.DestroyImmediate(timeline);
                Object.DestroyImmediate(source);
            }
        }
    }
}
