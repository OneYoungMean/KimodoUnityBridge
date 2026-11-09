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
        public KimodoConstraintWindowSpec BeginWindow = KimodoConstraintWindowSpec.FromModelFrames(1, 1);
        public KimodoConstraintWindowSpec EndWindow = KimodoConstraintWindowSpec.FromModelFrames(1, 1);
        public KimodoTimelineInOutConstraintContext TimelineContext;
        public KimodoTimelineInOutConstraintContext BeginTimelineContext;
        public KimodoTimelineInOutConstraintContext EndTimelineContext;
        public bool HasExplicitBeginSource;
        public bool HasExplicitEndSource;
        public int BeginSourceFrame = -1;
        public int EndSourceFrame = -1;
    }

}
