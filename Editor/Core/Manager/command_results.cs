using UnityEngine;

namespace CharacterAnimationCli.Unity.Command
{
    public interface command_result
    {
    }

    public sealed class command_noop_result : command_result
    {
        public static readonly command_noop_result Instance = new command_noop_result();

        private command_noop_result()
        {
        }
    }

    public sealed class command_generate_result : command_result
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
