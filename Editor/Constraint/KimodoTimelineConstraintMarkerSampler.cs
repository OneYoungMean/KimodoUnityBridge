using System;
using System.Collections.Generic;
using System.Reflection;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineTrackOffsetUtility
    {
        private static readonly FieldInfo SceneOffsetPositionField = typeof(AnimationTrack).GetField(
            "m_SceneOffsetPosition",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SceneOffsetRotationField = typeof(AnimationTrack).GetField(
            "m_SceneOffsetRotation",
            BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void ResolveWorldOffset(
            TrackAsset track,
            Animator animator,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (track is not AnimationTrack animationTrack)
            {
                return;
            }

            if (UsesExplicitTransformOffset(animationTrack, animator))
            {
                position = animationTrack.position;
                rotation = animationTrack.rotation;
            }
            else if (animator != null)
            {
                position = SceneOffsetPositionField?.GetValue(animationTrack) is Vector3 scenePosition
                    ? scenePosition
                    : animator.transform.localPosition;
                rotation = SceneOffsetRotationField?.GetValue(animationTrack) is Vector3 sceneRotation
                    ? Quaternion.Euler(sceneRotation)
                    : animator.transform.localRotation;
            }
            rotation.Normalize();

            Transform parent = animator != null ? animator.transform.parent : null;
            if (parent != null)
            {
                position = parent.TransformPoint(position);
                rotation = parent.rotation * rotation;
                rotation.Normalize();
            }
        }

        internal static void ApplyToRootPose(
            Vector3 offsetPosition,
            Quaternion offsetRotation,
            ref Vector3 position,
            ref Quaternion rotation)
        {
            position = offsetPosition + offsetRotation * position;
            rotation = (offsetRotation * rotation).normalized;
        }

        private static bool UsesExplicitTransformOffset(AnimationTrack track, Animator animator)
        {
            return track.trackOffset == TrackOffset.ApplyTransformOffsets ||
                (track.trackOffset == TrackOffset.Auto &&
                 (animator == null || animator.runtimeAnimatorController == null));
        }
    }

    internal readonly struct KimodoTimelineConstraintCacheRange
    {
        internal readonly int StartFrame;
        internal readonly int EndFrame;
        internal readonly int BakedStartFrame;
        internal readonly int BakedEndFrame;
        internal readonly float FrameRate;

        internal KimodoTimelineConstraintCacheRange(
            int startFrame,
            int endFrame,
            int bakedStartFrame,
            int bakedEndFrame,
            float frameRate)
        {
            StartFrame = startFrame;
            EndFrame = endFrame;
            BakedStartFrame = bakedStartFrame;
            BakedEndFrame = bakedEndFrame;
            FrameRate = frameRate;
        }

        internal int BakedFrameCount => Mathf.Max(2, BakedEndFrame - BakedStartFrame + 1);

        internal float ResolveLocalSampleTime(double timelineTime)
        {
            float localTime = (float)(Math.Max(0.0, timelineTime) - BakedStartFrame / FrameRate);
            return Mathf.Clamp(localTime, 0f, (BakedFrameCount - 1) / FrameRate);
        }
    }

    internal readonly struct KimodoTimelineConstraintCacheKey : IEquatable<KimodoTimelineConstraintCacheKey>
    {
        internal readonly int TrackId;
        internal readonly int AnimatorId;
        internal readonly int AvatarId;
        internal readonly int StartFrame;
        internal readonly int EndFrame;
        internal readonly float FrameRate;
        internal readonly string ModelName;
        internal readonly int TrackDirtyIndex;

        internal KimodoTimelineConstraintCacheKey(
            int trackId,
            int animatorId,
            int avatarId,
            KimodoTimelineConstraintCacheRange range,
            string modelName,
            int trackDirtyIndex = 0)
        {
            TrackId = trackId;
            AnimatorId = animatorId;
            AvatarId = avatarId;
            StartFrame = range.StartFrame;
            EndFrame = range.EndFrame;
            FrameRate = range.FrameRate;
            ModelName = modelName ?? string.Empty;
            TrackDirtyIndex = trackDirtyIndex;
        }

        public bool Equals(KimodoTimelineConstraintCacheKey other)
        {
            return TrackId == other.TrackId &&
                AnimatorId == other.AnimatorId &&
                AvatarId == other.AvatarId &&
                StartFrame == other.StartFrame &&
                EndFrame == other.EndFrame &&
                FrameRate.Equals(other.FrameRate) &&
                TrackDirtyIndex == other.TrackDirtyIndex &&
                string.Equals(ModelName, other.ModelName, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is KimodoTimelineConstraintCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = TrackId;
                hash = hash * 397 ^ AnimatorId;
                hash = hash * 397 ^ AvatarId;
                hash = hash * 397 ^ StartFrame;
                hash = hash * 397 ^ EndFrame;
                hash = hash * 397 ^ FrameRate.GetHashCode();
                hash = hash * 397 ^ TrackDirtyIndex;
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(ModelName ?? string.Empty);
                return hash;
            }
        }
    }

    internal readonly struct KimodoTimelineConstraintSampleKey : IEquatable<KimodoTimelineConstraintSampleKey>
    {
        private readonly long timelineTimeBits;
        private readonly string markerType;

        internal KimodoTimelineConstraintSampleKey(double timelineTime, string markerType)
        {
            timelineTimeBits = BitConverter.DoubleToInt64Bits(timelineTime);
            this.markerType = markerType ?? string.Empty;
        }

        public bool Equals(KimodoTimelineConstraintSampleKey other)
        {
            return timelineTimeBits == other.timelineTimeBits &&
                string.Equals(markerType, other.markerType, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is KimodoTimelineConstraintSampleKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return timelineTimeBits.GetHashCode() * 397 ^
                    StringComparer.OrdinalIgnoreCase.GetHashCode(markerType ?? string.Empty);
            }
        }
    }

    internal sealed class KimodoTimelineConstraintCacheEntry
    {
        internal AnimationClip Clip;
        internal Vector3[] SourceHipsPositions;
        internal Quaternion[] SourceHipsRotations;
        internal readonly Dictionary<KimodoTimelineConstraintSampleKey, KimodoMarkerSampleResult> MarkerSamples =
            new Dictionary<KimodoTimelineConstraintSampleKey, KimodoMarkerSampleResult>();
    }

    internal static class KimodoTimelineConstraintClipCache
    {
        private const int GuardFrames = 1;
        private static readonly Dictionary<KimodoTimelineConstraintCacheKey, KimodoTimelineConstraintCacheEntry> Entries =
            new Dictionary<KimodoTimelineConstraintCacheKey, KimodoTimelineConstraintCacheEntry>();

        static KimodoTimelineConstraintClipCache()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
        }

        internal static KimodoTimelineConstraintCacheRange ResolveRange(
            double timelineTime,
            double trackEndTime,
            int cacheTimeFrames,
            float frameRate = KimodoPlayableClip.FIXED_FRAME_RATE)
        {
            float fps = Mathf.Max(1f, frameRate);
            int bucketFrames = Mathf.Max(1, cacheTimeFrames);
            int trackEndFrame = Mathf.Max(
                1,
                (int)Math.Ceiling(Math.Max(trackEndTime, 1.0 / fps) * fps - 1e-9));
            int sampleFrame = Mathf.Clamp(
                (int)Math.Floor(Math.Max(0.0, timelineTime) * fps + 1e-9),
                0,
                trackEndFrame - 1);
            int startFrame = sampleFrame / bucketFrames * bucketFrames;
            int endFrame = Mathf.Min(startFrame + bucketFrames, trackEndFrame);
            int bakedStartFrame = Mathf.Max(0, startFrame - GuardFrames);
            int bakedEndFrame = Mathf.Min(trackEndFrame, endFrame + GuardFrames - 1);
            return new KimodoTimelineConstraintCacheRange(
                startFrame,
                endFrame,
                bakedStartFrame,
                bakedEndFrame,
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
            return TrySampleMarker(
                context,
                timelineTime,
                exportedSampleTime,
                markerType,
                modelName,
                forceRefresh: false,
                out sample,
                out error);
        }

        internal static bool TrySampleMarker(
            KimodoTimelineInOutConstraintContext context,
            double timelineTime,
            double exportedSampleTime,
            string markerType,
            string modelName,
            bool forceRefresh,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            if (!TryGetOrCreateMuscleClip(
                    context,
                    timelineTime,
                    modelName,
                    forceRefresh,
                    out KimodoTimelineConstraintCacheEntry entry,
                    out KimodoTimelineConstraintCacheRange range,
                    out error))
            {
                return false;
            }

            var sampleKey = new KimodoTimelineConstraintSampleKey(timelineTime, markerType);
            if (entry.MarkerSamples.TryGetValue(sampleKey, out KimodoMarkerSampleResult cachedSample))
            {
                sample = cachedSample.Clone();
                sample.sampleTime = exportedSampleTime;
                return true;
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

            SkeletonCache targetCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        targetAvatar,
                        "KimodoTimelineConstraintCache_Target",
                        out targetCache,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromHumanoidClip(
                        entry.Clip,
                        targetCache,
                        range.ResolveLocalSampleTime(timelineTime),
                        out BoneSample targetSample,
                        out _,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        targetSample,
                        targetCache,
                        modelName,
                        markerType,
                        exportedSampleTime,
                        out sample,
                        out error))
                {
                    return false;
                }

                if (!TryResolveCachedSourceHipsPose(
                        entry,
                        range,
                        timelineTime,
                        out Vector3 sourceHipsPosition,
                        out Quaternion sourceHipsRotation))
                {
                    sample = null;
                    error = "Timeline source Hips pose cache is missing or invalid.";
                    return false;
                }

                KimodoTimelinePoseSampler.ApplySourceHipsPose(
                    sample,
                    sourceHipsPosition,
                    sourceHipsRotation,
                    exportedSampleTime);
                KimodoMarkerSampleResult cached = sample.Clone();
                cached.sampleTime = timelineTime;
                entry.MarkerSamples[sampleKey] = cached;
                return true;
            }
            finally
            {
                targetCache?.Dispose();
            }
        }

        internal static void Clear()
        {
            foreach (KimodoTimelineConstraintCacheEntry entry in Entries.Values)
            {
                if (entry?.Clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.Clip);
                }
            }
            Entries.Clear();
        }

        private static bool TryGetOrCreateMuscleClip(
            KimodoTimelineInOutConstraintContext context,
            double timelineTime,
            string modelName,
            bool forceRefresh,
            out KimodoTimelineConstraintCacheEntry entry,
            out KimodoTimelineConstraintCacheRange range,
            out string error)
        {
            entry = null;
            range = default;
            error = string.Empty;
            if (context?.Track == null || context.Director == null || context.Animator == null)
            {
                error = "Timeline track, director or Animator is missing.";
                return false;
            }

            int cacheTimeFrames = KimodoPlayableClipGenerationSettings.instance.TimelineConstraintCacheTimeFrames;
            range = ResolveRange(
                timelineTime,
                ResolveTrackEndTime(context.Track, context.SourceClip),
                cacheTimeFrames);
            var key = new KimodoTimelineConstraintCacheKey(
                context.Track.GetInstanceID(),
                context.Animator.GetInstanceID(),
                context.SourceAvatar != null ? context.SourceAvatar.GetInstanceID() : 0,
                range,
                modelName,
                KimodoTimelinePreviewRefreshUtility.GetDirtyIndex(context.Track));
            if (forceRefresh)
            {
                Invalidate(key);
            }
            if (Entries.TryGetValue(key, out entry) && entry?.Clip != null)
            {
                return true;
            }
            RemoveStaleEntries(key);

            if (!KimodoTimelinePoseSampler.TryCreate(context, modelName, out KimodoTimelinePoseSampler sampler, out error))
            {
                return false;
            }

            var samples = new List<MuscleSample>(range.BakedFrameCount);
            var sourceHipsPositions = new Vector3[range.BakedFrameCount];
            var sourceHipsRotations = new Quaternion[range.BakedFrameCount];
            try
            {
                for (int i = 0; i < range.BakedFrameCount; i++)
                {
                    int sampleFrame = Mathf.Min(range.BakedStartFrame + i, range.BakedEndFrame);
                    if (!sampler.TryCaptureMuscleSample(
                            sampleFrame / range.FrameRate,
                            normalizeRootToAnchor: false,
                            Vector3.zero,
                            Quaternion.identity,
                            out MuscleSample muscleSample,
                            out error))
                    {
                        return false;
                    }

                    samples.Add(muscleSample);
                    if (!sampler.TryGetSourceHipsPose(
                            out sourceHipsPositions[i],
                            out sourceHipsRotations[i],
                            out error))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                sampler.Dispose();
            }

            if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                    samples,
                    range.FrameRate,
                    out AnimationClip clip,
                    out error))
            {
                return false;
            }

            clip.name = "KimodoTimelineConstraintCache";
            entry = new KimodoTimelineConstraintCacheEntry
            {
                Clip = clip,
                SourceHipsPositions = sourceHipsPositions,
                SourceHipsRotations = sourceHipsRotations
            };
            Entries[key] = entry;
            return true;
        }

        internal static bool Invalidate(KimodoTimelineConstraintCacheKey key)
        {
            if (!Entries.TryGetValue(key, out KimodoTimelineConstraintCacheEntry entry))
            {
                return false;
            }

            if (entry?.Clip != null)
            {
                UnityEngine.Object.DestroyImmediate(entry.Clip);
            }
            return Entries.Remove(key);
        }

        private static void RemoveStaleEntries(KimodoTimelineConstraintCacheKey current)
        {
            List<KimodoTimelineConstraintCacheKey> stale = null;
            foreach (KeyValuePair<KimodoTimelineConstraintCacheKey, KimodoTimelineConstraintCacheEntry> pair in Entries)
            {
                KimodoTimelineConstraintCacheKey key = pair.Key;
                if (key.TrackId != current.TrackId || key.AnimatorId != current.AnimatorId)
                {
                    continue;
                }
                if (key.TrackDirtyIndex == current.TrackDirtyIndex &&
                    key.AvatarId == current.AvatarId &&
                    key.FrameRate.Equals(current.FrameRate) &&
                    string.Equals(key.ModelName, current.ModelName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (pair.Value?.Clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(pair.Value.Clip);
                }
                stale ??= new List<KimodoTimelineConstraintCacheKey>();
                stale.Add(key);
            }
            if (stale == null)
            {
                return;
            }
            for (int i = 0; i < stale.Count; i++)
            {
                Entries.Remove(stale[i]);
            }
        }

        private static bool TryResolveCachedSourceHipsPose(
            KimodoTimelineConstraintCacheEntry entry,
            KimodoTimelineConstraintCacheRange range,
            double timelineTime,
            out Vector3 position,
            out Quaternion rotation)
        {
            float frame = range.ResolveLocalSampleTime(timelineTime) * range.FrameRate;
            return TryInterpolateSourceHipsPose(
                entry?.SourceHipsPositions,
                entry?.SourceHipsRotations,
                frame,
                out position,
                out rotation);
        }

        internal static bool TryInterpolateSourceHipsPose(
            IReadOnlyList<Vector3> positions,
            IReadOnlyList<Quaternion> rotations,
            float frame,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (positions == null || rotations == null || positions.Count == 0 || positions.Count != rotations.Count)
            {
                return false;
            }

            float clampedFrame = Mathf.Clamp(frame, 0f, positions.Count - 1);
            int first = Mathf.FloorToInt(clampedFrame);
            int second = Mathf.Min(first + 1, positions.Count - 1);
            float blend = clampedFrame - first;
            position = Vector3.Lerp(positions[first], positions[second], blend);
            rotation = Quaternion.Slerp(rotations[first], rotations[second], blend).normalized;
            return true;
        }

        private static double ResolveTrackEndTime(TrackAsset track, TimelineClip sourceClip)
        {
            double endTime = sourceClip != null ? sourceClip.end : 0.0;
            if (track != null)
            {
                foreach (TimelineClip clip in track.GetClips())
                {
                    if (clip != null)
                    {
                        endTime = Math.Max(endTime, clip.end);
                    }
                }
            }

            return Math.Max(endTime, 1.0 / KimodoPlayableClip.FIXED_FRAME_RATE);
        }
    }

    internal sealed class KimodoTimelinePoseSampler : IDisposable
    {
        private readonly KimodoTimelineInOutConstraintContext context;
        private readonly HumanPoseHandler sourcePoseHandler;
        private readonly float sourceHumanScale;
        private readonly double originalTime;
        private readonly DirectorWrapMode originalWrapMode;
        private bool disposed;

        private KimodoTimelinePoseSampler(
            KimodoTimelineInOutConstraintContext context,
            HumanPoseHandler sourcePoseHandler,
            float sourceHumanScale,
            SkeletonCache targetCache)
        {
            this.context = context;
            this.sourcePoseHandler = sourcePoseHandler;
            this.sourceHumanScale = sourceHumanScale;
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
            SkeletonCache sourceScaleCache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        context.SourceAvatar,
                        "KimodoTimelinePoseSampler_SourceScale",
                        out sourceScaleCache,
                        out error))
                {
                    targetCache.Dispose();
                    return false;
                }
                float sourceHumanScale = sourceScaleCache.humanScale;
                sourceScaleCache.Dispose();
                sourceScaleCache = null;
                sourceHandler = new HumanPoseHandler(context.SourceAvatar, context.Animator.transform);
                sampler = new KimodoTimelinePoseSampler(
                    context,
                    sourceHandler,
                    sourceHumanScale,
                    targetCache);
                return true;
            }
            catch (Exception ex)
            {
                sourceHandler?.Dispose();
                sourceScaleCache?.Dispose();
                targetCache.Dispose();
                error = ex.Message;
                return false;
            }
        }

        internal bool TryEvaluate(double timelineTime, out string error)
        {
            return TryEvaluate(
                timelineTime,
                normalizeRootToAnchor: false,
                Vector3.zero,
                Quaternion.identity,
                out error);
        }

        internal bool TryEvaluate(
            double timelineTime,
            bool normalizeRootToAnchor,
            Vector3 anchorRootPosition,
            Quaternion anchorRootRotation,
            out string error)
        {
            if (!TryCaptureMuscleSample(
                    timelineTime,
                    normalizeRootToAnchor,
                    anchorRootPosition,
                    anchorRootRotation,
                    out MuscleSample sample,
                    out error))
            {
                return false;
            }

            HumanPose pose = sample.pose;
            TargetCache.poseHandler.SetHumanPose(ref pose);
            return true;
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

            try
            {
                context.Director.time = Math.Max(0.0, timelineTime);
                context.Director.Evaluate();
                var pose = new HumanPose();
                sourcePoseHandler.GetHumanPose(ref pose);
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref pose);
                sample = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(
                    context.SourceAvatar,
                    sourceHumanScale,
                    pose,
                    bone => ResolveSourceHumanBone(context.Animator, context.SourceAvatar, bone));
                Vector3 bodyPosition = pose.bodyPosition * sourceHumanScale;
                Quaternion bodyRotation = pose.bodyRotation;
                if (normalizeRootToAnchor)
                {
                    KimodoConstraintNormalizationUtility.NormalizeRootPose(
                        anchorRootPosition,
                        anchorRootRotation,
                        ref bodyPosition,
                        ref bodyRotation);
                }
                if (normalizeRootToAnchor)
                {
                    pose.bodyPosition = bodyPosition / sourceHumanScale;
                    pose.bodyRotation = bodyRotation;
                }
                sample.pose = pose;
                return true;
            }
            catch (Exception ex)
            {
                sample = null;
                error = ex.Message;
                return false;
            }
        }

        internal bool TryGetSourceHipsPose(
            out Vector3 position,
            out Quaternion rotation,
            out string error)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            Transform sourceHips = ResolveSourceHumanBone(
                context.Animator,
                context.SourceAvatar,
                HumanBodyBones.Hips);
            if (sourceHips == null)
            {
                error = "Timeline source Animator has no Hips bone.";
                return false;
            }

            position = sourceHips.position;
            rotation = sourceHips.rotation;
            error = string.Empty;
            return true;
        }

        internal static void ApplySourceHipsPose(
            KimodoMarkerSampleResult sample,
            Vector3 sourceHipsPosition,
            Quaternion sourceHipsRotation,
            double exportedSampleTime)
        {
            if (sample == null)
            {
                return;
            }

            Vector3 sourceForward = Vector3.ProjectOnPlane(sourceHipsRotation * Vector3.forward, Vector3.up);
            if (sourceForward.sqrMagnitude <= 1e-8f)
            {
                sourceForward = Vector3.forward;
            }
            sourceForward.Normalize();

            Quaternion sampledRootRotation = sample.unityRootRot;
            Quaternion restoredRootRotation = Quaternion.LookRotation(sourceForward, Vector3.up);
            if (sample.localAxisAngles != null && sample.localAxisAngles.Count > 0)
            {
                Vector3 hipsAxisAngle = sample.localAxisAngles[0];
                Quaternion sampledHipsRotation = hipsAxisAngle.sqrMagnitude > 1e-16f
                    ? Quaternion.AngleAxis(hipsAxisAngle.magnitude * Mathf.Rad2Deg, hipsAxisAngle.normalized)
                    : Quaternion.identity;
                Quaternion rootDelta = restoredRootRotation * Quaternion.Inverse(sampledRootRotation);
                Quaternion restoredHipsRotation = (rootDelta * sampledHipsRotation).normalized;
                sample.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(restoredHipsRotation);
            }

            // Humanoid Clip sampling keeps the pose and Foot IK but drops the Timeline clip's world root anchor.
            sample.kimodoRootPosition = new Vector3(
                sourceHipsPosition.x,
                sample.kimodoRootPosition.y,
                sourceHipsPosition.z);
            sample.unityRootPos = sourceHipsPosition;
            sample.unityRootRot = restoredRootRotation;
            sample.rootHeading = new Vector2(sourceForward.x, sourceForward.z);
            sample.hasRootHeading = true;
            sample.sampleTime = exportedSampleTime;
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
            if (!TryCaptureMuscleSample(
                    timelineTime,
                    normalizeRootToAnchor: false,
                    Vector3.zero,
                    Quaternion.identity,
                    out MuscleSample muscleSample,
                    out error))
            {
                return false;
            }

            BoneSample targetSample;
            if (string.Equals(markerType, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                float frameRate = KimodoMotionModelProfiles.TryGetArdy(
                        modelName,
                        out KimodoMotionModelProfile profile)
                    ? profile.SourceFps
                    : KimodoPlayableClip.FIXED_FRAME_RATE;
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        muscleSample,
                        frameRate,
                        TargetCache,
                        out targetSample,
                        out _,
                        out error))
                {
                    return false;
                }
            }
            else
            {
                HumanPose pose = muscleSample.pose;
                TargetCache.poseHandler.SetHumanPose(ref pose);
                targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(TargetCache);
            }

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

            if (!TryGetSourceHipsPose(
                    out Vector3 sourceHipsPosition,
                    out Quaternion sourceHipsRotation,
                    out error))
            {
                return false;
            }

            ApplySourceHipsPose(
                sample,
                sourceHipsPosition,
                sourceHipsRotation,
                exportedSampleTime);
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

            for (int i = 0; i < markers.Count; i++)
            {
                if (!TryBuildMarkerSample(markers[i], context, out KimodoMarkerSampleResult sample, out error))
                {
                    return false;
                }

                samples.Add(sample);
            }

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

            string mode;
            if (CanUseOverrideWithoutTimelineSampling(marker))
            {
                sample = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, marker.SampleData);
                if (sample == null)
                {
                    error = "failed to read override marker data";
                    return false;
                }

                mode = "override";
            }
            else
            {
                if (!KimodoTimelineConstraintClipCache.TrySampleMarker(
                        context,
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
                mode = "sampled";
            }

            Debug.Log(
                $"[Kimodo][ConstraintExport] marker='{marker.ConstraintType}' time={marker.time:F3} mode={mode} " +
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
