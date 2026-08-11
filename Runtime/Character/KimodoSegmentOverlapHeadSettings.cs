using System;
using UnityEngine;

namespace KimodoBridge
{
    [Serializable]
    public sealed class KimodoSegmentOverlapHeadSettings
    {
        [SerializeField] private KimodoSegmentSamplingMode mode = KimodoSegmentSamplingMode.ByDelta;
        [SerializeField][Min(0f)] private float overlapTimeSeconds = 0.35f;
        [SerializeField][Min(0f)] private float deltaThresholdMeters = 0.025f;
        [SerializeField][Min(0f)] private float maxOverlapTimeSeconds = 0.75f;
        [SerializeField][Range(1, 8)] private int sampleCount = 4;

        public KimodoSegmentSamplingMode Mode => mode;
        public float OverlapTimeSeconds => Mathf.Max(0f, overlapTimeSeconds);
        public float DeltaThresholdMeters => Mathf.Max(0f, deltaThresholdMeters);
        public float MaxOverlapTimeSeconds => Mathf.Max(0f, maxOverlapTimeSeconds);
        public int SampleCount => Mathf.Clamp(sampleCount, 1, 8);
    }
}
