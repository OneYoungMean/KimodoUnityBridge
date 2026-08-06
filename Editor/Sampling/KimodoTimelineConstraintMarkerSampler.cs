using System;
using System.Collections.Generic;
using System.Text;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintPoseDiagnostics
    {
        private const float MuscleEpsilon = 1e-4f;

        internal static string BuildMuscleStats(float[] muscles)
        {
            int count = muscles != null ? muscles.Length : 0;
            int nonZero = 0;
            float sum = 0f;
            float max = 0f;
            var topIndices = new int[5] { -1, -1, -1, -1, -1 };
            var topValues = new float[5];
            for (int i = 0; i < count; i++)
            {
                float value = muscles[i];
                float abs = Mathf.Abs(value);
                sum += abs;
                max = Mathf.Max(max, abs);
                if (abs > MuscleEpsilon)
                {
                    nonZero++;
                }

                for (int top = 0; top < topValues.Length; top++)
                {
                    if (abs <= topValues[top])
                    {
                        continue;
                    }
                    for (int shift = topValues.Length - 1; shift > top; shift--)
                    {
                        topValues[shift] = topValues[shift - 1];
                        topIndices[shift] = topIndices[shift - 1];
                    }
                    topValues[top] = abs;
                    topIndices[top] = i;
                    break;
                }
            }

            var topText = new StringBuilder();
            for (int i = 0; i < topIndices.Length; i++)
            {
                int index = topIndices[i];
                if (index < 0 || index >= count)
                {
                    continue;
                }
                if (topText.Length > 0)
                {
                    topText.Append(", ");
                }
                string name = index < HumanTrait.MuscleCount ? HumanTrait.MuscleName[index] : $"muscle_{index}";
                topText.Append(name).Append('=').Append(muscles[index].ToString("F5"));
            }

            return $"len={count} nonZero={nonZero} allNearZero={nonZero == 0} absMean={(count > 0 ? sum / count : 0f):F6} absMax={max:F6} top=[{topText}]";
        }

        internal static string BuildMuscleValues(float[] muscles)
        {
            int count = muscles != null ? muscles.Length : 0;
            var text = new StringBuilder(count * 9);
            text.Append('[');
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }
                text.Append(muscles[i].ToString("F5"));
            }
            return text.Append(']').ToString();
        }

        internal static string BuildMuscleDiff(float[] left, float[] right)
        {
            int leftCount = left != null ? left.Length : 0;
            int rightCount = right != null ? right.Length : 0;
            int count = Mathf.Min(leftCount, rightCount);
            float sum = 0f;
            float max = 0f;
            int maxIndex = -1;
            int changed = 0;
            for (int i = 0; i < count; i++)
            {
                float delta = Mathf.Abs(left[i] - right[i]);
                sum += delta;
                if (delta > MuscleEpsilon)
                {
                    changed++;
                }
                if (delta > max)
                {
                    max = delta;
                    maxIndex = i;
                }
            }

            string maxName = maxIndex >= 0 && maxIndex < HumanTrait.MuscleCount
                ? HumanTrait.MuscleName[maxIndex]
                : maxIndex >= 0 ? $"muscle_{maxIndex}" : "none";
            return $"leftLen={leftCount} rightLen={rightCount} compared={count} changed={changed} " +
                $"absMean={(count > 0 ? sum / count : 0f):F6} absMax={max:F6} maxIndex={maxIndex} maxName='{maxName}'";
        }

        internal static void LogDragMuscleSnapshot(
            int snapshotId,
            string markerType,
            double timelineTime,
            MuscleSample timelinePose,
            float timelinePoseScale,
            MuscleSample virtualSkeleton,
            float virtualSkeletonScale,
            MuscleSample targetCharacter,
            float targetCharacterScale,
            string details = null)
        {
            var text = new StringBuilder(8192);
            text.Append("[Kimodo][ConstraintDragMuscles][snapshot=")
                .Append(snapshotId)
                .Append("] marker='").Append(markerType ?? string.Empty)
                .Append("' timelineTime=").Append(timelineTime.ToString("R")).Append('s')
                .Append(FormatDetails(details)).AppendLine();
            AppendDragMuscleSample(text, "TimelinePoseClip", timelinePose, timelinePoseScale);
            AppendDragMuscleSample(text, "VirtualSkeleton", virtualSkeleton, virtualSkeletonScale);
            AppendDragMuscleSample(text, "TargetCharacter", targetCharacter, targetCharacterScale);
            AppendDragMuscleDiff(text, "TimelinePoseClip->VirtualSkeleton", timelinePose, timelinePoseScale, virtualSkeleton, virtualSkeletonScale);
            AppendDragMuscleDiff(text, "VirtualSkeleton->TargetCharacter", virtualSkeleton, virtualSkeletonScale, targetCharacter, targetCharacterScale);
            KimodoPlayableClipGenerationSettings.DebugLog(text.ToString().TrimEnd());
        }

        private static void AppendDragMuscleSample(
            StringBuilder text,
            string label,
            MuscleSample sample,
            float humanScale)
        {
            HumanPose pose = sample != null ? sample.pose : default;
            text.Append("  ").Append(label)
                .Append(": bodyMeters=").Append(FormatVector(pose.bodyPosition * humanScale))
                .Append(" bodyRotation=").Append(FormatQuaternion(pose.bodyRotation))
                .Append(" stats={").Append(BuildMuscleStats(pose.muscles)).Append('}')
                .Append(" values=").Append(BuildMuscleValues(pose.muscles))
                .AppendLine();
        }

        private static void AppendDragMuscleDiff(
            StringBuilder text,
            string label,
            MuscleSample left,
            float leftScale,
            MuscleSample right,
            float rightScale)
        {
            HumanPose leftPose = left != null ? left.pose : default;
            HumanPose rightPose = right != null ? right.pose : default;
            text.Append("  Diff ").Append(label).Append(": ")
                .Append(BuildMuscleDiff(leftPose.muscles, rightPose.muscles))
                .Append(" bodyMetersDistance=")
                .Append(Vector3.Distance(leftPose.bodyPosition * leftScale, rightPose.bodyPosition * rightScale).ToString("F6"))
                .Append(" bodyRotationAngleDeg=")
                .Append(Quaternion.Angle(leftPose.bodyRotation, rightPose.bodyRotation).ToString("F4"))
                .AppendLine();
        }

        private static string FormatVector(Vector3 value) => $"({value.x:F5},{value.y:F5},{value.z:F5})";
        private static string FormatQuaternion(Quaternion value) => $"({value.x:F5},{value.y:F5},{value.z:F5},{value.w:F5})";
        private static string FormatDetails(string details) => string.IsNullOrWhiteSpace(details) ? string.Empty : $" {details}";
    }

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
        internal readonly int AvatarDirtyCount;
        internal readonly int StartFrame;
        internal readonly int EndFrame;
        internal readonly float FrameRate;
        internal readonly string ModelName;
        internal readonly int SourceSignature;
        internal readonly Vector3 TrackOffsetPosition;
        internal readonly Quaternion TrackOffsetRotation;

        internal KimodoTimelineConstraintCacheKey(
            int trackId,
            int animatorId,
            int avatarId,
            KimodoTimelineConstraintCacheRange range,
            string modelName,
            int sourceSignature = 0,
            Vector3 trackOffsetPosition = default,
            Quaternion trackOffsetRotation = default,
            int avatarDirtyCount = 0)
        {
            TrackId = trackId;
            AnimatorId = animatorId;
            AvatarId = avatarId;
            AvatarDirtyCount = avatarDirtyCount;
            StartFrame = range.StartFrame;
            EndFrame = range.EndFrame;
            FrameRate = range.FrameRate;
            ModelName = modelName ?? string.Empty;
            SourceSignature = sourceSignature;
            TrackOffsetPosition = trackOffsetPosition;
            TrackOffsetRotation = trackOffsetRotation == default
                ? Quaternion.identity
                : trackOffsetRotation.normalized;
        }

        public bool Equals(KimodoTimelineConstraintCacheKey other)
        {
            return TrackId == other.TrackId &&
                AnimatorId == other.AnimatorId &&
                AvatarId == other.AvatarId &&
                AvatarDirtyCount == other.AvatarDirtyCount &&
                StartFrame == other.StartFrame &&
                EndFrame == other.EndFrame &&
                FrameRate.Equals(other.FrameRate) &&
                SourceSignature == other.SourceSignature &&
                TrackOffsetPosition.Equals(other.TrackOffsetPosition) &&
                TrackOffsetRotation.Equals(other.TrackOffsetRotation) &&
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
                hash = hash * 397 ^ AvatarDirtyCount;
                hash = hash * 397 ^ StartFrame;
                hash = hash * 397 ^ EndFrame;
                hash = hash * 397 ^ FrameRate.GetHashCode();
                hash = hash * 397 ^ SourceSignature;
                hash = hash * 397 ^ TrackOffsetPosition.GetHashCode();
                hash = hash * 397 ^ TrackOffsetRotation.GetHashCode();
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(ModelName ?? string.Empty);
                return hash;
            }
        }
    }

    internal sealed class KimodoTimelineConstraintCacheEntry : IDisposable
    {
        internal AnimationClip Clip;
        internal SkeletonCache TargetCache;
        private KimodoRetargetClipSamplingUtility.ClipSamplingSession targetSession;
        private float lastSampleLocalTime;
        private bool hasLastSampleLocalTime;

        internal bool TrySampleTargetBone(float localTime, out BoneSample sample, out string error)
        {
            sample = null;
            error = string.Empty;
            if (Clip == null)
            {
                error = "Timeline constraint cache clip is null.";
                return false;
            }
            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(TargetCache, out error))
            {
                return false;
            }

            if (hasLastSampleLocalTime && localTime + 1e-6f < lastSampleLocalTime)
            {
                ResetTargetSession();
            }
            if (!EnsureTargetSession(out error))
            {
                return false;
            }

            if (KimodoRetargetSamplingUtility.TrySampleBoneClipSession(
                    targetSession,
                    localTime,
                    out sample,
                    out error))
            {
                lastSampleLocalTime = localTime;
                hasLastSampleLocalTime = true;
                return true;
            }

            ResetTargetSession();
            if (!EnsureTargetSession(out string retryError) ||
                !KimodoRetargetSamplingUtility.TrySampleBoneClipSession(
                    targetSession,
                    localTime,
                    out sample,
                    out retryError))
            {
                error = string.IsNullOrWhiteSpace(retryError) ? error : retryError;
                return false;
            }

            lastSampleLocalTime = localTime;
            hasLastSampleLocalTime = true;
            error = string.Empty;
            return true;
        }

        private bool EnsureTargetSession(out string error)
        {
            error = string.Empty;
            if (targetSession != null)
            {
                return true;
            }

            bool created = KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                Clip,
                TargetCache,
                "KimodoTimelineConstraintCache_TargetBoneSampler",
                KimodoRetargetClipSamplingUtility.ClipSamplingMode.RawTransform,
                out targetSession,
                out error);
            hasLastSampleLocalTime = false;
            return created;
        }

        private void ResetTargetSession()
        {
            targetSession?.Dispose();
            targetSession = null;
            hasLastSampleLocalTime = false;
        }

        public void Dispose()
        {
            ResetTargetSession();
            TargetCache?.Dispose();
            TargetCache = null;
            if (Clip != null)
            {
                UnityEngine.Object.DestroyImmediate(Clip);
                Clip = null;
            }
        }
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
                KimodoFrameTimeUtility.SecondsToFrameCount(
                    Math.Max(trackEndTime, 1.0 / fps),
                    fps));
            int sampleFrame = Mathf.Clamp(
                ResolveTimelineSampleFrame(timelineTime, fps),
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

        internal static float ResolveTimelineFrameRate(KimodoTimelineInOutConstraintContext context)
        {
            TimelineAsset timelineAsset = context?.Director?.playableAsset as TimelineAsset ??
                context?.Track?.timelineAsset ??
                context?.SourceClip?.GetParentTrack()?.timelineAsset;
            double frameRate = timelineAsset?.editorSettings.frameRate ?? KimodoPlayableClip.FIXED_FRAME_RATE;
            return (float)Math.Max(1.0, frameRate);
        }

        internal static int ComputeSamplingSourceSignature(TrackAsset track)
        {
            unchecked
            {
                int hash = 17;
                // Track DirtyIndex also changes when constraint markers write sampled data.
                // Hash only state that can change the evaluated animation pose.
                hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(track);
                if (track == null)
                {
                    return hash;
                }

                hash = hash * 31 + (track.muted ? 1 : 0);
                hash = AppendObjectSignature(hash, track.curves);
                if (track is AnimationTrack animationTrack)
                {
                    hash = hash * 31 + animationTrack.trackOffset.GetHashCode();
                    hash = hash * 31 + animationTrack.position.GetHashCode();
                    hash = hash * 31 + animationTrack.rotation.GetHashCode();
                    hash = hash * 31 + animationTrack.matchTargetFields.GetHashCode();
                    hash = hash * 31 + (animationTrack.applyAvatarMask ? 1 : 0);
                    hash = AppendObjectSignature(hash, animationTrack.avatarMask);
                    hash = AppendObjectSignature(hash, animationTrack.infiniteClip);
                    hash = hash * 31 + animationTrack.infiniteClipOffsetPosition.GetHashCode();
                    hash = hash * 31 + animationTrack.infiniteClipOffsetRotation.GetHashCode();
                    hash = hash * 31 + animationTrack.infiniteClipPreExtrapolation.GetHashCode();
                    hash = hash * 31 + animationTrack.infiniteClipPostExtrapolation.GetHashCode();
                }

                foreach (TimelineClip clip in track.GetClips())
                {
                    if (clip == null)
                    {
                        continue;
                    }

                    hash = hash * 31 + clip.start.GetHashCode();
                    hash = hash * 31 + clip.duration.GetHashCode();
                    hash = hash * 31 + clip.clipIn.GetHashCode();
                    hash = hash * 31 + clip.timeScale.GetHashCode();
                    hash = hash * 31 + clip.easeInDuration.GetHashCode();
                    hash = hash * 31 + clip.easeOutDuration.GetHashCode();
                    hash = hash * 31 + clip.blendInDuration.GetHashCode();
                    hash = hash * 31 + clip.blendOutDuration.GetHashCode();
                    hash = hash * 31 + clip.blendInCurveMode.GetHashCode();
                    hash = hash * 31 + clip.blendOutCurveMode.GetHashCode();
                    hash = AppendCurveSignature(hash, clip.mixInCurve);
                    hash = AppendCurveSignature(hash, clip.mixOutCurve);
                    hash = hash * 31 + clip.preExtrapolationMode.GetHashCode();
                    hash = hash * 31 + clip.postExtrapolationMode.GetHashCode();
                    hash = AppendObjectSignature(hash, clip.curves);
                    if (clip.asset is AnimationPlayableAsset animationAsset)
                    {
                        hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(animationAsset);
                        hash = AppendObjectSignature(hash, animationAsset.clip);
                        hash = hash * 31 + animationAsset.position.GetHashCode();
                        hash = hash * 31 + animationAsset.rotation.GetHashCode();
                        hash = hash * 31 + (animationAsset.removeStartOffset ? 1 : 0);
                        hash = hash * 31 + (animationAsset.applyFootIK ? 1 : 0);
                        hash = hash * 31 + (animationAsset.useTrackMatchFields ? 1 : 0);
                        hash = hash * 31 + animationAsset.matchTargetFields.GetHashCode();
                        hash = hash * 31 + animationAsset.loop.GetHashCode();
                    }
                    else
                    {
                        hash = AppendObjectSignature(hash, clip.asset);
                    }
                }

                return hash;
            }
        }

        private static int AppendObjectSignature(int hash, UnityEngine.Object value)
        {
            unchecked
            {
                hash = hash * 31 + KimodoUnityObjectIdUtility.IdHash(value);
                return hash * 31 + (value != null ? EditorUtility.GetDirtyCount(value) : 0);
            }
        }

        private static int AppendCurveSignature(int hash, AnimationCurve curve)
        {
            unchecked
            {
                if (curve == null)
                {
                    return hash * 31;
                }

                hash = hash * 31 + curve.preWrapMode.GetHashCode();
                hash = hash * 31 + curve.postWrapMode.GetHashCode();
                int keyCount = curve.length;
                hash = hash * 31 + keyCount;
                for (int i = 0; i < keyCount; i++)
                {
                    Keyframe key = curve[i];
                    hash = hash * 31 + key.time.GetHashCode();
                    hash = hash * 31 + key.value.GetHashCode();
                    hash = hash * 31 + key.inTangent.GetHashCode();
                    hash = hash * 31 + key.outTangent.GetHashCode();
                    hash = hash * 31 + key.inWeight.GetHashCode();
                    hash = hash * 31 + key.outWeight.GetHashCode();
                    hash = hash * 31 + key.weightedMode.GetHashCode();
                }
                return hash;
            }
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
            float timelineFrameRate = ResolveTimelineFrameRate(context);
            double exactTimelineTime = Math.Max(0.0, timelineTime);
            double timelineSampleTime = ResolveTimelineSampleTime(exactTimelineTime, timelineFrameRate);
            if (!TryGetOrCreatePoseClip(
                    context,
                    timelineSampleTime,
                    timelineFrameRate,
                    modelName,
                    forceRefresh,
                    out KimodoTimelineConstraintCacheEntry entry,
                    out KimodoTimelineConstraintCacheRange range,
                    out error))
            {
                return false;
            }

            float localSampleTime = range.ResolveLocalSampleTime(exactTimelineTime);
            if (!entry.TrySampleTargetBone(localSampleTime, out BoneSample targetSample, out error))
            {
                return false;
            }

            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    targetSample,
                    entry.TargetCache,
                    modelName,
                    markerType,
                    exportedSampleTime,
                    out sample,
                    out error))
            {
                return false;
            }

            return true;
        }

        internal static void Clear()
        {
            foreach (KimodoTimelineConstraintCacheEntry entry in Entries.Values)
            {
                entry?.Dispose();
            }
            Entries.Clear();
        }

        private static bool TryGetOrCreatePoseClip(
            KimodoTimelineInOutConstraintContext context,
            double timelineTime,
            float frameRate,
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
                ResolveSamplingEndTime(context, timelineTime, frameRate),
                cacheTimeFrames,
                frameRate);
            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation);
            var key = new KimodoTimelineConstraintCacheKey(
                KimodoUnityObjectIdUtility.IdHash(context.Track),
                KimodoUnityObjectIdUtility.IdHash(context.Animator),
                KimodoUnityObjectIdUtility.IdHash(context.SourceAvatar),
                range,
                modelName,
                ComputeSamplingSourceSignature(context.Track),
                trackOffsetPosition,
                trackOffsetRotation,
                context.SourceAvatar != null ? EditorUtility.GetDirtyCount(context.SourceAvatar) : 0);
            if (forceRefresh)
            {
                Invalidate(key);
            }
            RemoveStaleEntries(key);
            if (Entries.TryGetValue(key, out entry) && entry?.Clip != null)
            {
                return true;
            }

            if (!KimodoTimelineSamplingSession.TryCreate(context, modelName, out KimodoTimelineSamplingSession sampler, out error))
            {
                return false;
            }

            AnimationClip clip = null;
            SkeletonCache targetCache = null;
            try
            {
                var timelineTimes = new double[range.BakedFrameCount];
                for (int i = 0; i < range.BakedFrameCount; i++)
                {
                    int sampleFrame = Mathf.Min(range.BakedStartFrame + i, range.BakedEndFrame);
                    timelineTimes[i] = sampleFrame / range.FrameRate;
                }
                Func<AnimationClip, string, string> debugWriteback =
                    KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips
                        ? (clipToWrite, label) => WriteDebugResampledClip(clipToWrite, key, label)
                        : null;
                if (!sampler.TryCaptureTargetBoneSamples(
                        timelineTimes,
                        range.FrameRate,
                        out BoneSample[] targetSamples,
                        out error,
                        debugWriteback))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                        targetSamples,
                        range.FrameRate,
                        out clip,
                        out error))
                {
                    return false;
                }

                if (debugWriteback != null)
                {
                    string writebackError = debugWriteback(clip, "TargetBoneClip");
                    if (!string.IsNullOrWhiteSpace(writebackError))
                    {
                        error = writebackError;
                        return false;
                    }
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
                        "KimodoTimelineConstraintCache_TargetSampler",
                        out targetCache,
                        out error))
                {
                    return false;
                }

                clip.name = "KimodoTimelineConstraintCache";
                entry = new KimodoTimelineConstraintCacheEntry
                {
                    Clip = clip,
                    TargetCache = targetCache
                };
                Entries[key] = entry;
                clip = null;
                targetCache = null;
                return true;
            }
            finally
            {
                sampler.Dispose();
                targetCache?.Dispose();
                if (clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        private static string WriteDebugResampledClip(
            AnimationClip sourceClip,
            KimodoTimelineConstraintCacheKey key,
            string label)
        {
            if (sourceClip == null)
            {
                return "Cannot write a null resampled clip.";
            }

            string assetName = $"TimelineConstraint_{key.TrackId}_{key.AnimatorId}_{key.StartFrame}_{key.EndFrame}_{label}";
            if (!KimodoEditorClipWritebackService.TryWriteGeneratedCacheAnimationClipAsset(
                    sourceClip,
                    assetName,
                    out AnimationClip savedClip,
                    out string error))
            {
                return $"Write resampled {label} clip failed: {error}";
            }

            KimodoPlayableClipGenerationSettings.DebugLog($"[Kimodo][TimelineSample] Wrote resampled {label}: '{AssetDatabase.GetAssetPath(savedClip)}'.");
            return string.Empty;
        }

        internal static bool Invalidate(KimodoTimelineConstraintCacheKey key)
        {
            if (!Entries.TryGetValue(key, out KimodoTimelineConstraintCacheEntry entry))
            {
                return false;
            }

            entry?.Dispose();
            return Entries.Remove(key);
        }

        internal static void RemoveStaleEntries(KimodoTimelineConstraintCacheKey current)
        {
            List<KimodoTimelineConstraintCacheKey> stale = null;
            foreach (KeyValuePair<KimodoTimelineConstraintCacheKey, KimodoTimelineConstraintCacheEntry> pair in Entries)
            {
                KimodoTimelineConstraintCacheKey key = pair.Key;
                if (key.TrackId != current.TrackId)
                {
                    continue;
                }
                if (key.AnimatorId == current.AnimatorId &&
                    key.SourceSignature == current.SourceSignature &&
                    key.AvatarId == current.AvatarId &&
                    key.AvatarDirtyCount == current.AvatarDirtyCount &&
                    key.FrameRate.Equals(current.FrameRate) &&
                    key.TrackOffsetPosition.Equals(current.TrackOffsetPosition) &&
                    key.TrackOffsetRotation.Equals(current.TrackOffsetRotation) &&
                    string.Equals(key.ModelName, current.ModelName, StringComparison.Ordinal))
                {
                    continue;
                }

                pair.Value?.Dispose();
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
            Vector3 forward = planarRotation * Vector3.forward;

            sample.kimodoRootPosition = targetRootPosition;
            sample.unityRootPos = targetRootPosition;
            sample.unityRootRot = planarRotation;
            sample.rootHeading = new Vector2(forward.x, forward.z);
            sample.hasRootHeading = true;
            if (sample.localAxisAngles != null && sample.localAxisAngles.Count > 0)
            {
                sample.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(targetRootRotation);
            }
            sample.sampleTime = exportedSampleTime;
        }

        internal static double ResolveSamplingEndTime(
            KimodoTimelineInOutConstraintContext context,
            double timelineTime,
            float frameRate = KimodoPlayableClip.FIXED_FRAME_RATE)
        {
            double endTime = context?.SourceClip != null ? context.SourceClip.end : 0.0;
            if (context?.Track != null)
            {
                foreach (TimelineClip clip in context.Track.GetClips())
                {
                    if (clip != null)
                    {
                        endTime = Math.Max(endTime, clip.end);
                    }
                }
            }

            PlayableAsset playableAsset = context?.Director != null ? context.Director.playableAsset : null;
            if (playableAsset != null && !double.IsNaN(playableAsset.duration) && !double.IsInfinity(playableAsset.duration))
            {
                endTime = Math.Max(endTime, playableAsset.duration);
            }

            double frameDuration = 1.0 / Mathf.Max(1f, frameRate);
            if (timelineTime >= endTime - 1e-9)
            {
                endTime = Math.Max(endTime, timelineTime + frameDuration);
            }

            return Math.Max(endTime, frameDuration);
        }
    }

    internal sealed class KimodoTimelineSamplingSession : IDisposable
    {
        private readonly KimodoTimelineInOutConstraintContext context;
        private readonly SkeletonCache sourceSamplingCache;
        private readonly Transform[] sourceBoneTransforms;
        private readonly float sourceHumanScale;
        private readonly double originalTime;
        private readonly DirectorWrapMode originalWrapMode;
        private readonly Dictionary<int, MuscleSample> sampledMusclePoses = new Dictionary<int, MuscleSample>();
        private bool disposed;

        private KimodoTimelineSamplingSession(
            KimodoTimelineInOutConstraintContext context,
            SkeletonCache sourceSamplingCache,
            Transform[] sourceBoneTransforms,
            float sourceHumanScale,
            SkeletonCache targetCache,
            double originalTime,
            DirectorWrapMode originalWrapMode)
        {
            this.context = context;
            this.sourceSamplingCache = sourceSamplingCache;
            this.sourceBoneTransforms = sourceBoneTransforms;
            this.sourceHumanScale = sourceHumanScale;
            TargetCache = targetCache;
            this.originalTime = originalTime;
            this.originalWrapMode = originalWrapMode;
        }

        internal SkeletonCache TargetCache { get; }
        internal float SourceHumanScale => sourceHumanScale;

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
                    "KimodoTimelineSamplingSession_Target",
                    out SkeletonCache targetCache,
                    out error))
            {
                return false;
            }

            SkeletonCache sourceSamplingCache = null;
            Transform[] sourceBoneTransforms = null;
            double originalTime = context.Director.time;
            DirectorWrapMode originalWrapMode = context.Director.extrapolationMode;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        context.SourceAvatar,
                        "KimodoTimelineSamplingSession_SourcePose",
                        out sourceSamplingCache,
                        out error))
                {
                    targetCache.Dispose();
                    return false;
                }
                float sourceHumanScale = sourceSamplingCache.humanScale;
                if (!TryBuildSourceBoneTransforms(
                        context.Animator.transform,
                        sourceSamplingCache,
                        out sourceBoneTransforms,
                        out error))
                {
                    sourceSamplingCache.Dispose();
                    targetCache.Dispose();
                    return false;
                }

                context.Director.extrapolationMode = DirectorWrapMode.Hold;
                sampler = new KimodoTimelineSamplingSession(
                    context,
                    sourceSamplingCache,
                    sourceBoneTransforms,
                    sourceHumanScale,
                    targetCache,
                    originalTime,
                    originalWrapMode);
                return true;
            }
            catch (Exception ex)
            {
                RestoreSourceState(context, originalTime, originalWrapMode, logFailures: false);
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

            sample = samples[0];
            NormalizeSampleRoot(sample, normalizeRootToAnchor, anchorRootPosition, anchorRootRotation);
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
                float frameRate = KimodoTimelineConstraintClipCache.ResolveTimelineFrameRate(context);
                int sampleCount = timelineTimes.Count;
                samples = new MuscleSample[sampleCount];
                var missingFrames = new List<int>();
                var missingTimes = new List<double>();
                var pendingFrames = new HashSet<int>();
                for (int i = 0; i < sampleCount; i++)
                {
                    double timelineTime = timelineTimes[i];
                    if (double.IsNaN(timelineTime) || double.IsInfinity(timelineTime))
                    {
                        samples = null;
                        error = $"Timeline sample time {i} is invalid.";
                        return false;
                    }

                    int sampleFrame = KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(timelineTime, frameRate);
                    if (sampledMusclePoses.TryGetValue(sampleFrame, out MuscleSample cached))
                    {
                        samples[i] = KimodoRetargetSamplingUtility.CloneMuscleSample(cached);
                    }
                    else if (pendingFrames.Add(sampleFrame))
                    {
                        missingFrames.Add(sampleFrame);
                        missingTimes.Add(KimodoTimelinePreviewRefreshUtility.TimelineFrameToTime(sampleFrame, frameRate));
                    }
                }

                if (missingTimes.Count == 0)
                {
                    return true;
                }

                int clipSampleCount = Mathf.Max(2, missingTimes.Count);
                var poseSamples = new BoneSample[clipSampleCount];
                for (int i = 0; i < missingTimes.Count; i++)
                {
                    int sampleFrame = missingFrames[i];
                    double timelineTime = missingTimes[i];
                    context.Director.time = Math.Max(0.0, timelineTime);
                    context.Director.Evaluate();
                    if (!TryCaptureSourceBoneSample(
                            context.Animator.transform,
                            sourceSamplingCache,
                            sourceBoneTransforms,
                            out poseSamples[i],
                            out error))
                    {
                        return false;
                    }
                }
                for (int i = missingTimes.Count; i < clipSampleCount; i++)
                {
                    poseSamples[i] = poseSamples[missingTimes.Count - 1];
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

                    for (int i = 0; i < missingTimes.Count; i++)
                    {
                        sampledMusclePoses[missingFrames[i]] = KimodoRetargetSamplingUtility.CloneMuscleSample(decoded[i]);
                    }
                    for (int i = 0; i < sampleCount; i++)
                    {
                        int sampleFrame = KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(timelineTimes[i], frameRate);
                        samples[i] = KimodoRetargetSamplingUtility.CloneMuscleSample(sampledMusclePoses[sampleFrame]);
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

        private void NormalizeSampleRoot(
            MuscleSample sample,
            bool normalizeRootToAnchor,
            Vector3 anchorRootPosition,
            Quaternion anchorRootRotation)
        {
            if (!normalizeRootToAnchor || sample == null)
            {
                return;
            }

            HumanPose pose = sample.pose;
            Vector3 bodyPosition = pose.bodyPosition * sourceHumanScale;
            Quaternion bodyRotation = pose.bodyRotation;
            KimodoConstraintNormalizationUtility.NormalizeRootPose(
                anchorRootPosition,
                anchorRootRotation,
                ref bodyPosition,
                ref bodyRotation);
            pose.bodyPosition = bodyPosition / sourceHumanScale;
            pose.bodyRotation = bodyRotation;
            sample.pose = pose;
        }


        private static bool TryBuildSourceBoneTransforms(
            Transform sourceRoot,
            SkeletonCache sourceSamplingCache,
            out Transform[] sourceBoneTransforms,
            out string error)
        {
            sourceBoneTransforms = null;
            error = string.Empty;
            if (sourceRoot == null || !KimodoRetargetAvatarUtility.ValidateRetargetCache(sourceSamplingCache, out error))
            {
                return false;
            }

            sourceBoneTransforms = KimodoRetargetAvatarUtility.BuildBoneTransforms(
                sourceRoot,
                sourceSamplingCache.bonePaths,
                sourceSamplingCache.canonicalRootBoneName);
            for (int i = 0; i < sourceBoneTransforms.Length; i++)
            {
                if (sourceBoneTransforms[i] != null)
                {
                    continue;
                }

                string path = sourceSamplingCache.bonePaths[i];
                int separator = path.LastIndexOf('/');
                string boneName = separator >= 0 ? path.Substring(separator + 1) : path;
                if (!KimodoRetargetAvatarUtility.TryFindUniqueTransformByName(
                        sourceRoot,
                        boneName,
                        out sourceBoneTransforms[i],
                        out bool ambiguous))
                {
                    error = ambiguous
                        ? $"Source bone '{boneName}' is ambiguous under '{sourceRoot.name}'."
                        : $"Source bone '{path}' is missing under '{sourceRoot.name}'.";
                    sourceBoneTransforms = null;
                    return false;
                }
            }

            return true;
        }

        private static bool TryCaptureSourceBoneSample(
            Transform sourceRoot,
            SkeletonCache sourceSamplingCache,
            Transform[] sourceBoneTransforms,
            out BoneSample sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (sourceRoot == null ||
                sourceSamplingCache?.boneTransforms == null ||
                sourceBoneTransforms == null ||
                sourceBoneTransforms.Length != sourceSamplingCache.boneTransforms.Length)
            {
                error = "Source bone sampling map is invalid.";
                return false;
            }

            int count = sourceBoneTransforms.Length;
            sample = new BoneSample
            {
                boneNames = sourceSamplingCache.bonePaths,
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

                bool isRoot = sourceSamplingCache.boneTransforms[i] == sourceSamplingCache.skeletonRoot;
                sample.localPositions[i] = isRoot ? sourceRoot.position : sourceBone.localPosition;
                sample.localRotations[i] = isRoot ? sourceRoot.rotation : sourceBone.localRotation;
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
            RestoreSourceState(context, originalTime, originalWrapMode, logFailures: true);
            sourceSamplingCache?.Dispose();
            TargetCache.Dispose();
        }

        private static void RestoreSourceState(
            KimodoTimelineInOutConstraintContext context,
            double originalTime,
            DirectorWrapMode originalWrapMode,
            bool logFailures)
        {
            if (context?.Director == null)
            {
                return;
            }

            try
            {
                context.Director.extrapolationMode = originalWrapMode;
                context.Director.time = originalTime;
                context.Director.Evaluate();
            }
            catch (Exception ex)
            {
                if (logFailures)
                {
                    Debug.LogWarning($"[Kimodo][TimelineSample] Failed to restore Director state: {ex.Message}");
                }
            }
        }
    }

    internal static class KimodoTimelineConstraintMarkerSampler
    {
        private const double SeamTimeEpsilon = 1e-9;

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
                return markerTime >= clipRange.start && markerTime < clipRange.end;
            }

            if (ReferenceEquals(FindOwningClip(track, markerTime), clipRange))
            {
                return true;
            }

            if (Math.Abs(markerTime - clipRange.end) > SeamTimeEpsilon)
            {
                return false;
            }

            foreach (TimelineClip nextClip in track.GetClips())
            {
                if (!ReferenceEquals(nextClip, clipRange) &&
                    nextClip?.asset is KimodoPlayableClip &&
                    Math.Abs(nextClip.start - clipRange.end) <= SeamTimeEpsilon)
                {
                    return true;
                }
            }

            return false;
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
                    markerTime < clip.start ||
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

            List<KimodoConstraintMarkerBase> markers = CollectMarkersForClip(context.Track, context.SourceClip);
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

            var resolvedSamples = new KimodoMarkerSampleResult[markers.Count];
            var sampledMarkerIndices = new List<int>();
            for (int i = 0; i < markers.Count; i++)
            {
                KimodoConstraintMarkerBase marker = markers[i];
                if (marker == null)
                {
                    error = "Marker is null.";
                    return false;
                }

                if (CanUseOverrideWithoutTimelineSampling(marker))
                {
                    if (!TryNormalizeMarkerSample(marker, marker.SampleData, "override", out resolvedSamples[i], out error))
                    {
                        return false;
                    }
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
                    float frameRate = KimodoTimelineConstraintClipCache.ResolveTimelineFrameRate(context);
                    var uniqueFrames = new List<int>();
                    var frameToSample = new Dictionary<int, int>();
                    var markerSampleIndices = new int[sampledMarkerIndices.Count];
                    for (int i = 0; i < sampledMarkerIndices.Count; i++)
                    {
                        int frame = KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(
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

                    var timelineTimes = new double[uniqueFrames.Count];
                    for (int i = 0; i < uniqueFrames.Count; i++)
                    {
                        timelineTimes[i] = KimodoTimelinePreviewRefreshUtility.TimelineFrameToTime(
                            uniqueFrames[i],
                            frameRate);
                    }
                    if (!sampler.TryCaptureTargetBoneSamples(
                            timelineTimes,
                            frameRate,
                            out BoneSample[] targetSamples,
                            out error))
                    {
                        return false;
                    }

                    for (int i = 0; i < sampledMarkerIndices.Count; i++)
                    {
                        int markerIndex = sampledMarkerIndices[i];
                        KimodoConstraintMarkerBase marker = markers[markerIndex];
                        if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                                targetSamples[markerSampleIndices[i]],
                                sampler.TargetCache,
                                context.ModelName,
                                marker.ConstraintType,
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
            KimodoConstraintMarkerBase marker,
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

        internal static List<KimodoConstraintMarkerBase> CollectMarkersForClip(
            TrackAsset track,
            TimelineClip clipRange)
        {
            var markers = new List<KimodoConstraintMarkerBase>();
            foreach (IMarker marker in track.GetMarkers())
            {
                if (marker is KimodoConstraintMarkerBase kimodoMarker)
                {
                    if (!kimodoMarker.constraintEnabled)
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
