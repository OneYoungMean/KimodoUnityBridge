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
            out string error)
        {
            frameTimes = null;
            durationSeconds = 0f;
            error = string.Empty;
            if (motion == null || motion.FrameCount <= 0)
            {
                error = "FlowHMR motion is null or has no frames.";
                return false;
            }
            if (!motion.HasGlobalTimeTrack)
            {
                error = "FlowHMR KMB is missing the required global_time_seconds track.";
                return false;
            }

            frameTimes = new float[motion.FrameCount];
            for (int frame = 0; frame < frameTimes.Length; frame++)
            {
                if (!motion.TryReadGlobalTime(frame, out frameTimes[frame]) ||
                    (frame > 0 && frameTimes[frame] <= frameTimes[frame - 1]))
                {
                    frameTimes = null;
                    error = $"FlowHMR global_time_seconds is invalid at frame {frame}.";
                    return false;
                }
            }

            durationSeconds = analysis?.Value<float?>("source_duration_seconds") ?? 0f;
            if (float.IsNaN(durationSeconds) || float.IsInfinity(durationSeconds) || durationSeconds <= 0f)
                durationSeconds = frameTimes[frameTimes.Length - 1] + 1f / Math.Max(1f, motion.FrameRate);
            if (durationSeconds < frameTimes[frameTimes.Length - 1])
            {
                frameTimes = null;
                error = "FlowHMR source duration precedes the last global_time_seconds value.";
                return false;
            }
            return true;
        }
    }
}
