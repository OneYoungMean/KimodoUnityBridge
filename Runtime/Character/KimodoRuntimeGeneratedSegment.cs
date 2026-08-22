using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeGeneratedSegment
    {
        public int Index;
        public string PromptText;
        public KimodoRawMotionData Motion;
        public List<KimodoConstraintInternalData> ConstraintOverlapInternalData;
        public Vector3 FirstRootPosition;
        public Vector3 LastRootPosition;
        public Vector3 WorldAccumulatedOffset;
        public int EffectiveLastFrameIndex;
        public float EffectiveLastFrameTimeSeconds;
        public byte[] MotionBytes;
        public string MotionRepFingerprint;
        public int? ResolvedSeed;
        public bool UseRawRootPosition;
    }
}
