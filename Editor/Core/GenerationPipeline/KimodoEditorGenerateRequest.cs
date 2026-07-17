using System;
using System.Collections.Generic;
using System.Threading;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGenerateRequest
    {
        public string Prompt;
        public string ModelName;
        public KimodoTextEncoderMode TextEncoderMode;
        public float DurationSeconds;
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
        public bool NormalizeConstraintOriginApplied;
        public KimodoConstraintNormalizationAnchorKind NormalizationAnchorKind;
        public KimodoMarkerSampleResult NormalizationAnchorSample;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
        public ArdyEditorHistorySource InitialArdyHistorySource;
        public string GeneratedArdyMotionCachePath = string.Empty;
        public List<string> GeneratedArdyWindowCachePaths = new List<string>();
        public List<string> GeneratedArdyHandles = new List<string>();
        public List<int> GeneratedArdySeeds = new List<int>();
        public string GeneratedArdyFingerprint = string.Empty;
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
