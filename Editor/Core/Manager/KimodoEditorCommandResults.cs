using UnityEngine;
using System.Collections.Generic;

namespace KimodoBridge.Editor
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

    public sealed class KimodoEditorGenerateResult : IKimodoEditorCommandResult
    {
        public string ConstraintsPath;
        public string Prompt;
        public int Seed;
        public string MotionJsonCompact;
        public string AnalysisJson;
        public AnimationClip GeneratedClip;
        public AnimationClip RawBoneClip;
        public string ArdyMotionCachePath;
        public string ArdyMotionRepFingerprint;
        public List<int> ArdyResolvedSeeds;
    }
}
