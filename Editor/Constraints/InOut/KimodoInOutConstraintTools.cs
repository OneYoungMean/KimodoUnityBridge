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

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(request.SourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
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
                !KimodoTimelineConstraintClipCache.TrySampleMarker(
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

            if (request.EnableEnd &&
                !KimodoTimelineConstraintClipCache.TrySampleMarker(
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

            if (!request.EnableBegin && !request.EnableEnd)
            {
                warning = "InOut constraint request has no enabled boundary segments.";
            }
            return true;
        }

        internal static double ResolveTimelineBoundaryTime(KimodoInOutConstraintRequest request, bool isBegin)
        {
            KimodoTimelineInOutConstraintContext context = request.TimelineContext;
            float frameRate = KimodoTimelineConstraintClipCache.ResolveTimelineFrameRate(context);
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
                    forceRefresh: false,
                    out KimodoMarkerSampleResult sampledPose,
                    out error))
            {
                return false;
            }

            sampledPose.constraintType = FullBodyConstraintType;
            sampledPose.sampleTime = exportedSampleTime;
            sample = sampledPose;
            return true;
        }
    }
}
