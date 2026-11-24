#if UNITY_EDITOR
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;
using KimodoBridge;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoFlowHmrTimingTests
    {
        [Test]
        public void ClampSampleToTrackingRange_HoldsBoundarySamplesOutsideTrack()
        {
            Assert.That(KimodoFlowHmrTiming.ClampSampleToTrackingRange(0, 45, 498, 542), Is.EqualTo(45));
            Assert.That(KimodoFlowHmrTiming.ClampSampleToTrackingRange(45, 45, 498, 542), Is.EqualTo(45));
            Assert.That(KimodoFlowHmrTiming.ClampSampleToTrackingRange(200, 45, 498, 542), Is.EqualTo(200));
            Assert.That(KimodoFlowHmrTiming.ClampSampleToTrackingRange(541, 45, 498, 542), Is.EqualTo(497));
        }

        [Test]
        public void TryRead_MapsFullVideoSamplesToTrackedHalfOpenRange()
        {
            var timestamps = new JArray();
            for (int frame = 0; frame < 542; frame++)
                timestamps.Add(frame / 30.0);
            var analysis = new JObject
            {
                ["frame_timestamps_seconds"] = timestamps,
                ["source_duration_seconds"] = 18.0817,
                ["tracking_frame_range"] = new JArray(45, 498)
            };

            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    analysis, 542, 30f,
                    out float[] frameTimes, out float duration,
                    out int trackingStart, out int trackingEnd, out string error),
                Is.True,
                error);
            Assert.That(frameTimes, Has.Length.EqualTo(542));
            Assert.That(duration, Is.EqualTo(18.0817f).Within(1e-4f));
            Assert.That(trackingStart, Is.EqualTo(45));
            Assert.That(trackingEnd, Is.EqualTo(498));
        }

        [Test]
        public void TryRead_MapsTrackedOnlySamplesToLocalRange()
        {
            var timestamps = new JArray();
            for (int frame = 45; frame < 498; frame++)
                timestamps.Add(frame / 30.0);
            var analysis = new JObject
            {
                ["frame_timestamps_seconds"] = timestamps,
                ["source_duration_seconds"] = 18.0817,
                ["tracking_frame_range"] = new JArray(45, 498)
            };

            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    analysis, 453, 30f,
                    out _, out _,
                    out int trackingStart, out int trackingEnd, out string error),
                Is.True,
                error);
            Assert.That(trackingStart, Is.EqualTo(0));
            Assert.That(trackingEnd, Is.EqualTo(453));
        }

        [Test]
        public void TryRead_RejectsInvalidTrackingRange()
        {
            var analysis = new JObject
            {
                ["frame_timestamps_seconds"] = new JArray(0.0, 1.0 / 30.0),
                ["source_duration_seconds"] = 1.0,
                ["tracking_frame_range"] = new JArray(2, 2)
            };

            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    analysis, 2, 30f,
                    out _, out _, out _, out _, out string error),
                Is.False);
            Assert.That(error, Does.Contain("tracking frame range"));
        }

        [Test]
        public void TryRead_PrefersGlobalTimeTrackOverDiagnosticJsonTimestamps()
        {
            var motion = new KimodoRawMotionData(
                3, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new[] { Vector3.zero, Vector3.zero, Vector3.zero },
                new List<float>
                {
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                },
                0,
                globalTimeSeconds: new[] { 0f, 0.04f, 0.0733333f });
            var analysis = new JObject
            {
                ["frame_timestamps_seconds"] = new JArray(0.0, 1.0 / 30.0, 2.0 / 30.0),
                ["source_duration_seconds"] = 0.2,
            };

            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    motion, analysis,
                    out float[] frameTimes, out _,
                    out int trackingStart, out int trackingEnd, out string error),
                Is.True,
                error);
            Assert.That(frameTimes, Has.Length.EqualTo(3));
            Assert.That(frameTimes[1], Is.EqualTo(0.04f).Within(1e-6f));
            Assert.That(frameTimes[2], Is.EqualTo(0.0733333f).Within(1e-6f));
            Assert.That(trackingStart, Is.EqualTo(0));
            Assert.That(trackingEnd, Is.EqualTo(3));
        }

        [Test]
        public void TryRead_UsesLegacyJsonTimestampsForOldKmbWithoutTrack()
        {
            var motion = new KimodoRawMotionData(
                2, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new[] { Vector3.zero, Vector3.zero },
                new List<float> { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f },
                0);
            var analysis = new JObject
            {
                ["frame_timestamps_seconds"] = new JArray(0.0, 0.04),
                ["source_duration_seconds"] = 0.1,
            };

            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    motion, analysis,
                    out float[] frameTimes, out _,
                    out _, out _, out string error),
                Is.True,
                error);
            Assert.That(frameTimes[1], Is.EqualTo(0.04f).Within(1e-6f));
        }

        [Test]
        public void HumanDetectionTrack_ClampsInvalidSampleToNearestValidPose()
        {
            var motion = new KimodoRawMotionData(
                5, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero },
                new List<float>
                {
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                },
                0,
                humanDetection01: new byte[] { 1, 0, 0, 1, 0 });

            Assert.That(motion.TryResolveHumanDetectionFrame(2, out int resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(0));
            Assert.That(motion.TryResolveHumanDetectionFrame(4, out resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(3));
        }

        [Test]
        public void HumanDetectionTrack_ReportsNoPoseWhenAllFramesAreInvalid()
        {
            var motion = new KimodoRawMotionData(
                2, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new[] { Vector3.zero, Vector3.zero },
                new List<float> { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f },
                0,
                humanDetection01: new byte[] { 0, 0 });

            Assert.That(motion.HasHumanDetectionTrack, Is.True);
            Assert.That(motion.HasAnyHumanDetection, Is.False);
            Assert.That(motion.TryResolveHumanDetectionFrame(0, out _), Is.False);
        }

        [Test]
        public void MuscleImporter_UsesDetectionMaskBeforeLegacyTrackingRange()
        {
            var motion = new KimodoRawMotionData(
                5, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero },
                new List<float>
                {
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                },
                0,
                humanDetection01: new byte[] { 1, 0, 1, 1, 0 });

            Assert.That(
                KimodoFlowHmrMuscleClipImporter.TryResolvePoseFrame(
                    motion, 2, hasSourceTiming: true,
                    trackingStartSample: 0, trackingEndSampleExclusive: 1,
                    frameCount: 5,
                    out int poseFrame, out string error),
                Is.True,
                error);
            Assert.That(poseFrame, Is.EqualTo(2));
        }

        [Test]
        public void RawMotionFlatBuffer_RoundTripsOptionalTracksAndKeepsLegacyKmbReadable()
        {
            var trackedMotion = new KimodoRawMotionData(
                3, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new[] { Vector3.zero, Vector3.zero, Vector3.zero },
                new List<float>
                {
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f,
                },
                0,
                globalTimeSeconds: new[] { 0f, 0.04f, 0.0733333f },
                humanDetection01: new byte[] { 1, 0, 1 });
            byte[] trackedBytes = KimodoRawMotionUtility.ToFlatBuffer(trackedMotion, "smplh");
            Assert.That(
                KimodoRawMotionUtility.TryParseFlatBuffer(trackedBytes, out KimodoRawMotionData parsedTracked, out string error),
                Is.True,
                error);
            Assert.That(parsedTracked.HasGlobalTimeTrack, Is.True);
            Assert.That(parsedTracked.HasHumanDetectionTrack, Is.True);
            Assert.That(parsedTracked.FrameRate, Is.EqualTo(30f));
            Assert.That(parsedTracked.FrameCount, Is.EqualTo(3));
            Assert.That(parsedTracked.TryReadGlobalTime(1, out float time), Is.True);
            Assert.That(time, Is.EqualTo(0.04f).Within(1e-6f));
            CollectionAssert.AreEqual(new byte[] { 1, 0, 1 }, parsedTracked.humanDetection01);

            var legacyMotion = new KimodoRawMotionData(
                2, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new[] { Vector3.zero, Vector3.zero },
                new List<float> { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f },
                0);
            byte[] legacyBytes = KimodoRawMotionUtility.ToFlatBuffer(legacyMotion, "smplh");
            Assert.That(
                KimodoRawMotionUtility.TryParseFlatBuffer(legacyBytes, out KimodoRawMotionData parsedLegacy, out error),
                Is.True,
                error);
            Assert.That(parsedLegacy.HasGlobalTimeTrack, Is.False);
            Assert.That(parsedLegacy.HasHumanDetectionTrack, Is.False);
        }
    }
}
#endif
