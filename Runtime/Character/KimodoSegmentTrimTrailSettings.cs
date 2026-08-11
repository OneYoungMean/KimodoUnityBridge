using System;
using UnityEngine;

namespace KimodoBridge
{
    public enum KimodoSegmentSamplingMode
    {
        ByTime = 0,
        ByDelta = 1
    }

    [Serializable]
    public sealed class KimodoSegmentTrimTrailSettings
    {
        [SerializeField] private KimodoSegmentSamplingMode mode = KimodoSegmentSamplingMode.ByDelta;
        [SerializeField][Min(0f)] private float trimTimeSeconds = 0.35f;
        [SerializeField][Min(0f)] private float deltaThresholdMeters = 0.01f;
        [SerializeField][Min(0f)] private float maxTrimTimeSeconds = 0.75f;

        public KimodoSegmentSamplingMode Mode => mode;
        public float TrimTimeSeconds => Mathf.Max(0f, trimTimeSeconds);
        public float DeltaThresholdMeters => Mathf.Max(0f, deltaThresholdMeters);
        public float MaxTrimTimeSeconds => Mathf.Max(0f, maxTrimTimeSeconds);
    }
}
