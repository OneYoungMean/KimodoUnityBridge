using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.Manager
{
    public interface IKimodoEditorCommandResult
    {
    }

    public sealed class KimodoEditorNoopResult : IKimodoEditorCommandResult
    {
        public static readonly KimodoEditorNoopResult Instance = new KimodoEditorNoopResult();

        private KimodoEditorNoopResult()
        {
        }
    }

    public sealed class KimodoEditorBridgeOperationResult : IKimodoEditorCommandResult
    {
        public bool Running;
        public bool HasPort;
        public string Host;
        public int Port;
        public string Status;
        public string Error;
    }

    public sealed class KimodoEditorGenerateResult : IKimodoEditorCommandResult
    {
        public string ConstraintsPath;
        public string Prompt;
        public int Seed;
        public AnimationClip GeneratedClip;
    }
}
