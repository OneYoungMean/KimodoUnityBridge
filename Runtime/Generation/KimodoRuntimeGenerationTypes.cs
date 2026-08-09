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
        [NonSerialized] public KimodoConstraintPayload constraints = new KimodoConstraintPayload();
        // Optional JSON object forwarded as the protocol-level analysis_option field.
        public string analysis_option_json;
        [NonSerialized] public List<KimodoTimelineSegmentDto> timeline_segments;
        // Generic KMB ClipConstraints for `analysis_option.analysis_only`.
        // They intentionally carry no ARDY mask so the server can analyze them
        // without loading a motion model or text encoder.
        [NonSerialized] public List<KimodoKmbClipConstraint> analysis_clip_constraints;
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
        public double? ardy_history_weight;
        public double? ardy_max_speed;
        public double? ardy_max_acceleration;
        public double? ardy_playback_reserve_seconds;
        public string output_format = "kmb_v1";
    }

    [Serializable]
    public sealed class KimodoTimelineSegmentDto
    {
        public string prompt;
        public float duration;
    }

    [Serializable]
    public sealed class KimodoKmbClipConstraint
    {
        [NonSerialized] public byte[] motionBytes;
        public int startFrame;
        public int endFrameExclusive;
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
        [NonSerialized] public IReadOnlyList<KimodoBridgeKmbAttachment> kmbAttachments;
        public string motionFormat;
        public string rawStatus;
        public string message;
        public string motionRepFingerprint;
        public int? resolvedSeed;
        public int startFrame;
        public int endFrameExclusive;
        public string analysisJson;
    }
}
