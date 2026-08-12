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
        public float TargetFrameRate = KimodoMotionModelProfiles.DefaultFrameRate;
        public int RuntimeFrameCount;
        public int RuntimeTrimStartFrame;
        public int DiffusionSteps;
        public int EffectiveSeed;
        public KimodoConstraintPayload Constraints = new KimodoConstraintPayload();
        public string AnalysisOptionsJson;
        public Func<AnimationClip> CreateTargetClip;
        public Func<AnimationClip, string, KimodoEditorGenerateOutputPlan> ResolveOutputPlan;
        public KimodoEditorGenerateOutputPlan OutputPlan;
        public string ModelsRoot = string.Empty;
        public AnimationClip TargetClip;
        public AnimationClip RawBoneClip;
        public Action<KimodoBridgeCommandStage, string> Progress;
        public CancellationToken Token;
        public bool HasSyntheticAutoBeginConstraint;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
        public TimelineClip TimelineClipSnapshot;
        public bool ResetTimelineTimeScaleAfterGeneration;
        public PlayableDirector TimelineDirectorSnapshot;
        public ArdyEditorHistorySource InitialArdyHistorySource;
        public double? ArdyHistoryWeight;
        public double? ArdyMaxSpeed;
        public double? ArdyMaxAcceleration;

        public int EffectiveRuntimeFrameCount =>
            RuntimeFrameCount > 0 ? RuntimeFrameCount : TargetFrameCount;

        public float EffectiveRuntimeDurationSeconds =>
            EffectiveRuntimeFrameCount / TargetFrameRate;
    }

    internal sealed class ArdyEditorHistorySource
    {
        public KimodoTimelineInOutConstraintContext TimelineContext;
        public double RangeStartSeconds;
        public double RangeEndSeconds;
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
