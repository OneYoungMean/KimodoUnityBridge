using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGenerateRequest
    {
        private Func<AnimationClip> createTargetClip;
        private Func<AnimationClip, string, KimodoEditorGenerateOutputPlan> resolveOutputPlan;

        internal KimodoEditorGenerateRequest()
        {
        }

        internal KimodoEditorGenerateRequest(
            Func<AnimationClip> targetClipFactory,
            Func<AnimationClip, string, KimodoEditorGenerateOutputPlan> outputPlanResolver,
            KimodoEditorGenerateOutputPlan outputPlan)
        {
            createTargetClip = targetClipFactory;
            resolveOutputPlan = outputPlanResolver;
            OutputPlan = outputPlan;
        }

        public string Prompt;
        internal string RawGenerateInstructionJson;
        public string ModelName;
        public KimodoTextEncoderMode TextEncoderMode;
        public int TargetFrameCount;
        public float TargetFrameRate = KimodoMotionModelProfiles.DefaultFrameRate;
        public int RuntimeFrameCount;
        public int RuntimeTrimStartFrame;
        // Requests one real endpoint sample from the backend while keeping the visible frame count unchanged.
        internal bool RequestEndpointSample;
        // Full output interval including the endpoint, used only when baking curve coverage.
        internal string BakeMotionJsonCompact = string.Empty;
        internal KimodoRawMotionData BakeMotionData;
        // Server-owned orchestration fields. The editor sends these once and
        // never mutates the request between stages.
        public string StagePlanJson = string.Empty;
        public string WindowConstraintsJson = string.Empty;
        public bool DebugGeneration;
        public int DiffusionSteps;
        public int EffectiveSeed;
        public KimodoConstraintPayload Constraints = new KimodoConstraintPayload();
        internal KimodoEditorGenerateOutputPlan OutputPlan { get; private set; }
        public string ModelsRoot = string.Empty;
        internal AnimationClip TargetClip { get; set; }
        internal AnimationClip RawBoneClip { get; set; }
        public Action<KimodoBridgeCommandStage, string> Progress;
        public CancellationToken Token;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
        internal List<KimodoDebugConstraintPass> DebugConstraintPasses = new List<KimodoDebugConstraintPass>();
        internal KimodoDebugTimelineGroup DebugTimelineGroup;
        internal KimodoDebugTimelineReservation DebugTimelineReservation;
        internal KimodoRawMotionData DebugRuntimeMotion;
        internal string DebugSegmentsJson = string.Empty;
        internal string DebugConstraintsJson = string.Empty;
        public TimelineClip TimelineClipSnapshot;
        public bool ResetTimelineTimeScaleAfterGeneration;
        public PlayableDirector TimelineDirectorSnapshot;
        internal KimodoTimelineInOutConstraintContext TimelineContextSnapshot;
        public ArdyEditorHistorySource InitialArdyHistorySource;
        public double? ArdyHistoryWeight;

        public int EffectiveRuntimeFrameCount =>
            RuntimeFrameCount > 0 ? RuntimeFrameCount : TargetFrameCount;

        public float EffectiveRuntimeDurationSeconds =>
            EffectiveRuntimeFrameCount / TargetFrameRate;

        internal int EffectiveBackendRuntimeFrameCount =>
            EffectiveRuntimeFrameCount + (RequestEndpointSample ? 1 : 0);

        internal float EffectiveBackendRuntimeDurationSeconds =>
            EffectiveBackendRuntimeFrameCount / TargetFrameRate;

        internal void CreateTargetClip()
        {
            if (createTargetClip == null)
            {
                return;
            }

            Func<AnimationClip> factory = createTargetClip;
            createTargetClip = null;
            TargetClip = factory() ?? throw new InvalidOperationException("Created target clip is null.");
        }

        internal KimodoEditorGenerateOutputPlan ResolveOutputPlan(string modelName)
        {
            if (resolveOutputPlan == null)
            {
                return OutputPlan;
            }

            Func<AnimationClip, string, KimodoEditorGenerateOutputPlan> resolver = resolveOutputPlan;
            resolveOutputPlan = null;
            OutputPlan = resolver(TargetClip, modelName) ??
                throw new InvalidOperationException("Output plan is null.");
            return OutputPlan;
        }

        internal void CleanupGeneratedClips()
        {
            TryCleanupGeneratedClip(TargetClip);
            if (!ReferenceEquals(RawBoneClip, TargetClip))
            {
                TryCleanupGeneratedClip(RawBoneClip);
            }
            TargetClip = null;
            RawBoneClip = null;
        }

        private static void TryCleanupGeneratedClip(AnimationClip clip)
        {
            if (clip == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(clip)))
            {
                UnityEngine.Object.DestroyImmediate(clip);
                return;
            }
            KimodoEditorClipWritebackService.TryDeleteGeneratedAnimationClipAsset(clip);
        }
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
