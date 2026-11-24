using System;
using Newtonsoft.Json.Linq;
using KimodoBridge;

namespace KimodoBridge.Editor
{
    internal static class KimodoFlowHmrTiming
    {
        internal static bool TryRead(
            KimodoRawMotionData motion,
            JObject analysis,
            out float[] frameTimes,
            out float durationSeconds,
            out int trackingStartSample,
            out int trackingEndSampleExclusive,
            out string error)
        {
            frameTimes = null;
            durationSeconds = 0f;
            trackingStartSample = 0;
            trackingEndSampleExclusive = motion != null ? motion.FrameCount : 0;
            error = string.Empty;
            if (motion == null)
            {
                error = "FlowHMR motion is null.";
                return false;
            }

            if (!motion.HasGlobalTimeTrack)
            {
                return TryRead(
                    analysis, motion.FrameCount, motion.FrameRate,
                    out frameTimes, out durationSeconds,
                    out trackingStartSample, out trackingEndSampleExclusive, out error);
            }

            var withGlobalTimes = analysis != null
                ? (JObject)analysis.DeepClone()
                : new JObject();
            var times = new JArray();
            for (int frame = 0; frame < motion.FrameCount; frame++)
            {
                if (!motion.TryReadGlobalTime(frame, out float time))
                {
                    error = $"FlowHMR global time track is invalid at frame {frame}.";
                    return false;
                }
                times.Add(time);
            }
            withGlobalTimes["frame_timestamps_seconds"] = times;
            return TryRead(
                withGlobalTimes, motion.FrameCount, motion.FrameRate,
                out frameTimes, out durationSeconds,
                out trackingStartSample, out trackingEndSampleExclusive, out error);
        }

        internal static int ClampSampleToTrackingRange(
            int sampleIndex,
            int trackingStartSample,
            int trackingEndSampleExclusive,
            int frameCount)
        {
            if (frameCount <= 0)
                return 0;
            int start = Math.Max(0, Math.Min(trackingStartSample, frameCount - 1));
            int end = Math.Max(start + 1, Math.Min(trackingEndSampleExclusive, frameCount));
            return Math.Max(start, Math.Min(sampleIndex, end - 1));
        }

        internal static bool TryRead(
            JObject analysis,
            int frameCount,
            float frameRate,
            out float[] frameTimes,
            out float durationSeconds,
            out string error)
        {
            return TryRead(
                analysis, frameCount, frameRate,
                out frameTimes, out durationSeconds,
                out _, out _, out error);
        }

        internal static bool TryRead(
            JObject analysis,
            int frameCount,
            float frameRate,
            out float[] frameTimes,
            out float durationSeconds,
            out int trackingStartSample,
            out int trackingEndSampleExclusive,
            out string error)
        {
            frameTimes = null;
            durationSeconds = 0f;
            trackingStartSample = 0;
            trackingEndSampleExclusive = frameCount;
            error = string.Empty;
            JToken token = analysis?["frame_timestamps_seconds"];
            if (token == null || token.Type == JTokenType.Null)
                return false;
            if (token is not JArray values || values.Count != frameCount || frameCount <= 0)
            {
                error = "FlowHMR frame timestamps do not match the KMB frame count.";
                return false;
            }

            frameTimes = new float[frameCount];
            for (int index = 0; index < frameCount; index++)
            {
                float value = values[index].Value<float>();
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f ||
                    (index > 0 && value <= frameTimes[index - 1]))
                {
                    frameTimes = null;
                    error = $"FlowHMR frame timestamp {index} is invalid or not strictly increasing.";
                    return false;
                }
                frameTimes[index] = value;
            }

            durationSeconds = analysis.Value<float?>("source_duration_seconds") ?? 0f;
            if (float.IsNaN(durationSeconds) || float.IsInfinity(durationSeconds) || durationSeconds <= 0f)
                durationSeconds = frameTimes[frameCount - 1] + 1f / Math.Max(1f, frameRate);
            if (durationSeconds < frameTimes[frameCount - 1])
            {
                frameTimes = null;
                error = "FlowHMR source duration precedes the last frame timestamp.";
                return false;
            }

            JToken rangeToken = analysis?["tracking_frame_range"];
            if (rangeToken == null || rangeToken.Type == JTokenType.Null)
                return true;
            if (rangeToken is not JArray range || range.Count != 2)
            {
                frameTimes = null;
                error = "FlowHMR tracking frame range must contain two values.";
                return false;
            }
            int trackingStartFrame = range[0].Value<int>();
            int trackingEndFrameExclusive = range[1].Value<int>();
            if (trackingStartFrame < 0 || trackingEndFrameExclusive <= trackingStartFrame)
            {
                frameTimes = null;
                error = "FlowHMR tracking frame range must be a positive half-open interval.";
                return false;
            }

            // The server reports the range in the 30 FPS transcode's frame
            // space. Map it onto the returned motion samples so this also
            // works with an older result that contains only tracked samples.
            float safeFrameRate = Math.Max(1f, frameRate);
            while (trackingStartSample < frameCount &&
                   (int)Math.Floor(frameTimes[trackingStartSample] * safeFrameRate + 0.5f) < trackingStartFrame)
            {
                trackingStartSample++;
            }
            trackingEndSampleExclusive = trackingStartSample;
            while (trackingEndSampleExclusive < frameCount &&
                   (int)Math.Floor(frameTimes[trackingEndSampleExclusive] * safeFrameRate + 0.5f) < trackingEndFrameExclusive)
            {
                trackingEndSampleExclusive++;
            }
            if (trackingStartSample >= trackingEndSampleExclusive)
            {
                frameTimes = null;
                error = "FlowHMR tracking frame range does not overlap its motion timestamps.";
                return false;
            }
            return true;
        }
    }
}
