using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineTrackOffsetUtility
    {
        internal static void ResolveWorldOffset(
            TrackAsset track,
            Animator animator,
            out Vector3 position,
            out Quaternion rotation)
        {
            ResolveWorldOffset(track, animator, out position, out rotation, out _);
        }

        internal static void ResolveWorldOffset(
            TrackAsset track,
            Animator animator,
            out Vector3 position,
            out Quaternion rotation,
            out bool isSceneOffset)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            isSceneOffset = false;
            if (track is not AnimationTrack animationTrack)
            {
                return;
            }
            KimodoTimelinePreviewRefreshUtility.ResolveAnimationTrackOffset(
                animationTrack,
                animator,
                out position,
                out rotation,
                out isSceneOffset);
        }

        internal static void WorldToTrackPose(
            Vector3 worldPosition,
            Quaternion worldRotation,
            Vector3 trackPosition,
            Quaternion trackRotation,
            out Vector3 position,
            out Quaternion rotation)
        {
            Quaternion normalizedTrackRotation = NormalizeTrackRotation(trackRotation);
            Quaternion inverseTrackRotation = Quaternion.Inverse(normalizedTrackRotation);
            position = inverseTrackRotation * (worldPosition - trackPosition);
            rotation = (inverseTrackRotation * worldRotation).normalized;
        }

        internal static void TrackToWorldPose(
            Vector3 trackLocalPosition,
            Quaternion trackLocalRotation,
            Vector3 trackPosition,
            Quaternion trackRotation,
            out Vector3 position,
            out Quaternion rotation)
        {
            Quaternion normalizedTrackRotation = NormalizeTrackRotation(trackRotation);
            position = trackPosition + normalizedTrackRotation * trackLocalPosition;
            rotation = (normalizedTrackRotation * trackLocalRotation).normalized;
        }

        internal static void ConvertSampleToTrackSpace(
            KimodoMarkerSampleResult sample,
            Vector3 trackPosition,
            Quaternion trackRotation)
        {
            ConvertSampleSpace(sample, trackPosition, trackRotation, toWorld: false);
        }

        internal static void ConvertSampleFromTrackSpace(
            KimodoMarkerSampleResult sample,
            Vector3 trackPosition,
            Quaternion trackRotation)
        {
            ConvertSampleSpace(sample, trackPosition, trackRotation, toWorld: true);
        }

        private static void ConvertSampleSpace(
            KimodoMarkerSampleResult sample,
            Vector3 trackPosition,
            Quaternion trackRotation,
            bool toWorld)
        {
            if (sample == null)
            {
                return;
            }

            if (KimodoConstraintMask.IsActive(sample, "rootposition") && sample.rootOverride != null)
            {
                ConvertRigidTransform(
                    sample.rootOverride,
                    trackPosition,
                    trackRotation,
                    toWorld);
            }

            ConvertRigidTransformIfActive(sample, "lefthand", sample.effectors?.leftHand, trackPosition, trackRotation, toWorld);
            ConvertRigidTransformIfActive(sample, "righthand", sample.effectors?.rightHand, trackPosition, trackRotation, toWorld);
            ConvertRigidTransformIfActive(sample, "leftfoot", sample.effectors?.leftFoot, trackPosition, trackRotation, toWorld);
            ConvertRigidTransformIfActive(sample, "rightfoot", sample.effectors?.rightFoot, trackPosition, trackRotation, toWorld);
        }

        private static void ConvertRigidTransformIfActive(
            KimodoMarkerSampleResult sample,
            string channel,
            KimodoRigidTransform target,
            Vector3 trackPosition,
            Quaternion trackRotation,
            bool toWorld)
        {
            if (target != null && KimodoConstraintMask.IsActive(sample, channel))
            {
                ConvertRigidTransform(target, trackPosition, trackRotation, toWorld);
            }
        }

        private static void ConvertRigidTransform(
            KimodoRigidTransform target,
            Vector3 trackPosition,
            Quaternion trackRotation,
            bool toWorld)
        {
            Vector3 position;
            Quaternion rotation;
            if (toWorld)
            {
                TrackToWorldPose(
                    target.t,
                    target.q,
                    trackPosition,
                    trackRotation,
                    out position,
                    out rotation);
            }
            else
            {
                WorldToTrackPose(
                    target.t,
                    target.q,
                    trackPosition,
                    trackRotation,
                    out position,
                    out rotation);
            }
            target.t = position;
            target.q = rotation;
        }

        private static Quaternion NormalizeTrackRotation(Quaternion rotation)
        {
            if (rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w <= 1e-8f)
            {
                return Quaternion.identity;
            }

            rotation.Normalize();
            return rotation;
        }
    }

    internal static class KimodoTimelineConstraintSampler
    {
        internal static float ResolveTimelineFrameRate(KimodoTimelineInOutConstraintContext context)
        {
            TimelineAsset timelineAsset = context?.Director?.playableAsset as TimelineAsset ??
                context?.Track?.timelineAsset ??
                context?.SourceClip?.GetParentTrack()?.timelineAsset;
            double frameRate = timelineAsset?.editorSettings.frameRate ?? KimodoMotionModelProfiles.DefaultFrameRate;
            return (float)Math.Max(1.0, frameRate);
        }


        internal static int ResolveTimelineSampleFrame(double timelineTime, float frameRate)
        {
            return KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(
                Math.Max(0.0, timelineTime),
                Math.Max(1f, frameRate));
        }

        internal static double ResolveTimelineSampleTime(double timelineTime, float frameRate)
        {
            float fps = Math.Max(1f, frameRate);
            return KimodoTimelinePreviewRefreshUtility.TimelineFrameToTime(
                ResolveTimelineSampleFrame(timelineTime, fps),
                fps);
        }

        internal static bool TrySampleMarker(
            KimodoTimelineInOutConstraintContext context,
            double timelineTime,
            double exportedSampleTime,
            string markerType,
            string modelName,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            float timelineFrameRate = ResolveTimelineFrameRate(context);
            double exactTimelineTime = Math.Max(0.0, timelineTime);
            int timelineSampleFrame = ResolveTimelineSampleFrame(exactTimelineTime, timelineFrameRate);
            if (!KimodoTimelineSamplingSession.TryCreate(
                    context,
                    modelName,
                    out KimodoTimelineSamplingSession sampler,
                    out error))
            {
                return false;
            }
            try
            {
                if (!sampler.TryCaptureTargetBoneSamplesAtFrames(
                        new[] { timelineSampleFrame },
                        timelineFrameRate,
                        out BoneSample[] targetSamples,
                        out error) ||
                    targetSamples == null || targetSamples.Length == 0)
                {
                    return false;
                }

                return KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    targetSamples[0],
                    sampler.TargetCache,
                    modelName,
                    markerType,
                    exportedSampleTime,
                    out sample,
                    out error);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        internal static void ApplyTargetRootPose(
            KimodoMarkerSampleResult sample,
            Vector3 targetRootPosition,
            Quaternion targetRootRotation,
            double exportedSampleTime)
        {
            if (sample == null)
            {
                return;
            }

            targetRootRotation.Normalize();
            Quaternion planarRotation = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(targetRootRotation);
            sample.enableMask.rootPosition = true;
            sample.enableMask.rootHeading = true;
            sample.validMask ??= new KimodoConstraintMask();
            sample.validMask.rootPosition = true;
            sample.validMask.rootHeading = true;
            sample.root2DOverride = new KimodoRigidTransform { t = targetRootPosition, q = planarRotation };
            sample.constraintMode = "root2d";
            sample.sampleTime = exportedSampleTime;
        }

    }

    internal sealed class KimodoTimelineSamplingSession : IDisposable
    {
        private readonly KimodoTimelineInOutConstraintContext context;
        private readonly RetargetSkeleton sourceSamplingCache;
        private readonly string[] sourceBonePaths;
        private readonly Transform[] sourceBoneTransforms;
        private readonly KimodoTimelineEvaluationScope evaluationScope;
        private readonly DirectorWrapMode originalWrapMode;
        private readonly Vector3 trackOffsetPosition;
        private readonly Quaternion trackOffsetRotation;
        private bool disposed;

        private KimodoTimelineSamplingSession(
            KimodoTimelineInOutConstraintContext context,
            RetargetSkeleton sourceSamplingCache,
            string[] sourceBonePaths,
            Transform[] sourceBoneTransforms,
            RetargetSkeleton targetCache,
            KimodoTimelineEvaluationScope evaluationScope,
            DirectorWrapMode originalWrapMode,
            Vector3 trackOffsetPosition,
            Quaternion trackOffsetRotation)
        {
            this.context = context;
            this.sourceSamplingCache = sourceSamplingCache;
            this.sourceBonePaths = sourceBonePaths;
            this.sourceBoneTransforms = sourceBoneTransforms;
            TargetCache = targetCache;
            this.evaluationScope = evaluationScope;
            this.originalWrapMode = originalWrapMode;
            this.trackOffsetPosition = trackOffsetPosition;
            this.trackOffsetRotation = trackOffsetRotation;
        }

        internal RetargetSkeleton TargetCache { get; }
        internal static bool TryCreate(
            KimodoTimelineInOutConstraintContext context,
            string modelName,
            out KimodoTimelineSamplingSession sampler,
            out string error)
        {
            sampler = null;
            error = string.Empty;
            if (context?.Director == null || context.Animator == null)
            {
                error = "Timeline director or Animator is missing.";
                return false;
            }

            // Resolve the binding avatar through the shared editor utility. The
            // Animator avatar is only the first source; track custom/importer/
            // cached/generated humanoid avatars are valid fallbacks for the
            // same bound hierarchy.
            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(
                    context.Track,
                    context.Animator);
            Avatar sourceAvatar = avatarResult.Avatar;
            if (!avatarResult.IsHumanoid || !KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = string.IsNullOrWhiteSpace(avatarResult.Error)
                    ? "Timeline binding Animator avatar is null/invalid/non-humanoid."
                    : avatarResult.Error;
                return false;
            }
            Avatar targetAvatar = sourceAvatar;
            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    targetAvatar,
                    "KimodoTimelineSamplingSession_Target",
                    out RetargetSkeleton targetCache,
                    out error))
            {
                return false;
            }

            RetargetSkeleton sourceSamplingCache = null;
            string[] sourceBonePaths = null;
            Transform[] sourceBoneTransforms = null;
            DirectorWrapMode originalWrapMode = context.Director.extrapolationMode;
            KimodoTimelineEvaluationScope evaluationScope = null;
            try
            {
                evaluationScope = KimodoTimelineEvaluationScope.Begin(context.Director);
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        sourceAvatar,
                        "KimodoTimelineSamplingSession_SourcePose",
                        out sourceSamplingCache,
                        out error))
                {
                    targetCache.Dispose();
                    return false;
                }
                if (!TryBuildSourceBoneTransforms(
                        context.Animator.transform,
                        sourceSamplingCache,
                        out sourceBonePaths,
                        out sourceBoneTransforms,
                        out error))
                {
                    sourceSamplingCache.Dispose();
                    targetCache.Dispose();
                    return false;
                }

                context.Director.extrapolationMode = DirectorWrapMode.Hold;
                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    context.Track,
                    context.Animator,
                    out Vector3 trackOffsetPosition,
                    out Quaternion trackOffsetRotation);
                sampler = new KimodoTimelineSamplingSession(
                    context,
                    sourceSamplingCache,
                    sourceBonePaths,
                    sourceBoneTransforms,
                    targetCache,
                    evaluationScope,
                    originalWrapMode,
                    trackOffsetPosition,
                    trackOffsetRotation);
                return true;
            }
            catch (Exception ex)
            {
                evaluationScope?.Dispose();
                sourceSamplingCache?.Dispose();
                targetCache.Dispose();
                error = ex.Message;
                return false;
            }
        }

        internal bool TryCaptureMuscleSample(
            double timelineTime,
            bool normalizeRootToAnchor,
            Vector3 anchorRootPosition,
            Quaternion anchorRootRotation,
            out MuscleSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (disposed || double.IsNaN(timelineTime) || double.IsInfinity(timelineTime))
            {
                error = "Timeline pose sampler or sample time is invalid.";
                return false;
            }

            if (!TryCaptureMuscleSamples(
                    new[] { timelineTime },
                    out MuscleSample[] samples,
                    out error))
            {
                return false;
            }

            // RootTQ is not part of the muscle sampling transport. The
            // canonical track-space root is captured later from RetargetSkeleton
            // and written to root2DOverride; legacy anchor normalization is
            // kept in the signature only for call-site compatibility.
            sample = samples[0];
            return true;
        }

        internal bool TryCaptureMuscleSamples(
            IReadOnlyList<double> timelineTimes,
            out MuscleSample[] samples,
            out string error,
            Func<AnimationClip, string, string> writebackClip = null)
        {
            samples = null;
            error = string.Empty;
            if (timelineTimes == null || timelineTimes.Count == 0)
            {
                error = "Timeline sample times are empty.";
                return false;
            }
            if (disposed)
            {
                error = "Timeline pose sampler is disposed.";
                return false;
            }

            try
            {
                float frameRate = KimodoTimelineConstraintSampler.ResolveTimelineFrameRate(context);
                int sampleCount = timelineTimes.Count;
                samples = new MuscleSample[sampleCount];
                int clipSampleCount = Mathf.Max(2, sampleCount);
                var poseSamples = new BoneSample[clipSampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    double timelineTime = timelineTimes[i];
                    if (double.IsNaN(timelineTime) || double.IsInfinity(timelineTime))
                    {
                        samples = null;
                        error = $"Timeline sample time {i} is invalid.";
                        return false;
                    }

                    evaluationScope.EvaluateAt(Math.Max(0.0, timelineTime));
                    if (!TryCaptureSourceBoneSample(
                            context.Animator.transform,
                            sourceSamplingCache,
                            sourceBonePaths,
                            sourceBoneTransforms,
                            trackOffsetPosition,
                            trackOffsetRotation,
                            out poseSamples[i],
                            out error))
                    {
                        return false;
                    }
                }
                for (int i = sampleCount; i < clipSampleCount; i++)
                {
                    poseSamples[i] = poseSamples[sampleCount - 1];
                }

                AnimationClip poseClip = null;
                try
                {
                    if (!KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                            poseSamples,
                            frameRate,
                            out poseClip,
                            out error))
                    {
                        return false;
                    }
                    if (writebackClip != null)
                    {
                        string writebackError = writebackClip(poseClip, "SourceBoneClip");
                        if (!string.IsNullOrWhiteSpace(writebackError))
                        {
                            error = writebackError;
                            return false;
                        }
                    }
                    if (!KimodoRetargetSamplingUtility.TryCollectMuscleSamplesFromClip(
                            poseClip,
                            sourceSamplingCache,
                            clipSampleCount,
                            KimodoRetargetClipSamplingUtility.ClipSamplingMode.RawTransform,
                            out MuscleSample[] decoded,
                            out error))
                    {
                        return false;
                    }

                    for (int i = 0; i < sampleCount; i++)
                    {
                        samples[i] = KimodoRetargetSamplingUtility.CloneMuscleSample(decoded[i]);
                    }
                    return true;
                }
                finally
                {
                    if (poseClip != null)
                    {
                        UnityEngine.Object.DestroyImmediate(poseClip);
                    }
                }
            }
            catch (Exception ex)
            {
                samples = null;
                error = ex.Message;
                return false;
            }
        }

        internal bool TryCaptureTargetBoneSamples(
            IReadOnlyList<double> timelineTimes,
            float targetFrameRate,
            out BoneSample[] samples,
            out string error,
            Func<AnimationClip, string, string> writebackClip = null)
        {
            samples = null;
            if (!TryCaptureMuscleSamples(
                    timelineTimes,
                    out MuscleSample[] muscleSamples,
                    out error,
                    writebackClip))
            {
                return false;
            }

            return KimodoRetargetSamplingUtility.TryRetargetMuscleSamplesToBoneSamples(
                muscleSamples,
                targetFrameRate,
                TargetCache,
                out samples,
                out error,
                writebackClip);
        }

        internal bool TryCaptureTargetBoneSamplesAtFrames(
            IReadOnlyList<int> timelineFrames,
            float frameRate,
            out BoneSample[] samples,
            out string error,
            Func<AnimationClip, string, string> writebackClip = null)
        {
            if (timelineFrames == null)
            {
                samples = null;
                error = "Timeline sample frames are null.";
                return false;
            }

            var timelineTimes = new double[timelineFrames.Count];
            for (int i = 0; i < timelineFrames.Count; i++)
            {
                timelineTimes[i] = KimodoTimelinePreviewRefreshUtility.TimelineFrameToTime(
                    Math.Max(0, timelineFrames[i]),
                    frameRate);
            }
            return TryCaptureTargetBoneSamples(
                timelineTimes,
                frameRate,
                out samples,
                out error,
                writebackClip);
        }

        private static bool TryBuildSourceBoneTransforms(
            Transform sourceRoot,
            RetargetSkeleton sourceSamplingCache,
            out string[] sourceBonePaths,
            out Transform[] sourceBoneTransforms,
            out string error)
        {
            sourceBonePaths = null;
            sourceBoneTransforms = null;
            error = string.Empty;
            if (sourceRoot == null || !KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(sourceSamplingCache, out error))
            {
                return false;
            }

            var paths = new List<string> { sourceSamplingCache.canonicalRootBoneName };
            var transforms = new List<Transform> { sourceRoot };
            var seenPaths = new HashSet<string>(StringComparer.Ordinal)
            {
                sourceSamplingCache.canonicalRootBoneName
            };

            HumanBone[] humanBones = sourceSamplingCache.avatar.humanDescription.human;
            for (int i = 0; i < humanBones.Length; i++)
            {
                HumanBone humanBone = humanBones[i];
                if (!Enum.TryParse(humanBone.humanName, out HumanBodyBones humanBodyBone) ||
                    humanBodyBone == HumanBodyBones.LastBone)
                {
                    continue;
                }

                if (!sourceSamplingCache.humanBoneTransforms.TryGetValue(
                        humanBodyBone,
                        out Transform cachedHumanBone) || cachedHumanBone == null)
                {
                    error = $"Source Avatar human bone '{humanBone.humanName}' ('{humanBone.boneName}') is unavailable in the skeleton cache.";
                    sourceBonePaths = null;
                    return false;
                }

                string path = null;
                foreach (KeyValuePair<string, Transform> cachedPath in sourceSamplingCache.bonePathMap)
                {
                    if (cachedPath.Value == cachedHumanBone)
                    {
                        path = cachedPath.Key;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(path))
                {
                    error = $"Source Avatar human bone '{humanBone.humanName}' ('{humanBone.boneName}') has no cached transform path.";
                    sourceBonePaths = null;
                    return false;
                }

                if (!KimodoRetargetAvatarUtility.TryFindUniqueTransformByName(
                        sourceRoot,
                        humanBone.boneName,
                        out Transform sourceBone,
                        out bool ambiguous))
                {
                    error = ambiguous
                        ? $"Source human bone '{humanBone.humanName}' ('{humanBone.boneName}') is ambiguous under '{sourceRoot.name}'."
                        : $"Source human bone '{humanBone.humanName}' ('{humanBone.boneName}') is missing under '{sourceRoot.name}'.";
                    sourceBonePaths = null;
                    sourceBoneTransforms = null;
                    return false;
                }

                if (seenPaths.Add(path))
                {
                    paths.Add(path);
                    transforms.Add(sourceBone);
                }
            }

            sourceBonePaths = paths.ToArray();
            sourceBoneTransforms = transforms.ToArray();
            return true;
        }

        private static bool TryCaptureSourceBoneSample(
            Transform sourceRoot,
            RetargetSkeleton sourceSamplingCache,
            string[] sourceBonePaths,
            Transform[] sourceBoneTransforms,
            Vector3 trackOffsetPosition,
            Quaternion trackOffsetRotation,
            out BoneSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (sourceRoot == null ||
                sourceSamplingCache == null ||
                sourceBonePaths == null ||
                sourceBoneTransforms == null ||
                sourceBonePaths.Length != sourceBoneTransforms.Length)
            {
                error = "Source bone sampling map is invalid.";
                return false;
            }

            int count = sourceBoneTransforms.Length;
            sample = new BoneSample
            {
                boneNames = sourceBonePaths,
                localPositions = new Vector3[count],
                localRotations = new Quaternion[count]
            };
            for (int i = 0; i < count; i++)
            {
                Transform sourceBone = sourceBoneTransforms[i];
                if (sourceBone == null)
                {
                    error = $"Source bone '{sourceSamplingCache.bonePaths[i]}' is unavailable.";
                    sample = null;
                    return false;
                }

                bool isRoot = i == 0;
                if (isRoot)
                {
                    KimodoTimelineTrackOffsetUtility.WorldToTrackPose(
                        sourceRoot.position,
                        sourceRoot.rotation,
                        trackOffsetPosition,
                        trackOffsetRotation,
                        out sample.localPositions[i],
                        out sample.localRotations[i]);
                }
                else
                {
                    sample.localPositions[i] = sourceBone.localPosition;
                    sample.localRotations[i] = sourceBone.localRotation;
                }
            }

            return true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            RestoreSourceState();
            sourceSamplingCache?.Dispose();
            TargetCache.Dispose();
        }

        private void RestoreSourceState()
        {
            if (context?.Director == null)
            {
                return;
            }

            try
            {
                context.Director.extrapolationMode = originalWrapMode;
                evaluationScope?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][TimelineSample] Failed to restore Director state: {ex.Message}");
            }
        }
    }

    internal static class KimodoTimelineConstraintMarkerSampler
    {
        private const double SeamTimeEpsilon = 1e-9;

        private static bool IsSameTimelineFrame(TrackAsset track, double leftTime, double rightTime)
        {
            double frameRate = track?.timelineAsset?.editorSettings.frameRate ?? KimodoMotionModelProfiles.DefaultFrameRate;
            return KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(leftTime, frameRate) ==
                KimodoTimelinePreviewRefreshUtility.TimelineTimeToFrame(rightTime, frameRate);
        }

        internal static bool IsMarkerInClipRange(
            TrackAsset track,
            TimelineClip clipRange,
            double markerTime)
        {
            if (clipRange == null)
            {
                return true;
            }

            if (track == null)
            {
                return (markerTime >= clipRange.start ||
                        KimodoTimelinePreviewRefreshUtility.ApproximatelyTimelineTime(markerTime, clipRange.start)) &&
                    markerTime < clipRange.end;
            }

            if (ReferenceEquals(FindOwningClip(track, markerTime), clipRange))
            {
                return true;
            }

            if (!IsSameTimelineFrame(track, markerTime, clipRange.end))
            {
                return false;
            }

            // A marker on the last displayed frame at the clip end still belongs
            // to the ending clip; an adjacent clip may also resolve the same seam.
            return true;
        }

        internal static TimelineClip FindOwningClip(TrackAsset track, double markerTime)
        {
            TimelineClip owner = null;
            if (track == null)
            {
                return null;
            }

            foreach (TimelineClip clip in track.GetClips())
            {
                if (clip?.asset is not KimodoPlayableClip ||
                    (markerTime < clip.start &&
                        !KimodoTimelinePreviewRefreshUtility.ApproximatelyTimelineTime(markerTime, clip.start)) ||
                    markerTime >= clip.end)
                {
                    continue;
                }

                if (owner == null ||
                    clip.start > owner.start ||
                    Math.Abs(clip.start - owner.start) <= SeamTimeEpsilon && clip.end < owner.end)
                {
                    owner = clip;
                }
            }

            return owner;
        }

        internal static bool TryBuildMarkerSamplesForExport(
            KimodoTimelineInOutConstraintContext context,
            out List<KimodoMarkerSampleResult> samples,
            out string error)
        {
            samples = new List<KimodoMarkerSampleResult>();
            error = string.Empty;

            if (context == null)
            {
                error = "Timeline constraint context is null.";
                return false;
            }

            if (context.Track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            List<KimodoConstraintMarker> markers = CollectMarkersForClip(context.Track, context.SourceClip);
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

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation);

            var resolvedSamples = new KimodoMarkerSampleResult[markers.Count];
            var sampledMarkerIndices = new List<int>();
            for (int i = 0; i < markers.Count; i++)
            {
                KimodoConstraintMarker marker = markers[i];
                if (marker == null)
                {
                    error = "Marker is null.";
                    return false;
                }

                if (CanUseAuthoredValuesWithoutTimelineSampling(marker))
                {
                    if (!TryNormalizeMarkerSample(marker, marker.SampleData, "authored", out resolvedSamples[i], out error))
                    {
                        return false;
                    }
                    KimodoTimelineTrackOffsetUtility.ConvertSampleToTrackSpace(
                        resolvedSamples[i],
                        trackOffsetPosition,
                        trackOffsetRotation);
                }
                else
                {
                    sampledMarkerIndices.Add(i);
                }
            }

            if (sampledMarkerIndices.Count > 0)
            {
                if (!KimodoTimelineSamplingSession.TryCreate(context, context.ModelName, out KimodoTimelineSamplingSession sampler, out error))
                {
                    return false;
                }

                try
                {
                    float frameRate = KimodoTimelineConstraintSampler.ResolveTimelineFrameRate(context);
                    var uniqueFrames = new List<int>();
                    var frameToSample = new Dictionary<int, int>();
                    var markerSampleIndices = new int[sampledMarkerIndices.Count];
                    for (int i = 0; i < sampledMarkerIndices.Count; i++)
                    {
                                int frame = KimodoTimelineConstraintSampler.ResolveTimelineSampleFrame(
                            markers[sampledMarkerIndices[i]].time,
                            frameRate);
                        if (!frameToSample.TryGetValue(frame, out int sampleIndex))
                        {
                            sampleIndex = uniqueFrames.Count;
                            frameToSample.Add(frame, sampleIndex);
                            uniqueFrames.Add(frame);
                        }
                        markerSampleIndices[i] = sampleIndex;
                    }

                    if (!sampler.TryCaptureTargetBoneSamplesAtFrames(
                            uniqueFrames,
                            frameRate,
                            out BoneSample[] targetSamples,
                            out error))
                    {
                        return false;
                    }

                    for (int i = 0; i < sampledMarkerIndices.Count; i++)
                    {
                        int markerIndex = sampledMarkerIndices[i];
                        KimodoConstraintMarker marker = markers[markerIndex];
                        if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                                targetSamples[markerSampleIndices[i]],
                                sampler.TargetCache,
                                context.ModelName,
                                "fullbody",
                                marker.time,
                                out KimodoMarkerSampleResult captured,
                                out error) ||
                            !TryNormalizeMarkerSample(marker, captured, "sampled", out resolvedSamples[markerIndex], out error))
                        {
                            return false;
                        }

                    }
                }
                finally
                {
                    sampler.Dispose();
                }
            }

            samples.AddRange(resolvedSamples);
            return true;
        }

        private static bool TryNormalizeMarkerSample(
            KimodoConstraintMarker marker,
            KimodoMarkerSampleResult captured,
            string mode,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, captured);
            error = sample == null ? $"failed to map {mode} pose to marker sample data" : string.Empty;
            if (sample == null)
            {
                return false;
            }
            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][ConstraintExport] marker='{marker.ConstraintType}' time={marker.time:F3} mode={mode} " +
                $"channels={KimodoConstraintMask.FromSample(sample).muscle}:{KimodoConstraintMask.FromSample(sample).rootPosition}:{KimodoConstraintMask.FromSample(sample).AnyEndEffector} hasHeading={KimodoConstraintMask.IsActive(sample, "rootheading")}");
            return true;
        }

        private static bool CanUseAuthoredValuesWithoutTimelineSampling(KimodoConstraintMarker marker)
        {
            if (marker == null || marker.autoSample)
            {
                return false;
            }

            return true;
        }

        internal static List<KimodoConstraintMarker> CollectMarkersForClip(
            TrackAsset track,
            TimelineClip clipRange)
        {
            var markers = new List<KimodoConstraintMarker>();
            foreach (IMarker marker in track.GetMarkers())
            {
                if (marker is KimodoConstraintMarker kimodoMarker)
                {
                    if (!kimodoMarker.constraintEnabled || kimodoMarker.IsExternal)
                    {
                        continue;
                    }

                    if (!IsMarkerInClipRange(track, clipRange, kimodoMarker.time))
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
