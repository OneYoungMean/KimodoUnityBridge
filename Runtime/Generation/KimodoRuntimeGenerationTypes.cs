using System;
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
        public string constraints_json;
        // Optional hint to backend that this request is for loop/infinite continuation.
        public bool loop_hint;
        // Optional segment sequence index for observability on backend side.
        public int segment_index;
        // Optional desired transition overlap in seconds.
        public float transition_duration;
        // Runtime configuration is sent together with generate under the current bridge protocol.
        public string model;
        public bool highvram;
        public bool force_cpu;
        public string models_root;
        public bool force_hf_download;
        public int owner_pid;
    }

    [Serializable]
    public sealed class KimodoGenerationResultDto
    {
        public string motionJsonCompact;
        [NonSerialized] public KimodoRawMotionData motionData;
        public string motionFormat;
        public string rawStatus;
        public string message;
    }
}
