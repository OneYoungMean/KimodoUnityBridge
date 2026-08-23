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
                : KimodoMotionModelProfiles.DefaultFrameRate;
        }

    }
}
