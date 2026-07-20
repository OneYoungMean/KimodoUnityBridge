using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    [Serializable]
    public sealed class KimodoGenerationRequestDto
    {
        public string task_id;
        public string prompt;
        public float duration;
        public int? seed;
        public int steps;
        public float text_weight = 1f;
        public string constraints_json;
        [NonSerialized] public List<KimodoArdyClipConstraint> ardy_future_clips;
        // Optional desired transition overlap in seconds.
        public float transition_duration;
        // Runtime configuration is sent together with generate under the current bridge protocol.
        public string model;
        public string text_encoder_mode = KimodoTextEncoderModeProtocol.HighPrecision;
        public int? simulate_vram_gb;
        public string models_root;
        public bool force_hf_download;
        public int owner_pid;
        public string output_format = "kmb_handle_v1";
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
        public string clipHandle;
        public string motionRepFingerprint;
        public int? resolvedSeed;
        [NonSerialized] public AnimationHandleOperator handleOperator;
    }
}
