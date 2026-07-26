using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRuntimeMotionDriverTests
    {
        [Serializable]
        private sealed class MotionJsonData
        {
            public List<float> foot_contacts;
        }

        [TestCase(KimodoMotionModelProfiles.ArdyCoreModelName, 4)]
        [TestCase(KimodoMotionModelProfiles.ArdyG1ModelName, 5)]
        public void ValidateArdyResult_AcceptsPlaybackDelaySizedDownload(string modelName, int frameCount)
        {
            Assert.That(KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile), Is.True);
            var motion = new KimodoRawMotionData(
                frameCount,
                profile.JointCount,
                profile.SourceFps,
                new string[profile.JointCount],
                new int[profile.JointCount],
                new Vector3[frameCount],
                new List<float>(new float[frameCount * profile.JointCount * 4]),
                0);
            var result = new KimodoBridgeGenerationResult
            {
                MotionData = motion,
                MotionBytes = new byte[] { 1 },
                MotionFormat = "kmb_v1",
                MotionRepFingerprint = profile.MotionRepFingerprint,
                ResolvedSeed = 42,
                StartFrame = 0,
                EndFrameExclusive = frameCount
            };

            Assert.DoesNotThrow(() => KimodoRuntimeMotionDriver.ValidateArdyResult(result, profile, 42));
        }

        [Test]
        public void ValidateArdyResult_AcceptsVariableLengthDirectKmb()
        {
            Assert.That(
                KimodoMotionModelProfiles.TryGetArdy(
                    KimodoMotionModelProfiles.ArdyCoreModelName,
                    out KimodoMotionModelProfile profile),
                Is.True);
            int frameCount = profile.HorizonFrames + 1;
            var motion = new KimodoRawMotionData(
                frameCount,
                profile.JointCount,
                profile.SourceFps,
                new string[profile.JointCount],
                new int[profile.JointCount],
                new Vector3[frameCount],
                new List<float>(new float[frameCount * profile.JointCount * 4]),
                0);
            var result = new KimodoBridgeGenerationResult
            {
                MotionData = motion,
                MotionBytes = new byte[] { 1 },
                MotionFormat = "kmb_v1",
                MotionRepFingerprint = profile.MotionRepFingerprint,
                ResolvedSeed = 42,
                StartFrame = 0,
                EndFrameExclusive = frameCount
            };

            Assert.DoesNotThrow(() =>
                KimodoRuntimeMotionDriver.ValidateArdyResult(result, profile, 42));
        }

        [Test]
        public void CompletedStaleArdyResult_IsKeptToPreserveTheServerCursor()
        {
            Assert.That(
                KimodoRuntimeMotionDriver.ShouldDiscardCompletedGenerationResult(
                    isArdy: true,
                    staleRequest: true,
                    lifetimeCancelled: false),
                Is.False);
            Assert.That(
                KimodoRuntimeMotionDriver.ShouldDiscardCompletedGenerationResult(
                    isArdy: false,
                    staleRequest: true,
                    lifetimeCancelled: false),
                Is.True);
            Assert.That(
                KimodoRuntimeMotionDriver.ShouldDiscardCompletedGenerationResult(
                    isArdy: true,
                    staleRequest: false,
                    lifetimeCancelled: true),
                Is.True);
        }

        [Test]
        public void StreamRefresh_DoesNotCancelAnActiveArdyGenerate()
        {
            Assert.That(KimodoRuntimeMotionDriver.ShouldCancelActiveGenerationForRefresh(isArdy: true), Is.False);
            Assert.That(KimodoRuntimeMotionDriver.ShouldCancelActiveGenerationForRefresh(isArdy: false), Is.True);
        }

        [Test]
        public void RawMotionAppend_GrowsOneContinuousTimeline()
        {
            KimodoRawMotionData first = CreateMotion(4, 2, 20f);
            KimodoRawMotionData second = CreateMotion(3, 2, 20f);

            Assert.That(first.TryAppend(second, 4, out string error), Is.True, error);
            Assert.That(first.FrameCount, Is.EqualTo(7));
        }

        [Test]
        public void RawMotionAppend_ReportsMissingCommittedArdyRange()
        {
            KimodoRawMotionData timeline = CreateMotion(40, 2, 20f);
            KimodoRawMotionData segment = CreateMotion(20, 2, 20f);

            Assert.That(timeline.TryAppend(segment, 60, out string error), Is.False);
            Assert.That(error, Does.Contain("starts at frame 60"));
            Assert.That(error, Does.Contain("has 40 frames"));
        }

        [Test]
        public void RawMotionAppend_CompactJsonDoesNotSerializeSpareContactCapacity()
        {
            KimodoRawMotionData first = CreateMotion(4, 2, 20f, withContacts: true);
            KimodoRawMotionData second = CreateMotion(3, 2, 20f, withContacts: true);

            Assert.That(first.TryAppend(second, 4, out string error), Is.True, error);
            MotionJsonData json = JsonUtility.FromJson<MotionJsonData>(
                KimodoRawMotionUtility.ToCompactJson(first));
            Assert.That(
                json.foot_contacts.Count,
                Is.EqualTo(7 * KimodoFootContactTrackUtility.ChannelCount));
        }

        [Test]
        public void ConstraintJson_ARDYRootWaypointRequestsOfficialDensePath()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 2.0,
                kimodoRootPosition = new Vector3(3f, 0f, 4f),
                hasRootHeading = false
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipDurationSeconds: 8.0,
                    exportFps: 20.0,
                    denseRootPath: true));

            Assert.That(constraints[0].Value<bool>("dense_path"), Is.True);
            Assert.That(constraints[0]["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(40));
        }

        [Test]
        public void ConstraintJson_ARDYRootTargetKeepsTimingInTheBackend()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d_target",
                kimodoRootPosition = new Vector3(2f, 0f, 3f),
                rootTargetMaxSpeed = 1.25f,
                rootTargetMaxAcceleration = 1.5f,
                rootTargetArrivalThreshold = 0.1f,
                rootTargetIncludeHeading = true
            };

            JObject target = (JObject)JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipDurationSeconds: 8.0,
                    exportFps: 20.0))[0];

            Assert.That(target.Value<string>("type"), Is.EqualTo("root2d_target"));
            Assert.That(target["target_root_2d"]?[0]?.Value<float>(), Is.EqualTo(-2f));
            Assert.That(target["target_root_2d"]?[1]?.Value<float>(), Is.EqualTo(3f));
            Assert.That(target.Value<float>("max_speed"), Is.EqualTo(1.25f));
            Assert.That(target.Value<float>("max_acceleration"), Is.EqualTo(1.5f));
            Assert.That(target.Value<bool>("include_heading"), Is.True);
            Assert.That(target["frame_indices"], Is.Null);
        }

        [Test]
        public void ConstraintJson_ARDYRootHeadingUsesBridgeCoordinates()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(2f, 0f, 3f),
                hasRootHeading = true,
                rootHeading = new Vector2(-1f, 0f)
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipDurationSeconds: 8.0,
                    exportFps: 20.0));

            Assert.That(constraints[0]["global_root_heading"]?[0]?[0]?.Value<float>(), Is.EqualTo(1f));
            Assert.That(constraints[0]["global_root_heading"]?[0]?[1]?.Value<float>(), Is.EqualTo(0f));
        }

        private static KimodoRawMotionData CreateMotion(int frames, int joints, float fps, bool withContacts = false)
        {
            var rotations = new List<float>(frames * joints * 4);
            for (int frame = 0; frame < frames; frame++)
            {
                for (int joint = 0; joint < joints; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }
            return new KimodoRawMotionData(
                frames,
                joints,
                fps,
                new[] { "Root", "Child" },
                new[] { -1, 0 },
                new Vector3[frames],
                rotations,
                0,
                withContacts ? new byte[frames * KimodoFootContactTrackUtility.ChannelCount] : null);
        }
    }
}
