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
        public string ModelName = KimodoMotionModelProfiles.DefaultModelName;
        public int GenerationFrames = 1;
        public int OutputStartFrame;
        public int OutputFrameCount;
        public int BeginWindowFrames = 1;
        public int BeginSampleCount = 1;
        public int EndWindowFrames = 1;
        public int EndSampleCount = 1;
        public bool AutoBeginAnchor;
        public bool DeferNormalization;
        public bool DeferBeginConstraint;
        public bool DeferEndConstraint;
        public KimodoTimelineInOutConstraintContext TimelineContext;
        public KimodoTimelineInOutConstraintContext BeginTimelineContext;
        public KimodoTimelineInOutConstraintContext EndTimelineContext;
        public bool HasExplicitBeginSource;
        public bool HasExplicitEndSource;
        public int BeginSourceFrame = -1;
        public int EndSourceFrame = -1;
        public List<KimodoMarkerSampleResult> ManualSamples = new List<KimodoMarkerSampleResult>();
    }

    internal sealed class KimodoInOutConstraintResult
    {
        public List<KimodoMarkerSampleResult> CombinedSamples = new List<KimodoMarkerSampleResult>();
        public string ConstraintsJson = string.Empty;
        public string DeferredInConstraintJson = string.Empty;
        public string DeferredOutConstraintJson = string.Empty;
        public string InConstraintWindowJson = string.Empty;
        public string OutConstraintWindowJson = string.Empty;
        public bool HasSyntheticAutoBeginConstraint;
        public bool DenseRootPath;
        public KimodoMarkerSampleResult BeginBoundarySample;
        public List<KimodoMarkerSampleResult> BeginBoundarySamples = new List<KimodoMarkerSampleResult>();
        public List<KimodoMarkerSampleResult> EndBoundarySamples = new List<KimodoMarkerSampleResult>();
    }
}
