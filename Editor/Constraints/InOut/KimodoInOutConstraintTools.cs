using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintTools
    {
        private const string FullBodyConstraintType = "fullbody";
        private const double BoundarySampleEpsilonSeconds = 1e-7;

        // Window lengths and sampling positions use the model's FPS, independent of Timeline's UI FPS.
        internal static int ResolveWindowFrameCount(KimodoInOutConstraintRequest request, bool isBegin)
        {
            if (request == null || request.Mode == KimodoInOutConstraintMode.None ||
                !(isBegin ? request.EnableBegin : request.EnableEnd)) return 0;
            int requested = Mathf.Clamp(isBegin ? request.BeginWindowFrames : request.EndWindowFrames,
                1, KimodoMotionModelProfiles.MaxGenerationFrames);
            var context = request.TimelineContext;
            if (context == null) return Math.Min(requested, Math.Max(1, request.GenerationFrames));
            TimelineClip range = request.Mode == KimodoInOutConstraintMode.Inside
                ? context.SourceClip : isBegin ? context.PreviousTimelineClip : context.NextTimelineClip;
            if (range == null) return 0;
            double fps = KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName);
            double boundary = ResolveWindowBoundaryTime(request, isBegin);
            bool forward = (request.Mode == KimodoInOutConstraintMode.Inside) == isBegin;
            double available = forward ? Math.Max(range.start, range.end - 1.0 / fps) - boundary
                : boundary - range.start;
            int count = Math.Max(1, (int)Math.Floor(Math.Max(0.0, available) * fps + 1e-5) + 1);
            return Math.Min(Math.Min(requested, count), Math.Max(1, request.GenerationFrames));
        }

        private static double ResolveWindowBoundaryTime(KimodoInOutConstraintRequest request, bool isBegin)
        {
            var context = request.TimelineContext;
            double step = 1.0 / KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName);
            if (request.Mode == KimodoInOutConstraintMode.Outside)
                return ClampTimelineSampleTime(isBegin ? context.PreviousTimelineClip : context.NextTimelineClip,
                    isBegin ? context.SourceClip.start - step : context.SourceClip.end + BoundarySampleEpsilonSeconds, step);
            return ClampTimelineSampleTime(context.SourceClip,
                isBegin ? context.SourceClip.start : context.SourceClip.end - step, step);
        }

        internal static void BuildBoundarySampleTimes(KimodoInOutConstraintRequest request, bool isBegin,
            out double[] timelineTimes, out double[] exportTimes)
        {
            int frames = ResolveWindowFrameCount(request, isBegin);
            int count = frames == 0 ? 0 : Mathf.Clamp(isBegin ? request.BeginSampleCount : request.EndSampleCount, 1, frames);
            timelineTimes = new double[count];
            exportTimes = new double[count];
            if (count == 0) return;
            double fps = KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName);
            bool forward = (request.Mode == KimodoInOutConstraintMode.Inside) == isBegin;
            double first = ResolveWindowBoundaryTime(request, isBegin) - (forward ? 0 : (frames - 1) / fps);
            int exportStart = isBegin ? 0 : Math.Max(0, request.GenerationFrames - frames);
            for (int i = 0; i < count; i++)
            {
                // A single sample always keeps the seam pose; two or more include both endpoints.
                int frame = count == 1 ? (forward ? 0 : frames - 1)
                    : (int)Math.Round(i * (frames - 1.0) / (count - 1), MidpointRounding.AwayFromZero);
                timelineTimes[i] = first + frame / fps;
                exportTimes[i] = (exportStart + frame) / fps;
            }
        }

        internal static bool TrySampleBoundaries(KimodoInOutConstraintRequest request,
            out List<KimodoMarkerSampleResult> beginSamples, out List<KimodoMarkerSampleResult> endSamples,
            out string warning, out string error)
        {
            beginSamples = new List<KimodoMarkerSampleResult>();
            endSamples = new List<KimodoMarkerSampleResult>();
            warning = error = string.Empty;
            if (request == null || request.TimelineContext == null ||
                KimodoMotionModelProfiles.TryGetArdy(request.ModelName, out _))
            {
                if (request?.TimelineContext == null && request?.Mode != KimodoInOutConstraintMode.None &&
                    ((request?.BeginSampleCount ?? 1) > 1 || (request?.EndSampleCount ?? 1) > 1))
                {
                    error = "Multi-frame In/Out sampling requires a Timeline context.";
                    return false;
                }
                if (!TrySampleBoundaryPair(request, out var begin, out var end, out warning, out error)) return false;
                if (begin != null) beginSamples.Add(begin);
                if (end != null) endSamples.Add(end);
                return true;
            }
            BuildBoundarySampleTimes(request, true, out var inTimes, out var inExports);
            BuildBoundarySampleTimes(request, false, out var outTimes, out var outExports);
            if (inTimes.Length + outTimes.Length == 0) return true;
            if (request.Mode == KimodoInOutConstraintMode.Inside &&
                ResolveWindowFrameCount(request, true) + ResolveWindowFrameCount(request, false) > request.GenerationFrames &&
                (inTimes.Length > 1 || outTimes.Length > 1))
            {
                error = "In and Out sampling windows overlap. Reduce their window frame counts.";
                return false;
            }
            var times = new List<double>(inTimes);
            times.AddRange(outTimes);
            var exports = new List<double>(inExports);
            exports.AddRange(outExports);
            if (!KimodoTimelineSamplingSession.TryCreate(request.TimelineContext, request.ModelName, out var sampler, out error))
                return false;
            using (sampler)
            {
                if (!sampler.TryCaptureTargetBoneSamples(times.ToArray(),
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName), out var bones, out error)) return false;
                for (int i = 0; i < times.Count; i++)
                {
                    if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        bones[i], sampler.TargetCache, request.ModelName, FullBodyConstraintType, exports[i], out var sample, out error))
                        return false;
                    sample.enableMask = KimodoConstraintMask.ForType(FullBodyConstraintType);
                    (i < inTimes.Length ? beginSamples : endSamples).Add(sample);
                }
            }
            return true;
        }

        internal static void ResolveOutsideContextFrames(TimelineClip clip, out int before, out int after)
        {
            before = after = 0;
            if (clip?.asset is not KimodoPlayableClip playable ||
                playable.inOutConstraintMode != KimodoInOutConstraintMode.Outside ||
                KimodoMotionModelProfiles.TryGetArdy(playable.bridgeModelName, out _)) return;
            KimodoInOutConstraintAdapter.TryResolveNeighborTimelineClips(clip, out var previous, out var next);
            var request = KimodoInOutConstraintAdapter.BuildTimelineRequest(new KimodoTimelineInOutConstraintContext
            {
                SourceClip = clip, PreviousTimelineClip = previous, NextTimelineClip = next,
                ModelName = playable.bridgeModelName
            }, playable.inOutConstraintMode, false, true, playable.enableInConstraint, playable.enableOutConstraint,
                KimodoMotionModelProfiles.MaxGenerationFrames, null);
            before = ResolveWindowFrameCount(request, true);
            after = ResolveWindowFrameCount(request, false);
        }

        internal static bool TrySampleBoundaryPair(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult beginSample,
            out KimodoMarkerSampleResult endSample,
            out string warning,
            out string error)
        {
            beginSample = null;
            endSample = null;
            warning = string.Empty;
            error = string.Empty;

            if (request == null)
            {
                error = "InOut constraint request is null.";
                return false;
            }

            if (request.Mode == KimodoInOutConstraintMode.None)
            {
                return true;
            }

            if (request.TimelineContext != null)
            {
                // ponytail: Timeline already resolves clip ranges, blends and offsets.
                return TrySampleTimelineBoundaryPair(
                    request,
                    out beginSample,
                    out endSample,
                    out warning,
                    out error);
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(request.SourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (request.EnableBegin &&
                !TrySampleBoundaryPose(
                    request.BeginSegment,
                    request.SourceAvatar,
                    request.ModelName,
                    ResolveBoundaryNormalizedTime(request.Mode, isBegin: true),
                    0.0,
                    out beginSample,
                    out error))
            {
                return false;
            }

            if (request.EnableEnd &&
                !TrySampleBoundaryPose(
                    request.EndSegment,
                    request.SourceAvatar,
                    request.ModelName,
                    ResolveBoundaryNormalizedTime(request.Mode, isBegin: false),
                    ResolveConstraintEndSampleTimeSeconds(
                        request.GenerationFrames,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName)),
                    out endSample,
                    out error))
            {
                return false;
            }

            if (!request.EnableBegin && !request.EnableEnd)
            {
                warning = "InOut constraint request has no enabled boundary segments.";
            }

            return true;
        }

        private static bool TrySampleTimelineBoundaryPair(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult beginSample,
            out KimodoMarkerSampleResult endSample,
            out string warning,
            out string error)
        {
            beginSample = null;
            endSample = null;
            warning = string.Empty;
            error = string.Empty;
            if (request.EnableBegin &&
                !KimodoTimelineConstraintSampler.TrySampleMarker(
                    request.TimelineContext,
                    ResolveTimelineBoundaryTime(request, isBegin: true),
                    0.0,
                    FullBodyConstraintType,
                    request.ModelName,
                    out beginSample,
                    out error))
            {
                return false;
            }
            if (request.EnableBegin)
            {
                LogBoundaryRoot("In", beginSample, request.Mode,
                    ResolveTimelineBoundaryTime(request, isBegin: true));
            }

            if (request.EnableEnd &&
                !KimodoTimelineConstraintSampler.TrySampleMarker(
                    request.TimelineContext,
                    ResolveTimelineBoundaryTime(request, isBegin: false),
                    ResolveConstraintEndSampleTimeSeconds(
                        request.GenerationFrames,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName)),
                    FullBodyConstraintType,
                    request.ModelName,
                    out endSample,
                    out error))
            {
                return false;
            }
            if (request.EnableEnd)
            {
                LogBoundaryRoot("Out", endSample, request.Mode,
                    ResolveTimelineBoundaryTime(request, isBegin: false));
            }

            if (!request.EnableBegin && !request.EnableEnd)
            {
                warning = "InOut constraint request has no enabled boundary segments.";
            }
            return true;
        }

        private static void LogBoundaryRoot(
            string boundary,
            KimodoMarkerSampleResult sample,
            KimodoInOutConstraintMode mode,
            double timelineTime)
        {
            if (sample?.rootOverride == null) return;
            Debug.Log($"[Kimodo][InOutConstraint] {boundary} mode={mode} " +
                $"timelineTime={timelineTime:F6}s worldRoot={sample.rootOverride.t}");
        }

        internal static double ResolveTimelineBoundaryTime(KimodoInOutConstraintRequest request, bool isBegin)
        {
            KimodoTimelineInOutConstraintContext context = request.TimelineContext;
            float frameRate = KimodoTimelineConstraintSampler.ResolveTimelineFrameRate(context);
            double oneFrame = 1.0 / frameRate;
            if (request.Mode == KimodoInOutConstraintMode.Outside)
            {
                TimelineClip range = isBegin ? context.PreviousTimelineClip : context.NextTimelineClip;
                double boundary = isBegin ? context.SourceClip.start : context.SourceClip.end;
                return ClampTimelineSampleTime(
                    range,
                    isBegin ? boundary - oneFrame : boundary + BoundarySampleEpsilonSeconds,
                    oneFrame);
            }

            TimelineClip current = context.SourceClip;
            return ClampTimelineSampleTime(
                current,
                isBegin ? current.start : current.end - oneFrame,
                oneFrame);
        }

        private static double ClampTimelineSampleTime(TimelineClip range, double value, double oneFrame)
        {
            double first = range.start;
            double last = Math.Max(first, range.end - oneFrame);
            return Math.Max(first, Math.Min(last, value));
        }

        internal static int ClampFrameCount(int generationFrames)
        {
            return Mathf.Max(KimodoMotionModelProfiles.MinGenerationFrames, generationFrames);
        }

        internal static int DurationSecondsToFrameCount(float durationSeconds)
        {
            float minDurationSeconds = FrameCountToDurationSeconds(KimodoMotionModelProfiles.MinGenerationFrames);
            float maxDurationSeconds = FrameCountToDurationSeconds(KimodoMotionModelProfiles.MaxGenerationFrames);
            return Mathf.Clamp(
                KimodoFrameTimeUtility.SecondsToFrameCount(
                    Mathf.Clamp(durationSeconds, minDurationSeconds, maxDurationSeconds),
                    KimodoMotionModelProfiles.DefaultFrameRate),
                KimodoMotionModelProfiles.MinGenerationFrames,
                KimodoMotionModelProfiles.MaxGenerationFrames);
        }

        internal static float FrameCountToDurationSeconds(int frameCount)
        {
            return Mathf.Max(0, frameCount) / KimodoMotionModelProfiles.DefaultFrameRate;
        }

        internal static double ResolveConstraintClipDurationSeconds(
            int frameCount,
            float frameRate = KimodoMotionModelProfiles.DefaultFrameRate)
        {
            int safeFrameCount = Mathf.Max(1, frameCount);
            return safeFrameCount / Mathf.Max(1f, frameRate);
        }

        internal static double ResolveConstraintEndSampleTimeSeconds(
            int frameCount,
            float frameRate = KimodoMotionModelProfiles.DefaultFrameRate)
        {
            int safeFrameCount = Mathf.Max(1, frameCount);
            return Math.Max(0.0, (safeFrameCount - 1) / Mathf.Max(1f, frameRate));
        }

        internal static List<KimodoMarkerSampleResult> BuildLocalManualSamples(
            IReadOnlyList<KimodoMarkerSampleResult> sourceSamples,
            double clipStartSeconds,
            double sampleTimeOffsetSeconds = 0.0)
        {
            var normalized = new List<KimodoMarkerSampleResult>();
            if (sourceSamples == null)
            {
                return normalized;
            }

            for (int i = 0; i < sourceSamples.Count; i++)
            {
                KimodoMarkerSampleResult sample = sourceSamples[i];
                if (sample == null)
                {
                    continue;
                }

                KimodoMarkerSampleResult clone = sample.Clone();
                clone.sampleTime = Math.Max(0.0, clone.sampleTime - clipStartSeconds + sampleTimeOffsetSeconds);
                normalized.Add(clone);
            }

            return normalized;
        }

        internal static double ResolveSegmentSampleTime(KimodoInOutConstraintClipSegment segment, double normalizedTime)
        {
            if (segment == null || segment.Clip == null)
            {
                return 0.0;
            }

            double clipLength = Math.Max(0.0, segment.Clip.length);
            if (clipLength <= 0.0)
            {
                return 0.0;
            }

            double segmentStart = Math.Max(0.0, segment.StartSeconds);
            double segmentSourceDuration = Math.Max(0.0, segment.DurationSeconds * Math.Max(1e-6f, segment.Speed));
            if (segmentSourceDuration <= 0.0)
            {
                segmentSourceDuration = Math.Max(0.0, clipLength - segmentStart);
            }

            double segmentEnd = Math.Min(clipLength, segmentStart + segmentSourceDuration);
            if (segmentEnd <= segmentStart)
            {
                return Math.Min(segmentStart, Math.Max(0.0, clipLength - 1e-3));
            }

            double clampedNormalizedTime = Math.Max(0.0, Math.Min(1.0, normalizedTime));
            if (clampedNormalizedTime <= 0.0)
            {
                return segmentStart;
            }

            if (clampedNormalizedTime >= 1.0)
            {
                double epsilon = Math.Min(1e-3, (segmentEnd - segmentStart) * 0.5);
                return Math.Max(segmentStart, segmentEnd - epsilon);
            }

            return segmentStart + ((segmentEnd - segmentStart) * clampedNormalizedTime);
        }

        private static double ResolveBoundaryNormalizedTime(KimodoInOutConstraintMode mode, bool isBegin)
        {
            return mode switch
            {
                KimodoInOutConstraintMode.Inside => isBegin ? 0.0 : 1.0,
                KimodoInOutConstraintMode.Outside => isBegin ? 1.0 : 0.0,
                _ => 0.0
            };
        }

        private static bool TrySampleBoundaryPose(
            KimodoInOutConstraintClipSegment segment,
            Avatar sourceAvatar,
            string modelName,
            double normalizedTime,
            double exportedSampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (segment == null || segment.Clip == null)
            {
                error = "Boundary segment clip is null.";
                return false;
            }

            double clipSampleTime = ResolveSegmentSampleTime(segment, normalizedTime);
            if (!KimodoRetargetToolsEditor.TrySampleMarkerForClip(
                    segment.Clip,
                    FullBodyConstraintType,
                    clipSampleTime,
                    sourceAvatar,
                    null,
                    KimodoMotionModelProfiles.NormalizeName(modelName),
                    out KimodoMarkerSampleResult sampledPose,
                    out error))
            {
                return false;
            }

            sampledPose.constraintMode = FullBodyConstraintType;
            sampledPose.sampleTime = exportedSampleTime;
            sample = sampledPose;
            return true;
        }
    }
}
