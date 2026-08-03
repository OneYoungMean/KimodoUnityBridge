using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    public enum KimodoConstraintNormalizationAnchorKind
    {
        None = 0,
        FullBody = 1,
        Root2D = 2,
        Foot = 3,
        EndEffector = 4,
        // Retained for source compatibility. Auto Begin now produces a Root2D constraint.
        AutoBegin = 5
    }

    internal sealed class KimodoConstraintNormalizationInfo
    {
        public bool Applied;
        public KimodoConstraintNormalizationAnchorKind AnchorKind = KimodoConstraintNormalizationAnchorKind.None;
        public KimodoMarkerSampleResult AnchorSample;

        internal KimodoConstraintNormalizationInfo Clone()
        {
            return new KimodoConstraintNormalizationInfo
            {
                Applied = Applied,
                AnchorKind = AnchorKind,
                AnchorSample = AnchorSample?.Clone()
            };
        }
    }

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
        public float SourceHumanScale = 1f;
        public float KimodoHumanScale = 1f;
        public int GenerationFrames = 1;
        public bool AutoBeginAnchor;
        public bool DeferNormalization;
        public bool IsLoop;
        public KimodoTimelineInOutConstraintContext TimelineContext;
        public List<KimodoMarkerSampleResult> ManualSamples = new List<KimodoMarkerSampleResult>();
    }

    internal sealed class KimodoInOutConstraintResult
    {
        public List<KimodoMarkerSampleResult> CombinedSamples = new List<KimodoMarkerSampleResult>();
        public string ConstraintsJson = string.Empty;
        public KimodoConstraintNormalizationInfo NormalizationInfo = new KimodoConstraintNormalizationInfo();
        public bool HasSyntheticAutoBeginConstraint;
    }
}
