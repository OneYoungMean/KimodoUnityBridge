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
        public string ModelName;
        public KimodoTextEncoderMode TextEncoderMode;
        public int TargetFrameCount;
        public float TargetFrameRate = KimodoMotionModelProfiles.DefaultFrameRate;
        public int RuntimeFrameCount;
        public int RuntimeTrimStartFrame;
        // Per-clip overlap settings used when a long runtime is split into generation windows.
        // Keep these on the request so the windowed route remains deterministic after the
        // Timeline asset has been read and asynchronous generation begins.
        public int LongClipWindowFrames60 = 60;
        public int LongClipWindowSampleCount = 2;
        public int DiffusionSteps;
        public int EffectiveSeed;
        public KimodoConstraintPayload Constraints = new KimodoConstraintPayload();
        public string AnalysisOptionsJson;
        internal KimodoEditorGenerateOutputPlan OutputPlan { get; private set; }
        public string ModelsRoot = string.Empty;
        internal AnimationClip TargetClip { get; set; }
        internal AnimationClip RawBoneClip { get; set; }
        public Action<KimodoBridgeCommandStage, string> Progress;
        public CancellationToken Token;
        public bool HasSyntheticAutoBeginConstraint;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
        internal List<KimodoDebugConstraintPass> DebugConstraintPasses = new List<KimodoDebugConstraintPass>();
        internal KimodoDebugTimelineGroup DebugTimelineGroup;
        internal KimodoDebugTimelineReservation DebugTimelineReservation;
        internal bool DebugCaptureUntrimmedMotion;
        internal KimodoRawMotionData DebugRuntimeMotion;
        public string DeferredInConstraintJson = string.Empty;
        public string DeferredOutConstraintJson = string.Empty;
        public string InConstraintWindowJson = string.Empty;
        public string OutConstraintWindowJson = string.Empty;
        // Full window widths in model frames; constraint JSON may contain fewer
        // sample frames and cannot be used to recover this duration.
        public int InConstraintWindowModelFrameCount;
        public int OutConstraintWindowModelFrameCount;
        public TimelineClip TimelineClipSnapshot;
        public bool ResetTimelineTimeScaleAfterGeneration;
        public bool UseRelativeOffset;
        public Vector3 RelativeOffset;
        public float RelativeRootOriginY;
        public Quaternion RelativeRootOriginRotation = Quaternion.identity;
        public PlayableDirector TimelineDirectorSnapshot;
        internal KimodoTimelineInOutConstraintContext TimelineContextSnapshot;
        public ArdyEditorHistorySource InitialArdyHistorySource;
        public double? ArdyHistoryWeight;

        public int EffectiveRuntimeFrameCount =>
            RuntimeFrameCount > 0 ? RuntimeFrameCount : TargetFrameCount;

        public float EffectiveRuntimeDurationSeconds =>
            EffectiveRuntimeFrameCount / TargetFrameRate;

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

    internal sealed class KimodoEditorGenerationPass
    {
        private readonly Func<string, string> applyConstraints;

        internal KimodoEditorGenerationPass(string name, Func<string, string> applyConstraints)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Generation pass name is required.", nameof(name));
            this.applyConstraints = applyConstraints ??
                throw new ArgumentNullException(nameof(applyConstraints));
            Name = name;
        }

        internal string Name { get; }

        internal string Apply(string constraintsJson)
        {
            return applyConstraints(constraintsJson ?? string.Empty) ?? string.Empty;
        }
    }

    internal sealed class KimodoEditorGenerationStage
    {
        private readonly List<KimodoEditorGenerationPass> passes = new List<KimodoEditorGenerationPass>();

        internal KimodoEditorGenerationStage(
            int index,
            string name,
            KimodoEditorGenerateRequest request,
            KimodoRawMotionData sourceMotion = null)
        {
            if (index < 1)
                throw new ArgumentOutOfRangeException(nameof(index), "Generation stage index must be positive.");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Generation stage name is required.", nameof(name));
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Index = index;
            Name = name;
            SourceMotion = sourceMotion;
        }

        internal int Index { get; }
        internal string Name { get; }
        internal KimodoEditorGenerateRequest Request { get; }
        internal KimodoRawMotionData SourceMotion { get; }
        internal KimodoRawMotionData ResultMotion { get; set; }
        internal float FrameRate => ResultMotion?.FrameRate > 0f
            ? ResultMotion.FrameRate
            : Request.TargetFrameRate;
        internal int FrameCount => ResultMotion?.FrameCount > 0
            ? ResultMotion.FrameCount
            : Request.TargetFrameCount;
        internal double DurationSeconds => FrameRate > 0f ? FrameCount / (double)FrameRate : 0d;
        internal string ConstraintsJson => Request.Constraints?.json ?? string.Empty;
        internal IReadOnlyList<KimodoEditorGenerationPass> Passes => passes;

        internal void AddPass(KimodoEditorGenerationPass pass)
        {
            passes.Add(pass ?? throw new ArgumentNullException(nameof(pass)));
        }

        internal string ApplyPasses()
        {
            string constraintsJson = Request.Constraints?.json ?? string.Empty;
            for (int i = 0; i < passes.Count; i++)
            {
                constraintsJson = passes[i].Apply(constraintsJson);
            }

            // A Stage exposes one composed constraint set to the next stage and
            // to the debug timeline. Fold same-frame Root2D rows into the
            // matching root-context row here so the stage view follows the
            // protocol rule before serialization reaches the backend.
            if (!string.IsNullOrWhiteSpace(constraintsJson))
            {
                JToken parsed = JToken.Parse(constraintsJson);
                if (parsed is JArray array)
                {
                    constraintsJson = KimodoConstraintProtocolNormalizer
                        .NormalizeRoot2DIntoFullBody(array)
                        .ToString(Formatting.None);
                }
            }
            if (Request.Constraints == null)
                Request.Constraints = new KimodoConstraintPayload();
            Request.Constraints.json = constraintsJson;
            return constraintsJson;
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
