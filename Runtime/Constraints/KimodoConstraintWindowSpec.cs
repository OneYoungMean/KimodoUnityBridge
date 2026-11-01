using System;

namespace KimodoBridge
{
    /// <summary>Resolved model-frame layout for a constraint window.</summary>
    public sealed class KimodoConstraintWindowSpec
    {
        public int ModelFrameCount { get; }
        public int SampleCount { get; }

        private KimodoConstraintWindowSpec(int modelFrameCount, int sampleCount)
        {
            ModelFrameCount = Math.Max(0, modelFrameCount);
            SampleCount = Math.Max(1, sampleCount);
        }

        public static KimodoConstraintWindowSpec FromCommandFrames60(
            int commandFrameCount60,
            int sampleCount,
            double modelFrameRate)
        {
            int modelFrames = KimodoFrameTimeUtility.CommandFramesToModelFrameCount(
                commandFrameCount60,
                modelFrameRate);
            return FromModelFrames(modelFrames, sampleCount);
        }

        public static KimodoConstraintWindowSpec FromModelFrames(int modelFrameCount, int sampleCount)
        {
            return new KimodoConstraintWindowSpec(modelFrameCount, sampleCount);
        }

        public KimodoConstraintWindowSpec WithModelFrameCount(int modelFrameCount)
        {
            return FromModelFrames(modelFrameCount, SampleCount);
        }
    }
}
