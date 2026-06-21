using System;
using System.Threading;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGenerateRequest
    {
        public string Prompt;
        public string ModelName;
        public KimodoGenerationBackend GenerationBackend;
        public KimodoBridgeVramMode BridgeVramMode;
        public float DurationSeconds;
        public int DiffusionSteps;
        public int EffectiveSeed;
        public string ConstraintsJson;
        public Func<AnimationClip> CreateTargetClip;
        public Func<AnimationClip, string, KimodoEditorGenerateOutputPlan> ResolveOutputPlan;
        public Avatar OriginRetargetAvatar;
        public Avatar TargetRetargetAvatar;
        public bool ExportMuscleClip;
        public KimodoCurveFilterOptions CurveFilterOptions;
        public bool SkipRetarget;
        public string ModelsRoot = string.Empty;
        public string ComfyHost = "127.0.0.1";
        public int ComfyPort = 8188;
        public float GenerationTimeoutSeconds = 120f;
        public AnimationClip TargetClip;
        public AnimationClip RawBoneClip;
        public Action<KimodoGeneratePipelineStage, string> Progress;
        public CancellationToken Token;
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
