using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintComposer
    {
        internal static bool TryBuild(
            KimodoInOutConstraintRequest request,
            out KimodoInOutConstraintResult result,
            out string warning,
            out string error)
        {
            result = null;
            warning = string.Empty;
            error = string.Empty;

            if (request == null)
            {
                error = "InOut constraint request is null.";
                return false;
            }

            var built = new KimodoInOutConstraintResult();
            AppendManualSamples(request.ManualSamples, built.CombinedSamples);

            if (!KimodoInOutConstraintTools.TrySampleBoundaryPair(
                    request,
                    out KimodoMarkerSampleResult beginSample,
                    out KimodoMarkerSampleResult endSample,
                    out warning,
                    out error))
            {
                return false;
            }

            built.BeginSample = beginSample;
            built.EndSample = endSample;

            if (beginSample != null && !ContainsSampleTime(request.ManualSamples, beginSample.sampleTime))
            {
                built.CombinedSamples.Add(beginSample);
            }

            if (endSample != null &&
                KimodoInOutConstraintAdapter.ClampFrameCount(request.GenerationFrames) > 1 &&
                !ContainsSampleTime(request.ManualSamples, endSample.sampleTime))
            {
                built.CombinedSamples.Add(endSample);
            }

            if (request.NormalizeConstraintOrigin)
            {
                KimodoInOutConstraintSamplePostProcessor.NormalizeConstraintOrigin(built.CombinedSamples);
            }

            double clipDurationSeconds = KimodoInOutConstraintAdapter.ResolveConstraintClipDurationSeconds(request.GenerationFrames);
            built.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                built.CombinedSamples,
                clipStartSeconds: 0.0,
                clipDurationSeconds: clipDurationSeconds);

            result = built;
            return true;
        }

        private static void AppendManualSamples(
            List<KimodoMarkerSampleResult> source,
            List<KimodoMarkerSampleResult> destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                KimodoMarkerSampleResult sample = source[i];
                if (sample != null)
                {
                    destination.Add(sample.Clone());
                }
            }
        }

        private static bool ContainsSampleTime(List<KimodoMarkerSampleResult> samples, double sampleTime)
        {
            if (samples == null || samples.Count == 0)
            {
                return false;
            }

            long target = ToTimeKey(sampleTime);
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample != null && ToTimeKey(sample.sampleTime) == target)
                {
                    return true;
                }
            }

            return false;
        }

        private static long ToTimeKey(double sampleTime)
        {
            return (long)System.Math.Round(sampleTime * 1000000.0);
        }
    }
}
