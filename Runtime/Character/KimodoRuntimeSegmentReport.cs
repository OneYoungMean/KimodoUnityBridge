using UnityEngine;

namespace KimodoBridge
{
    public sealed class KimodoRuntimeSegmentReport
    {
        public int Index;
        public string PromptText;
        public Vector3 FirstRootPosition;
        public Vector3 EffectiveLastRootPosition;
        public int EffectiveLastFrameIndex;
        public float EffectiveLastFrameTimeSeconds;
        public float MotionDurationSeconds;
    }
}
