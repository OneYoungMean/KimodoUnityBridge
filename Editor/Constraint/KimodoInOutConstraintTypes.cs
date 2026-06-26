using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoInOutConstraintClipSegment
    {
        public AnimationClip Clip;
        public double StartSeconds;
        public double DurationSeconds;
        public float Speed = 1f;
    }

    internal sealed class KimodoInOutConstraintRequest
    {
        public KimodoInOutConstraintMode Mode;
        public KimodoInOutConstraintClipSegment BeginSegment;
        public KimodoInOutConstraintClipSegment EndSegment;
        public bool EnableBegin;
        public bool EnableEnd;
        public Avatar SourceAvatar;
        public string ModelName = KimodoPlayableClip.DefaultBridgeModelName;
        public int GenerationFrames = 1;
        public bool NormalizeConstraintOrigin;
        public bool IsLoop;
        public List<KimodoMarkerSampleResult> ManualSamples = new List<KimodoMarkerSampleResult>();
    }

    internal sealed class KimodoInOutConstraintResult
    {
        public KimodoMarkerSampleResult BeginSample;
        public KimodoMarkerSampleResult EndSample;
        public List<KimodoMarkerSampleResult> CombinedSamples = new List<KimodoMarkerSampleResult>();
        public string ConstraintsJson = string.Empty;
    }
}
