using System;
using System.Collections.Generic;

namespace KimodoBridge
{
    [Serializable]
    public sealed class KimodoGenerationRequestDto
    {
        public string task_id;
        public string prompt;
        public float? duration;
        public double time_as_double;
        public int? seed;
        public int steps;
        public float text_weight = 1f;
        public string constraints_json;
        [NonSerialized] public List<KimodoArdyTimelineSegmentDto> ardy_timeline_segments;
        [NonSerialized] public List<KimodoArdyClipConstraint> ardy_future_clips;
        [NonSerialized] public byte[] ardy_history_kmb;
        [NonSerialized] public bool ardy_session_update_only;
        // Optional desired transition overlap in seconds.
        public float transition_duration;
        // Runtime configuration is sent together with generate under the current bridge protocol.
        public string model;
        public string text_encoder_mode = KimodoTextEncoderModeProtocol.HighPrecision;
        public int? simulate_free_vram_gb;
        public string models_root;
        public bool force_hf_download;
        public int owner_pid;
        public double? ardy_history_crop_seconds;
        public double? ardy_future_crop_seconds;
        public double? ardy_playback_reserve_seconds;
        public bool? ardy_adaptive_playback_reserve;
        public string output_format = "kmb_v1";
    }

    [Serializable]
    public sealed class KimodoArdyTimelineSegmentDto
    {
        public string prompt;
        public float duration;
    }

    public static class KimodoTextEncoderModeProtocol
    {
        public const string HighPerformance = "high_performance";
        public const string HighPrecision = "high_precision";

        public static string ToProtocolValue(KimodoTextEncoderMode mode)
        {
            return mode == KimodoTextEncoderMode.HighPerformance ? HighPerformance : HighPrecision;
        }
    }

    [Serializable]
    public sealed class KimodoGenerationResultDto
    {
        public string motionJsonCompact;
        [NonSerialized] public KimodoRawMotionData motionData;
        [NonSerialized] public byte[] motionBytes;
        public string motionFormat;
        public string rawStatus;
        public string message;
        public string motionRepFingerprint;
        public int? resolvedSeed;
        public int startFrame;
        public int endFrameExclusive;
    }
}
