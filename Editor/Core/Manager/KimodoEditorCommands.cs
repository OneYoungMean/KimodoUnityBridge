using UnityEngine;

namespace KimodoBridge.Editor
{
    public sealed class KimodoExternalConstraintRequest
    {
        public string ConstraintsJson;
        public bool Enabled;
        public Avatar RetargetAvatar;
    }

    public sealed class GeneratePlayableClipCommand : KimodoEditorCommandBase
    {
        public GeneratePlayableClipCommand(
            KimodoPlayableClip clip,
            string promptOverride = null,
            KimodoExternalConstraintRequest externalConstraint = null)
            : base(BuildTargetKey(clip), KimodoEditorCommandKind.GeneratePlayableClip)
        {
            Clip = clip;
            PromptOverride = promptOverride;
            ExternalConstraint = externalConstraint;
        }

        public KimodoPlayableClip Clip { get; }

        public string PromptOverride { get; }

        public KimodoExternalConstraintRequest ExternalConstraint { get; }

        private static string BuildTargetKey(KimodoPlayableClip clip)
        {
            return clip == null ? "clip:null" : "clip:" + clip.GetInstanceID();
        }
    }

    public sealed class GenerateFromPromptCommand : KimodoEditorCommandBase
    {
        public GenerateFromPromptCommand(
            int clipInstanceId,
            string promptOverride,
            KimodoExternalConstraintRequest externalConstraint = null)
            : base("clip:" + clipInstanceId, KimodoEditorCommandKind.GeneratePlayableClip)
        {
            ClipInstanceId = clipInstanceId;
            PromptOverride = promptOverride ?? string.Empty;
            ExternalConstraint = externalConstraint;
        }

        public int ClipInstanceId { get; }

        public string PromptOverride { get; }

        public KimodoExternalConstraintRequest ExternalConstraint { get; }
    }

    public sealed class CancelPlayableClipGenerationCommand : KimodoEditorCommandBase
    {
        public CancelPlayableClipGenerationCommand(KimodoPlayableClip clip)
            : base(clip == null ? "clip:null" : "clip:" + clip.GetInstanceID(), KimodoEditorCommandKind.CancelPlayableClipGeneration)
        {
            Clip = clip;
        }

        public KimodoPlayableClip Clip { get; }
    }

}
