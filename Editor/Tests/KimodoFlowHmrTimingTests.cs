#if UNITY_EDITOR
using Newtonsoft.Json.Linq;
using NUnit.Framework;

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
    }
}
#endif
