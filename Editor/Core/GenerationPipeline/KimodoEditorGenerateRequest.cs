using System;
using System.Threading;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGenerateRequest
    {
        public string Prompt;
        public string ModelName;
        public KimodoBridgeVramMode BridgeVramMode;
        public float DurationSeconds;
        public int DiffusionSteps;
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
