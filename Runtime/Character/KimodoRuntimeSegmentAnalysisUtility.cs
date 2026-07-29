using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRuntimeSegmentAnalysisUtility
    {
        public static int ResolveEffectiveLastFrameIndex(
            KimodoRawMotionData motion,
            KimodoSegmentTrimTrailSettings settings)
        {
            if (motion == null || motion.FrameCount <= 1)
            {
                return 0;
            }

            settings ??= new KimodoSegmentTrimTrailSettings();
            int lastFrameIndex = motion.FrameCount - 1;
            float frameRate = ResolveFrameRate(motion);

            if (settings.Mode == KimodoSegmentSamplingMode.ByTime)
            {
                int trimFrames = KimodoFrameTimeUtility.SecondsToFrameCount(settings.TrimTimeSeconds, frameRate);
                return Mathf.Clamp(lastFrameIndex - trimFrames, 1, lastFrameIndex);
            }

            int maxTrimFrames = Mathf.Clamp(
                KimodoFrameTimeUtility.SecondsToFrameCount(settings.MaxTrimTimeSeconds, frameRate),
                0,
                lastFrameIndex);
            if (maxTrimFrames <= 0)
            {
                return lastFrameIndex;
            }

            float thresholdSq = settings.DeltaThresholdMeters * settings.DeltaThresholdMeters;
            int effectiveLastFrameIndex = lastFrameIndex;
            int scannedFrames = 0;
            for (int frameIndex = lastFrameIndex; frameIndex > 0 && scannedFrames < maxTrimFrames; frameIndex--, scannedFrames++)
            {
                if (!TryReadRootDeltaXZSquared(motion, frameIndex - 1, frameIndex, out float deltaSq))
                {
                    break;
                }

                if (deltaSq > thresholdSq)
                {
                    break;
                }

                effectiveLastFrameIndex = frameIndex - 1;
            }

            return Mathf.Clamp(effectiveLastFrameIndex, 1, lastFrameIndex);
        }

        public static List<KimodoMarkerSampleResult> BuildConstraintOverlapPoses(
            KimodoRawMotionData motion,
            string modelName,
            int effectiveLastFrameIndex,
            KimodoSegmentOverlapHeadSettings settings,
            bool allowPartialJoints)
        {
            var samples = new List<KimodoMarkerSampleResult>();
            if (motion == null || motion.FrameCount <= 0)
            {
                return samples;
            }

            settings ??= new KimodoSegmentOverlapHeadSettings();
            float frameRate = ResolveFrameRate(motion);
            int overlapStartFrameIndex = ResolveOverlapStartFrameIndex(motion, effectiveLastFrameIndex, settings, frameRate);
            int windowFrameCount = Mathf.Max(1, effectiveLastFrameIndex - overlapStartFrameIndex + 1);
            int sampleCount = Mathf.Clamp(settings.SampleCount, 1, windowFrameCount);

            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                int sourceFrameIndex = sampleCount == 1
                    ? effectiveLastFrameIndex
                    : Mathf.RoundToInt(Mathf.Lerp(overlapStartFrameIndex, effectiveLastFrameIndex, sampleIndex / (float)(sampleCount - 1)));
                sourceFrameIndex = Mathf.Clamp(sourceFrameIndex, overlapStartFrameIndex, effectiveLastFrameIndex);

                if (!KimodoRawMotionUtility.TryExtractMarkerSample(
                        motion,
                        modelName,
                        sourceFrameIndex,
                        out KimodoMarkerSampleResult sample,
                        out string _,
                        "fullbody",
                        (effectiveLastFrameIndex - sourceFrameIndex) / Mathf.Max(1e-6f, frameRate),
                        allowPartialJoints))
                {
                    continue;
                }

                if (samples.Count > 0)
                {
                    KimodoMarkerSampleResult previous = samples[samples.Count - 1];
                    if (Mathf.Approximately((float)previous.sampleTime, (float)sample.sampleTime))
                    {
                        continue;
                    }
                }

                samples.Add(sample);
            }

            return samples;
        }

        private static int ResolveOverlapStartFrameIndex(
            KimodoRawMotionData motion,
            int effectiveLastFrameIndex,
            KimodoSegmentOverlapHeadSettings settings,
            float frameRate)
        {
            effectiveLastFrameIndex = Mathf.Clamp(effectiveLastFrameIndex, 0, Mathf.Max(0, motion.FrameCount - 1));
            if (settings.Mode == KimodoSegmentSamplingMode.ByTime)
            {
                int overlapFrames = KimodoFrameTimeUtility.SecondsToFrameCount(settings.OverlapTimeSeconds, frameRate);
                return Mathf.Clamp(effectiveLastFrameIndex - overlapFrames, 0, effectiveLastFrameIndex);
            }

            int maxOverlapFrames = Mathf.Clamp(
                KimodoFrameTimeUtility.SecondsToFrameCount(settings.MaxOverlapTimeSeconds, frameRate),
                0,
                effectiveLastFrameIndex);
            if (maxOverlapFrames <= 0)
            {
                return effectiveLastFrameIndex;
            }

            float thresholdSq = settings.DeltaThresholdMeters * settings.DeltaThresholdMeters;
            int overlapStartFrameIndex = effectiveLastFrameIndex;
            int scannedFrames = 0;
            for (int frameIndex = effectiveLastFrameIndex; frameIndex > 0 && scannedFrames < maxOverlapFrames; frameIndex--, scannedFrames++)
            {
                if (!TryReadRootDeltaXZSquared(motion, frameIndex - 1, frameIndex, out float deltaSq))
                {
                    break;
                }

                if (deltaSq < thresholdSq)
                {
                    break;
                }

                overlapStartFrameIndex = frameIndex - 1;
            }

            return Mathf.Clamp(overlapStartFrameIndex, 0, effectiveLastFrameIndex);
        }

        private static bool TryReadRootDeltaXZSquared(
            KimodoRawMotionData motion,
            int frameIndex0,
            int frameIndex1,
            out float deltaSq)
        {
            deltaSq = 0f;
            if (motion == null ||
                !motion.TryReadUnityRootPosition(frameIndex0, out Vector3 root0) ||
                !motion.TryReadUnityRootPosition(frameIndex1, out Vector3 root1))
            {
                return false;
            }

            Vector2 delta = new Vector2(root1.x - root0.x, root1.z - root0.z);
            deltaSq = delta.sqrMagnitude;
            return true;
        }

        private static float ResolveFrameRate(KimodoRawMotionData motion)
        {
            return motion != null && motion.FrameRate > 1e-6f
                ? motion.FrameRate
                : KimodoPlayableClip.FIXED_FRAME_RATE;
        }
    }
}
