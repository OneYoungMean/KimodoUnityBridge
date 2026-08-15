using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGenerationResult
    {
        public string ConstraintsPath;
        public string Prompt;
        public int Seed;
        public string MotionJsonCompact;
        public string AnalysisJson;
        public byte[] MotionBytes;
        public int StartFrame;
        public int EndFrameExclusive;
        public AnimationClip GeneratedClip;
        public AnimationClip RawBoneClip;
    }
}
