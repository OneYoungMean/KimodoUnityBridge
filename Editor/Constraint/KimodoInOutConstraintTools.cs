using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintTools
    {
        private const string FullBodyConstraintType = "fullbody";

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
                    ResolveConstraintEndSampleTimeSeconds(request.GenerationFrames),
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

        internal static int ClampFrameCount(int generationFrames)
        {
            return Mathf.Clamp(generationFrames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
        }

        internal static int DurationSecondsToFrameCount(float durationSeconds)
        {
            float minDurationSeconds = FrameCountToDurationSeconds(KimodoPlayableClip.MIN_FRAMES);
            float maxDurationSeconds = FrameCountToDurationSeconds(KimodoPlayableClip.MAX_FRAMES);
            return Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Clamp(durationSeconds, minDurationSeconds, maxDurationSeconds) * KimodoPlayableClip.FIXED_FRAME_RATE),
                KimodoPlayableClip.MIN_FRAMES,
                KimodoPlayableClip.MAX_FRAMES);
        }

        internal static float FrameCountToDurationSeconds(int frameCount)
        {
            return Mathf.Max(0, frameCount) / KimodoPlayableClip.FIXED_FRAME_RATE;
        }

        internal static double ResolveConstraintClipDurationSeconds(int frameCount)
        {
            int safeFrameCount = Mathf.Max(1, frameCount);
            return safeFrameCount / KimodoPlayableClip.FIXED_FRAME_RATE;
        }

        internal static double ResolveConstraintEndSampleTimeSeconds(int frameCount)
        {
            int safeFrameCount = Mathf.Max(1, frameCount);
            return Math.Max(0.0, (safeFrameCount - 1) / KimodoPlayableClip.FIXED_FRAME_RATE);
        }

        internal static List<KimodoMarkerSampleResult> BuildLocalManualSamples(
            IReadOnlyList<KimodoMarkerSampleResult> sourceSamples,
            double clipStartSeconds)
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
                clone.sampleTime = Math.Max(0.0, clone.sampleTime - clipStartSeconds);
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
                    null,
                    KimodoPlayableClip.NormalizeBridgeModelName(modelName),
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
