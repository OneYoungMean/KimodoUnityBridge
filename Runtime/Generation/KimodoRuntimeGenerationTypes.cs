using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace KimodoBridge
{
    [Serializable]
    public sealed class KimodoGenerationRequestDto
    {
        public string task_id;
        [NonSerialized] public string raw_generate_instruction_json;
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
        public double? ardy_history_weight;
        public double? ardy_playback_reserve_seconds;
        public string output_format = "kmb_v1";
        public bool output_mp4;
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

    // Canonical result payload shared by the bridge and generation pipeline.
    [Serializable]
    public sealed class KimodoGenerationResultDto
    {
        public string motionJsonCompact;
        [NonSerialized] public KimodoRawMotionData motionData;
        [NonSerialized] public byte[] motionBytes;
        public string mp4Path;
        // `kmb_attachments_v1` keeps each analysis clip independently parseable.
        [NonSerialized] public IReadOnlyList<KimodoBridgeKmbAttachment> kmbAttachments;
        public string motionFormat;
        public string rawStatus;
        public string message;
        public string motionRepFingerprint;
        public int? resolvedSeed;
        public int startFrame;
        public int endFrameExclusive;
        public double? ardyPlaybackReserveSeconds;
        public string analysisJson;
    }

    public static class KimodoRawGenerateInstruction
    {
        public static JObject Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("raw_instruction.text must contain a generate JSON object.");
            JObject payload;
            try { payload = JObject.Parse(json); }
            catch (Exception ex) { throw new InvalidOperationException($"raw_instruction.text must be valid JSON: {ex.Message}"); }
            if (!string.Equals(payload.Value<string>("cmd")?.Trim(), "generate", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("raw_instruction.text must contain cmd=generate.");
            if (payload["kmb_attachments"] != null)
                throw new InvalidOperationException("Raw generate replay cannot use kmb_attachments; the binary payload is not included in JSON.");
            string constraints = payload.Value<string>("constraints_json");
            if (!string.IsNullOrWhiteSpace(constraints) && constraints.IndexOf("kmb_attachment_v1", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Raw generate replay cannot use binary KMB constraints; the attachment bytes are not included in JSON.");
            if (payload["analysis_option"] != null)
                throw new InvalidOperationException("Raw generate replay does not support the removed analysis_option field.");
            string outputFormat = payload.Value<string>("output_format")?.Trim();
            if (!string.IsNullOrEmpty(outputFormat) && !string.Equals(outputFormat, "kmb_v1", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Raw generate replay requires output_format=kmb_v1.");
            return payload;
        }

        public static float? ReadDuration(JObject payload)
        {
            JToken token = payload?["duration"];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!float.TryParse(token.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) ||
                float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                throw new InvalidOperationException("Raw generate duration must be a finite positive number.");
            return value;
        }

        public static string ReadPrompt(JObject payload) => payload?.Value<string>("prompt")?.Trim() ?? string.Empty;
    }
}
