using System;
using System.Collections.Generic;
using KimodoUnityMotionTools.Generation.Pipeline;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoInbetweenConstraintUtility
    {
        private const string LogPrefix = "[Kimodo][InbetweenConstraint]";
        private const double NeighborSampleDeltaSeconds = 1.0 / 60.0;

        public static bool TryBuildConstraintsJson(
            TimelineClip sourceClip,
            bool enableInbetweenInterpolation,
            int generationFrames,
            out string constraintsJson,
            out string error)
        {
            constraintsJson = string.Empty;
            error = string.Empty;

            if (!TryBuildMarkerSamplesForExport(sourceClip, out List<KimodoMarkerSampleResult> samples, out error))
            {
                return false;
            }

            if (enableInbetweenInterpolation)
            {
                if (!TryAddAutoInbetweenSamples(sourceClip, Mathf.Max(1, generationFrames), samples, out string warning))
                {
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        Debug.LogWarning($"{LogPrefix} {warning}");
                    }
                }
            }

            constraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                clipStartSeconds: sourceClip.start,
                clipDurationSeconds: sourceClip.duration);
            return true;
        }

        internal static bool TryBuildMarkerSamplesForExport(
            TimelineClip sourceClip,
            out List<KimodoMarkerSampleResult> samples,
            out string error)
        {
            samples = new List<KimodoMarkerSampleResult>();
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "No selected timeline clip for constraint export.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            List<KimodoConstraintMarkerBase> markers = GatherKimodoMarkers(track, sourceClip);
            if (markers.Count == 0)
            {
                return true;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            Transform skeletonRoot = animator.transform;
            if (skeletonRoot == null)
            {
                error = "Animator transform is null.";
                return false;
            }

            double originalTime = director.time;
            DirectorWrapMode originalWrap = director.extrapolationMode;

            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;
                for (int i = 0; i < markers.Count; i++)
                {
                    if (!TryBuildMarkerSample(markers[i], sourceClip, skeletonRoot, animator, director, out KimodoMarkerSampleResult sample, out error))
                    {
                        return false;
                    }

                    samples.Add(sample);
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrap;
            }

            return true;
        }

        internal static bool TrySamplePoseFromClipAsset(
            TimelineClip sourceClip,
            Animator animator,
            Transform skeletonRoot,
            double sampleTime,
            string markerType,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            string modelName = sourceClip.asset is KimodoPlayableClip playableClip
                ? playableClip.bridgeModelName
                : "Kimodo-SOMA-RP-v1";
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar originAvatar, out string originError))
            {
                error = $"Resolve origin avatar failed: {originError}";
                return false;
            }

            KimodoLocalAvatarUtility.AvatarResolveResult targetAvatarResult = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(animator.gameObject);
            Avatar targetAvatar = targetAvatarResult.Avatar;
            if (targetAvatar == null || !targetAvatar.isValid || !targetAvatar.isHuman)
            {
                error = $"Resolve target avatar failed: {targetAvatarResult.Error}";
                return false;
            }

            if (!KimodoMarkerSamplingUtility.TrySampleMarker(
                    animator,
                    skeletonRoot,
                    sourceClip,
                    modelName,
                    sampleTime,
                    markerType,
                    originAvatar,
                    targetAvatar,
                    out sample,
                    out error))
            {
                return false;
            }

            if (sample == null)
            {
                error = "sample result is null";
                return false;
            }

            sample.constraintType = markerType ?? string.Empty;
            sample.sampleTime = sampleTime;
            return true;
        }

        private static bool TryBuildMarkerSample(
            KimodoConstraintMarkerBase marker,
            TimelineClip sourceClip,
            Transform skeletonRoot,
            Animator animator,
            PlayableDirector director,
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

            bool isCustomEndEffector = marker is KimodoEndEffectorConstraintMarker ee &&
                                       string.Equals(ee.ConstraintType, "end-effector", StringComparison.OrdinalIgnoreCase);
            if (marker.useOverride && !isCustomEndEffector)
            {
                sample = KimodoConstraintMarkerPoseMapper.NormalizeSample(marker, marker.SampleData);
                if (sample == null)
                {
                    error = "failed to read override marker data";
                    return false;
                }

                return true;
            }

            double sampleTime = marker.time;
            director.time = sampleTime;
            director.Evaluate();

            if (!TrySamplePoseFromClipAsset(
                    sourceClip,
                    animator,
                    skeletonRoot,
                    sampleTime,
                    marker.ConstraintType,
                    out KimodoMarkerSampleResult captured,
                    out error))
            {
                return false;
            }

            captured.sampleTime = sampleTime;
            sample = KimodoConstraintMarkerPoseMapper.NormalizeSample(marker, captured);
            if (sample == null)
            {
                error = "failed to map sampled pose to marker sample data";
                return false;
            }

            return true;
        }

        private static bool TryAddAutoInbetweenSamples(
            TimelineClip sourceClip,
            int generationFrames,
            List<KimodoMarkerSampleResult> samples,
            out string warning)
        {
            warning = string.Empty;
            if (sourceClip == null)
            {
                warning = "source clip is null, skip inbetween interpolation.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                warning = "cannot resolve parent track, skip inbetween interpolation.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                warning = "Timeline inspected director is null, skip inbetween interpolation.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                warning = "track has no Animator binding, skip inbetween interpolation.";
                return false;
            }

            Transform skeletonRoot = animator.transform;
            if (skeletonRoot == null)
            {
                warning = "Animator transform is null, skip inbetween interpolation.";
                return false;
            }

            FindNeighborClips(sourceClip, out TimelineClip leftNeighbor, out TimelineClip rightNeighbor);
            if (leftNeighbor == null && rightNeighbor == null)
            {
                warning = "no neighboring clips found, skip inbetween interpolation.";
                return true;
            }

            var occupiedManualTimes = new HashSet<long>();
            CollectManualTimes(samples, occupiedManualTimes);

            double originalTime = director.time;
            DirectorWrapMode originalWrapMode = director.extrapolationMode;

            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;

                if (leftNeighbor != null && !occupiedManualTimes.Contains(ToTimeKey(0.0)))
                {
                    double evalTime = Math.Max(leftNeighbor.start, leftNeighbor.end - NeighborSampleDeltaSeconds);
                    if (TryCapturePoseAtTime(leftNeighbor, director, skeletonRoot, animator, evalTime, "fullbody", out KimodoMarkerSampleResult pose, out string captureError))
                    {
                        samples.Add(pose);
                    }
                    else
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample left neighbor end pose: {captureError}");
                    }
                }

                int endFrame = Math.Max(0, generationFrames - 1);
                if (rightNeighbor != null && !occupiedManualTimes.Contains(ToTimeKey(endFrame / (double)generationFrames)))
                {
                    double evalTime = rightNeighbor.start;
                    if (TryCapturePoseAtTime(rightNeighbor, director, skeletonRoot, animator, evalTime, "fullbody", out KimodoMarkerSampleResult pose, out string captureError))
                    {
                        samples.Add(pose);
                    }
                    else
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample right neighbor start pose: {captureError}");
                    }
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrapMode;
            }

            return true;
        }

        private static void CollectManualTimes(List<KimodoMarkerSampleResult> samples, HashSet<long> output)
        {
            if (samples == null || output == null)
            {
                return;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult s = samples[i];
                if (s == null)
                {
                    continue;
                }

                output.Add(ToTimeKey(s.sampleTime));
            }
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

        private static void FindNeighborClips(TimelineClip sourceClip, out TimelineClip leftNeighbor, out TimelineClip rightNeighbor)
        {
            leftNeighbor = null;
            rightNeighbor = null;

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                return;
            }

            foreach (TimelineClip clip in track.GetClips())
            {
                if (clip == null || clip == sourceClip)
                {
                    continue;
                }

                if (clip.end <= sourceClip.start)
                {
                    if (leftNeighbor == null || clip.end > leftNeighbor.end)
                    {
                        leftNeighbor = clip;
                    }
                }

                if (clip.start >= sourceClip.end)
                {
                    if (rightNeighbor == null || clip.start < rightNeighbor.start)
                    {
                        rightNeighbor = clip;
                    }
                }
            }
        }

        internal static bool TryBuildNeighborBoundarySamplesForPreview(
            TimelineClip sourceClip,
            int generationFrames,
            out KimodoMarkerSampleResult leftNeighborEndPose,
            out KimodoMarkerSampleResult rightNeighborStartPose,
            out string warning)
        {
            leftNeighborEndPose = null;
            rightNeighborStartPose = null;
            warning = string.Empty;

            if (sourceClip == null)
            {
                warning = "source clip is null, skip inbetween preview boundaries.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                warning = "cannot resolve parent track, skip inbetween preview boundaries.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                warning = "Timeline inspected director is null, skip inbetween preview boundaries.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                warning = "track has no Animator binding, skip inbetween preview boundaries.";
                return false;
            }

            Transform skeletonRoot = animator.transform;
            if (skeletonRoot == null)
            {
                warning = "Animator transform is null, skip inbetween preview boundaries.";
                return false;
            }

            FindNeighborClips(sourceClip, out TimelineClip leftNeighbor, out TimelineClip rightNeighbor);
            if (leftNeighbor == null && rightNeighbor == null)
            {
                warning = "no neighboring clips found, skip inbetween preview boundaries.";
                return true;
            }

            double originalTime = director.time;
            DirectorWrapMode originalWrapMode = director.extrapolationMode;
            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;

                if (leftNeighbor != null)
                {
                    double evalTime = Math.Max(leftNeighbor.start, leftNeighbor.end - NeighborSampleDeltaSeconds);
                    if (!TryCapturePoseAtTime(leftNeighbor, director, skeletonRoot, animator, evalTime, "fullbody", out leftNeighborEndPose, out string leftError))
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample left neighbor end pose for preview: {leftError}");
                    }
                }

                if (rightNeighbor != null)
                {
                    _ = Math.Max(0, generationFrames - 1);
                    double evalTime = rightNeighbor.start;
                    if (!TryCapturePoseAtTime(rightNeighbor, director, skeletonRoot, animator, evalTime, "fullbody", out rightNeighborStartPose, out string rightError))
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample right neighbor start pose for preview: {rightError}");
                    }
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrapMode;
            }

            return true;
        }

        private static bool TryCapturePoseAtTime(
            TimelineClip sourceClip,
            PlayableDirector director,
            Transform skeletonRoot,
            Animator animator,
            double evalTime,
            string markerType,
            out KimodoMarkerSampleResult pose,
            out string error)
        {
            pose = null;
            error = string.Empty;

            try
            {
                director.time = evalTime;
                director.Evaluate();
                if (!TrySamplePoseFromClipAsset(
                        sourceClip,
                        animator,
                        skeletonRoot,
                        evalTime,
                        markerType,
                        out pose,
                        out error))
                {
                    return false;
                }

                return pose != null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static long ToTimeKey(double time)
        {
            return (long)Math.Round(time * 1000000.0);
        }
    }
}
