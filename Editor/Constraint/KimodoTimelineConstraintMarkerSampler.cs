using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineConstraintMarkerSampler
    {
        internal static bool TryBuildMarkerSamplesForExport(
            KimodoTimelineInOutConstraintContext context,
            out List<KimodoMarkerSampleResult> samples,
            out string error)
        {
            samples = new List<KimodoMarkerSampleResult>();
            error = string.Empty;

            if (context == null || context.SourceClip == null)
            {
                error = "No selected timeline clip for constraint export.";
                return false;
            }

            if (context.Track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            List<KimodoConstraintMarkerBase> markers = GatherKimodoMarkers(context.Track, context.SourceClip);
            if (markers.Count == 0)
            {
                return true;
            }

            if (context.Director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            if (context.Animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            double originalTime = context.Director.time;
            DirectorWrapMode originalWrap = context.Director.extrapolationMode;

            try
            {
                context.Director.extrapolationMode = DirectorWrapMode.Hold;
                for (int i = 0; i < markers.Count; i++)
                {
                    if (!TryBuildMarkerSample(markers[i], context, out KimodoMarkerSampleResult sample, out error))
                    {
                        return false;
                    }

                    samples.Add(sample);
                }
            }
            finally
            {
                context.Director.time = originalTime;
                context.Director.Evaluate();
                context.Director.extrapolationMode = originalWrap;
            }

            return true;
        }

        internal static bool TryBuildOverrideMarkerSamplesWithoutTimelineSampling(
            TimelineClip clipRange,
            out List<KimodoMarkerSampleResult> samples,
            out bool requiresTimelineSampling,
            out string error)
        {
            samples = new List<KimodoMarkerSampleResult>();
            requiresTimelineSampling = false;
            error = string.Empty;

            TrackAsset track = clipRange != null ? clipRange.GetParentTrack() : null;
            if (track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            List<KimodoConstraintMarkerBase> markers = GatherKimodoMarkers(track, clipRange);
            for (int i = 0; i < markers.Count; i++)
            {
                KimodoConstraintMarkerBase marker = markers[i];
                if (!CanUseOverrideWithoutTimelineSampling(marker))
                {
                    requiresTimelineSampling = true;
                    return true;
                }

                KimodoMarkerSampleResult sample = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, marker.SampleData);
                if (sample == null)
                {
                    error = "failed to read override marker data";
                    return false;
                }

                samples.Add(sample);
            }

            return true;
        }

        internal static bool TrySamplePoseFromClipAsset(
            KimodoTimelineInOutConstraintContext context,
            double timelineTime,
            string markerType,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (context == null || context.SourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (context.Track == null)
            {
                error = "Parent track not found.";
                return false;
            }

            if (context.Animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            if (context.CurrentClip == null)
            {
                error = "Timeline clip does not contain a usable AnimationClip.";
                return false;
            }

            double sourceSampleTime = KimodoMarkerSamplingUtility.ResolveSourceClipSampleTime(
                context.SourceClip,
                timelineTime);

            if (!KimodoRetargetToolsEditor.TrySampleMarkerForClip(
                    context.CurrentClip,
                    markerType,
                    sourceSampleTime,
                    context.SourceAvatar,
                    null,
                    context.Animator,
                    context.ModelName,
                    forceRefresh: false,
                    out KimodoMarkerSampleResult sampledPose,
                    out error))
            {
                return false;
            }

            sample = sampledPose;
            sample.constraintType = markerType ?? string.Empty;
            sample.sampleTime = timelineTime;
            return true;
        }

        private static bool TryBuildMarkerSample(
            KimodoConstraintMarkerBase marker,
            KimodoTimelineInOutConstraintContext context,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (marker == null)
            {
                error = "Marker is null.";
                return false;
            }

            if (CanUseOverrideWithoutTimelineSampling(marker))
            {
                sample = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, marker.SampleData);
                if (sample == null)
                {
                    error = "failed to read override marker data";
                    return false;
                }

                Debug.Log(
                    $"[Kimodo][ConstraintExport] marker='{marker.ConstraintType}' time={marker.time:F3} mode=override " +
                    $"jointNames=[{string.Join(", ", sample.jointNames ?? new List<string>())}] hasHeading={sample.hasRootHeading}");

                return true;
            }

            double sampleTime = marker.time;
            context.Director.time = sampleTime;
            context.Director.Evaluate();

            if (!TrySamplePoseFromClipAsset(
                    context,
                    sampleTime,
                    marker.ConstraintType,
                    out KimodoMarkerSampleResult captured,
                    out error))
            {
                return false;
            }

            captured.sampleTime = sampleTime;
            sample = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, captured);
            if (sample == null)
            {
                error = "failed to map sampled pose to marker sample data";
                return false;
            }

            Debug.Log(
                $"[Kimodo][ConstraintExport] marker='{marker.ConstraintType}' time={marker.time:F3} mode=sampled " +
                $"jointNames=[{string.Join(", ", sample.jointNames ?? new List<string>())}] hasHeading={sample.hasRootHeading}");

            return true;
        }

        private static bool CanUseOverrideWithoutTimelineSampling(KimodoConstraintMarkerBase marker)
        {
            if (marker == null || !marker.useOverride)
            {
                return false;
            }

            return marker is not KimodoEndEffectorConstraintMarker ee ||
                !string.Equals(ee.ConstraintType, "end-effector", StringComparison.OrdinalIgnoreCase);
        }

        private static List<KimodoConstraintMarkerBase> GatherKimodoMarkers(TrackAsset track, TimelineClip clipRange)
        {
            var markers = new List<KimodoConstraintMarkerBase>();
            double minTime = clipRange != null ? clipRange.start : double.MinValue;
            double maxTime = clipRange != null ? clipRange.end : double.MaxValue;
            foreach (IMarker marker in track.GetMarkers())
            {
                if (marker is KimodoConstraintMarkerBase kimodoMarker)
                {
                    if (kimodoMarker.time < minTime || kimodoMarker.time > maxTime)
                    {
                        continue;
                    }

                    markers.Add(kimodoMarker);
                }
            }

            markers.Sort((a, b) => a.time.CompareTo(b.time));
            return markers;
        }
    }
}
