using System;
using System.Collections.Generic;
using System.Threading;
using TimelineInject;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGenerateRequest
    {
        public string Prompt;
        public string ModelName;
        public KimodoTextEncoderMode TextEncoderMode;
        public int TargetFrameCount;
        public float TargetFrameRate = KimodoPlayableClip.FIXED_FRAME_RATE;
        public int RuntimeFrameCount;
        public int RuntimeTrimStartFrame;
        public int DiffusionSteps;
        public float TextWeight = 1f;
        public int EffectiveSeed;
        public string ConstraintsJson;
        public Func<AnimationClip> CreateTargetClip;
        public Func<AnimationClip, string, KimodoEditorGenerateOutputPlan> ResolveOutputPlan;
        public KimodoEditorGenerateOutputPlan OutputPlan;
        public string ModelsRoot = string.Empty;
        public float GenerationTimeoutSeconds = 600f;
        public AnimationClip TargetClip;
        public AnimationClip RawBoneClip;
        public Action<KimodoBridgeCommandStage, string> Progress;
        public CancellationToken Token;
        public KimodoConstraintNormalizationInfo NormalizationInfo = new KimodoConstraintNormalizationInfo();
        public bool HasSyntheticAutoBeginConstraint;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
        public TimelineClip TimelineClipSnapshot;
        public bool ResetTimelineTimeScaleAfterGeneration;
        public PlayableDirector TimelineDirectorSnapshot;
        public PoseCacheRenderContext? TimelinePoseContextSnapshot;
        public ArdyEditorHistorySource InitialArdyHistorySource;
        public bool DisableTimelineInOut;
        public KimodoPlayableClip ContinuousOffsetSourceClip;
        public string GeneratedArdyMotionCachePath = string.Empty;
        public List<string> GeneratedArdyWindowCachePaths = new List<string>();
        public List<int> GeneratedArdySeeds = new List<int>();
        public string GeneratedArdyFingerprint = string.Empty;

        public int EffectiveRuntimeFrameCount =>
            RuntimeFrameCount > 0 ? RuntimeFrameCount : TargetFrameCount;
    }

    internal sealed class ArdyEditorHistorySource
    {
        public KimodoTimelineInOutConstraintContext TimelineContext;
        public double RangeStartSeconds;
        public double RangeEndSeconds;
        public bool HasTimelineWorldAnchor;
        public Vector3 TimelineWorldAnchorPosition;
        public Quaternion TimelineWorldAnchorRotation = Quaternion.identity;
    }

    internal sealed class KimodoEditorGenerateOutputPlan
    {
        public Avatar OriginRetargetAvatar;
        public Avatar TargetRetargetAvatar;
        public bool ExportMuscleClip;
        public KimodoCurveFilterOptions CurveFilterOptions;
        public bool SkipRetarget;
    }
}
