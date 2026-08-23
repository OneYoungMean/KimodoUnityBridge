using System;
using KimodoUnityBridge;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintMarkerSampling
    {
        private const string DefaultBridgeModelName = "Kimodo-SOMA-RP-v1";
        public static bool TryUpdateAutoSampleMarkerData(KimodoConstraintMarker marker, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!marker.constraintEnabled)
            {
                KimodoConstraintMarkerEditorUtility.ClearMarkerPreview(marker, keepIfOverrideWindowOpen: false);
                return true;
            }

            if (!marker.autoSample)
            {
                return true;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryGetMarkerTrack(marker, out TrackAsset track))
            {
                error = "parent track not found";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null || animator.transform == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            TimelineClip referenceClip = FindReferenceClip(track, marker.time, activeClip: null);
            double sampleTime = marker.time;
            var timelineContext = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = null,
                Track = track,
                Director = director,
                Animator = animator,
                ModelName = ResolveModelName(referenceClip)
            };
            string samplingType = "fullbody";
            if (!KimodoTimelineConstraintSampler.TrySampleMarker(
                    timelineContext,
                    sampleTime,
                    sampleTime,
                    samplingType,
                    ResolveModelName(referenceClip),
                    out KimodoMarkerSampleResult sample,
                    out error))
            {
                return false;
            }

            float timelineFrameRate = KimodoTimelineConstraintSampler.ResolveTimelineFrameRate(timelineContext);
            int timelineFrame = KimodoTimelineConstraintSampler.ResolveTimelineSampleFrame(
                sampleTime,
                timelineFrameRate);
            double timelineSampleTime = KimodoTimelineConstraintSampler.ResolveTimelineSampleTime(
                sampleTime,
                timelineFrameRate);
            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][ConstraintSampleFrame] marker='{marker.ConstraintType}' " +
                $"markerTime={sampleTime:R}s timelineFps={timelineFrameRate:R} " +
                $"exactFrame={(sampleTime * timelineFrameRate):R} " +
                $"zeroBasedFrame={timelineFrame} oneBasedFrame={timelineFrame + 1} " +
                $"quantizedSampleTime={timelineSampleTime:R}s");

            sample.sampleTime = sampleTime;
            if (marker is KimodoConstraintMarker)
            {
                sample.constraintMode = marker.ConstraintMode == KimodoConstraintMode.Root2D
                    ? "root2d"
                    : marker.ConstraintMode == KimodoConstraintMode.Effector ? "effector" : "fullbody";
            }
            KimodoMarkerSampleResult preview = MergeAutoSampledChannels(marker, sample);
            if (preview == null)
            {
                error = "failed to build marker sample";
                return false;
            }

            if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                    marker, preview, out error))
            {
                return false;
            }
            return true;
        }

        private static KimodoMarkerSampleResult MergeAutoSampledChannels(
            KimodoConstraintMarker marker,
            KimodoMarkerSampleResult sampled)
        {
            return sampled?.Clone() ?? marker?.SampleData?.Clone() ?? new KimodoMarkerSampleResult();
        }

internal static string ResolveModelName(TimelineClip clipRange)
        {
            KimodoPlayableClip playableClip = clipRange != null ? clipRange.asset as KimodoPlayableClip : null;
            return playableClip != null && !string.IsNullOrWhiteSpace(playableClip.bridgeModelName)
                ? playableClip.bridgeModelName.Trim()
                : DefaultBridgeModelName;
        }

internal static string ResolveModelName(TrackAsset track, double timelineTime, TimelineClip activeClip)
        {
            return ResolveModelName(FindReferenceClip(track, timelineTime, activeClip));
        }

internal static TimelineClip FindReferenceClip(TrackAsset track, double timelineTime, TimelineClip activeClip)
        {
            if (activeClip?.asset is KimodoPlayableClip)
            {
                return activeClip;
            }

            TimelineClip owningClip = KimodoTimelineConstraintMarkerSampler.FindOwningClip(track, timelineTime);
            if (owningClip != null)
            {
                return owningClip;
            }

            TimelineClip nearestKimodo = null;
            double nearestDistance = double.PositiveInfinity;
            if (track != null)
            {
                foreach (TimelineClip clip in track.GetClips())
                {
                    if (!(clip?.asset is KimodoPlayableClip))
                    {
                        continue;
                    }

                    double distance = timelineTime < clip.start
                        ? clip.start - timelineTime
                        : timelineTime - clip.end;
                    if (distance < nearestDistance ||
                        (Math.Abs(distance - nearestDistance) <= 1e-9 &&
                         (nearestKimodo == null || clip.start > nearestKimodo.start)))
                    {
                        nearestKimodo = clip;
                        nearestDistance = distance;
                    }
                }
            }

            return nearestKimodo ?? activeClip;
        }

    }
}
