using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.Manager
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

    public enum KimodoBridgeOperation
    {
        Start = 0,
        Stop = 1,
        TryFix = 2,
        DeleteAllData = 3,
        RefreshStatus = 4,
        EnsureRuntimeRoot = 5
    }

    public sealed class BridgeControlCommand : KimodoEditorCommandBase
    {
        public BridgeControlCommand(
            KimodoBridgeOperation operation,
            string runtimeRoot,
            string modelName,
            KimodoBridgeVramMode vramMode,
            string modelsRootOverride)
            : base(BuildTargetKey(operation), MapKind(operation))
        {
            Operation = operation;
            RuntimeRoot = runtimeRoot ?? string.Empty;
            ModelName = modelName ?? string.Empty;
            VramMode = vramMode;
            ModelsRootOverride = modelsRootOverride ?? string.Empty;
        }

        public KimodoBridgeOperation Operation { get; }

        public string RuntimeRoot { get; }

        public string ModelName { get; }

        public KimodoBridgeVramMode VramMode { get; }

        public string ModelsRootOverride { get; }

        private static string BuildTargetKey(KimodoBridgeOperation operation)
        {
            switch (operation)
            {
                case KimodoBridgeOperation.RefreshStatus:
                    return "bridge:refresh";
                case KimodoBridgeOperation.EnsureRuntimeRoot:
                case KimodoBridgeOperation.TryFix:
                case KimodoBridgeOperation.DeleteAllData:
                    return "bridge:maintenance";
                case KimodoBridgeOperation.Start:
                case KimodoBridgeOperation.Stop:
                default:
                    return "bridge:control";
            }
        }

        private static KimodoEditorCommandKind MapKind(KimodoBridgeOperation operation)
        {
            switch (operation)
            {
                case KimodoBridgeOperation.Start:
                    return KimodoEditorCommandKind.BridgeStartServer;
                case KimodoBridgeOperation.Stop:
                    return KimodoEditorCommandKind.BridgeStopServer;
                case KimodoBridgeOperation.TryFix:
                    return KimodoEditorCommandKind.BridgeTryFix;
                case KimodoBridgeOperation.DeleteAllData:
                    return KimodoEditorCommandKind.BridgeDeleteAllData;
                case KimodoBridgeOperation.RefreshStatus:
                    return KimodoEditorCommandKind.BridgeRefreshStatus;
                case KimodoBridgeOperation.EnsureRuntimeRoot:
                    return KimodoEditorCommandKind.BridgeEnsureRuntimeRoot;
                default:
                    return KimodoEditorCommandKind.Unknown;
            }
        }
    }
}
