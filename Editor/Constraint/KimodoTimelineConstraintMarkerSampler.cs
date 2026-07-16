using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoTimelinePoseSampler : IDisposable
    {
        private readonly KimodoTimelineInOutConstraintContext context;
        private readonly HumanPoseHandler sourcePoseHandler;
        private readonly double originalTime;
        private readonly DirectorWrapMode originalWrapMode;
        private bool disposed;

        private KimodoTimelinePoseSampler(
            KimodoTimelineInOutConstraintContext context,
            HumanPoseHandler sourcePoseHandler,
            SkeletonCache targetCache)
        {
            this.context = context;
            this.sourcePoseHandler = sourcePoseHandler;
            TargetCache = targetCache;
            originalTime = context.Director.time;
            originalWrapMode = context.Director.extrapolationMode;
            context.Director.extrapolationMode = DirectorWrapMode.Hold;
        }

        internal SkeletonCache TargetCache { get; }

        internal static bool TryCreate(
            KimodoTimelineInOutConstraintContext context,
            string modelName,
            out KimodoTimelinePoseSampler sampler,
            out string error)
        {
            sampler = null;
            error = string.Empty;
            if (context?.Director == null || context.Animator == null)
            {
                error = "Timeline director or Animator is missing.";
                return false;
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(context.SourceAvatar))
            {
                error = "Timeline source avatar is null/invalid/non-humanoid.";
                return false;
            }
            if (!KimodoRetargetMarkerSamplingUtility.TryResolveTargetAvatar(
                    null,
                    context.Animator,
                    modelName,
                    out Avatar targetAvatar,
                    out error))
            {
                return false;
            }
            if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    targetAvatar,
                    "KimodoTimelinePoseSampler_Target",
                    out SkeletonCache targetCache,
                    out error))
            {
                return false;
            }

            HumanPoseHandler sourceHandler = null;
            try
            {
                sourceHandler = new HumanPoseHandler(context.SourceAvatar, context.Animator.transform);
                sampler = new KimodoTimelinePoseSampler(context, sourceHandler, targetCache);
                return true;
            }
            catch (Exception ex)
            {
                sourceHandler?.Dispose();
                targetCache.Dispose();
                error = ex.Message;
                return false;
            }
        }

        internal bool TryEvaluate(double timelineTime, out string error)
        {
            error = string.Empty;
            if (disposed || double.IsNaN(timelineTime) || double.IsInfinity(timelineTime))
            {
                error = "Timeline pose sampler or sample time is invalid.";
                return false;
            }

            try
            {
                context.Director.time = Math.Max(0.0, timelineTime);
                context.Director.Evaluate();
                var pose = new HumanPose();
                sourcePoseHandler.GetHumanPose(ref pose);
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref pose);
                TargetCache.poseHandler.SetHumanPose(ref pose);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal bool TrySampleMarker(
            double timelineTime,
            double exportedSampleTime,
            string markerType,
            string modelName,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            if (!TryEvaluate(timelineTime, out error))
            {
                return false;
            }

            BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(TargetCache);
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    targetSample,
                    TargetCache,
                    modelName,
                    markerType,
                    timelineTime,
                    out sample,
                    out error))
            {
                return false;
            }

            Transform sourceHips = ResolveSourceHumanBone(
                context.Animator,
                context.SourceAvatar,
                HumanBodyBones.Hips);
            if (sourceHips == null)
            {
                error = "Timeline source Animator has no Hips bone.";
                return false;
            }

            Vector3 sourceForward = Vector3.ProjectOnPlane(sourceHips.forward, Vector3.up);
            if (sourceForward.sqrMagnitude <= 1e-8f)
            {
                sourceForward = Vector3.forward;
            }
            sourceForward.Normalize();

            // ponytail: SetHumanPose drops Timeline root motion, so keep source Hips XZ/yaw.
            sample.kimodoRootPosition = new Vector3(
                sourceHips.position.x,
                sample.kimodoRootPosition.y,
                sourceHips.position.z);
            sample.unityRootPos = sourceHips.position;
            sample.unityRootRot = Quaternion.LookRotation(sourceForward, Vector3.up);
            sample.rootHeading = new Vector2(sourceForward.x, sourceForward.z);
            sample.hasRootHeading = true;
            sample.sampleTime = exportedSampleTime;
            return true;
        }

        internal static Transform ResolveSourceHumanBone(
            Animator animator,
            Avatar sourceAvatar,
            HumanBodyBones bone)
        {
            if (animator == null)
            {
                return null;
            }

            if (KimodoRetargetCoreUtility.IsValidHumanoid(animator.avatar))
            {
                return animator.GetBoneTransform(bone);
            }

            // ponytail: importer/cache avatars may be valid without being assigned to the scene Animator.
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                return null;
            }

            HumanBone[] humanBones = sourceAvatar.humanDescription.human;
            string humanName = bone.ToString();
            for (int i = 0; i < humanBones.Length; i++)
            {
                if (string.Equals(humanBones[i].humanName, humanName, StringComparison.Ordinal))
                {
                    return KimodoRetargetAvatarUtility.FindTransformByName(
                        animator.transform,
                        humanBones[i].boneName);
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            try
            {
                context.Director.extrapolationMode = originalWrapMode;
                context.Director.time = originalTime;
                context.Director.Evaluate();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][TimelineSample] Failed to restore Director state: {ex.Message}");
            }
            sourcePoseHandler.Dispose();
            TargetCache.Dispose();
        }
    }

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

            if (!KimodoTimelinePoseSampler.TryCreate(
                    context,
                    context.ModelName,
                    out KimodoTimelinePoseSampler sampler,
                    out error))
            {
                return false;
            }
            try
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    if (!TryBuildMarkerSample(markers[i], context, sampler, out KimodoMarkerSampleResult sample, out error))
                    {
                        return false;
                    }

                    samples.Add(sample);
                }
            }
            finally
            {
                sampler.Dispose();
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

        private static bool TryBuildMarkerSample(
            KimodoConstraintMarkerBase marker,
            KimodoTimelineInOutConstraintContext context,
            KimodoTimelinePoseSampler sampler,
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

            if (!sampler.TrySampleMarker(
                    marker.time,
                    marker.time,
                    marker.ConstraintType,
                    context.ModelName,
                    out KimodoMarkerSampleResult captured,
                    out error))
            {
                return false;
            }
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
