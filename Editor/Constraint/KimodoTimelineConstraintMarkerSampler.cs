using System;
using System.Collections.Generic;
using System.Linq;
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
            MuscleSample currentClip,
            float currentClipScale,
            MuscleSample originalCharacter,
            float originalCharacterScale,
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
            AppendDragMuscleSample(text, "CurrentClip", currentClip, currentClipScale);
            AppendDragMuscleSample(text, "OriginalCharacter", originalCharacter, originalCharacterScale);
            AppendDragMuscleSample(text, "VirtualSkeleton", virtualSkeleton, virtualSkeletonScale);
            AppendDragMuscleSample(text, "TargetCharacter", targetCharacter, targetCharacterScale);
            AppendDragMuscleDiff(text, "CurrentClip->OriginalCharacter", currentClip, currentClipScale, originalCharacter, originalCharacterScale);
            AppendDragMuscleDiff(text, "OriginalCharacter->VirtualSkeleton", originalCharacter, originalCharacterScale, virtualSkeleton, virtualSkeletonScale);
            AppendDragMuscleDiff(text, "VirtualSkeleton->TargetCharacter", virtualSkeleton, virtualSkeletonScale, targetCharacter, targetCharacterScale);
            Debug.Log(text.ToString().TrimEnd());
        }

        internal static void LogMuscleSample(
            string stage,
            double timelineTime,
            MuscleSample sample,
            float humanScale,
            Animator animator,
            Avatar sourceAvatar = null,
            string details = null)
        {
            HumanPose pose = sample != null ? sample.pose : default;
            Transform hips = animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            Transform hand = animator != null ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            Transform foot = animator != null ? animator.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
            Debug.Log(
                $"[Kimodo][ConstraintPoseDiag][{stage}] representation=HumanPoseMuscle " +
                $"timelineTime={timelineTime:R}s humanScale={humanScale:F6} " +
                $"sourceAvatar={DescribeAvatar(sourceAvatar)} animatorAvatar={DescribeAvatar(animator != null ? animator.avatar : null)} " +
                $"bodyPositionRaw={FormatVector(pose.bodyPosition)} bodyPositionMeters={FormatVector(pose.bodyPosition * humanScale)} " +
                $"bodyRotation={FormatQuaternion(pose.bodyRotation)} root={FormatTransform(animator != null ? animator.transform : null)} " +
                $"hips={FormatTransform(hips)} leftHand={FormatTransform(hand)} leftFoot={FormatTransform(foot)} " +
                $"muscles=({BuildMuscleStats(pose.muscles)}){FormatDetails(details)}avgmuscle ={pose.muscles.Sum()/ pose.muscles.Length}");
        }

        internal static void LogMarkerSample(string stage, KimodoMarkerSampleResult sample, string details = null)
        {
            int count = sample?.localAxisAngles != null ? sample.localAxisAngles.Count : 0;
            int nonZero = 0;
            float max = 0f;
            for (int i = 0; i < count; i++)
            {
                float magnitude = sample.localAxisAngles[i].magnitude;
                max = Mathf.Max(max, magnitude);
                if (magnitude > MuscleEpsilon)
                {
                    nonZero++;
                }
            }
            Debug.Log(
                $"[Kimodo][ConstraintPoseDiag][{stage}] representation=KimodoMarkerAxisAngle " +
                $"constraint='{sample?.constraintType ?? string.Empty}' sampleTime={sample?.sampleTime ?? 0d:R}s " +
                $"rig={sample?.rigType} root={FormatVector(sample != null ? sample.kimodoRootPosition : Vector3.zero)} " +
                $"unityRoot={FormatVector(sample != null ? sample.unityRootPos : Vector3.zero)}/{FormatQuaternion(sample != null ? sample.unityRootRot : Quaternion.identity)} " +
                $"jointNames={sample?.jointNames?.Count ?? 0} sampledIndices={sample?.sampledJointIndices?.Count ?? 0} " +
                $"axisAngles={count} nonZeroAxisAngles={nonZero} axisMagnitudeMax={max:F6}{FormatDetails(details)}");
        }

        internal static void LogBoneSample(string stage, BoneSample sample, string details = null)
        {
            int count = sample?.localRotations != null ? sample.localRotations.Length : 0;
            int rotated = 0;
            float maxAngle = 0f;
            for (int i = 0; i < count; i++)
            {
                float angle = Quaternion.Angle(Quaternion.identity, sample.localRotations[i]);
                maxAngle = Mathf.Max(maxAngle, angle);
                if (angle > 0.01f)
                {
                    rotated++;
                }
            }
            Debug.Log(
                $"[Kimodo][ConstraintPoseDiag][{stage}] representation=BoneSample count={count} " +
                $"rotatedFromIdentity={rotated} maxLocalAngleDeg={maxAngle:F4} " +
                $"firstBone='{(sample?.boneNames != null && sample.boneNames.Length > 0 ? sample.boneNames[0] : string.Empty)}' " +
                $"firstPosition={FormatVector(sample?.localPositions != null && sample.localPositions.Length > 0 ? sample.localPositions[0] : Vector3.zero)} " +
                $"firstRotation={FormatQuaternion(sample?.localRotations != null && sample.localRotations.Length > 0 ? sample.localRotations[0] : Quaternion.identity)}{FormatDetails(details)}");
        }

        private static string DescribeAvatar(Avatar avatar)
        {
            return avatar != null ? $"'{avatar.name}'#{avatar.GetInstanceID()} valid={avatar.isValid} human={avatar.isHuman}" : "null";
        }

        private static string FormatTransform(Transform transform)
        {
            return transform != null
                ? $"'{transform.name}' p={FormatVector(transform.position)} r={FormatQuaternion(transform.rotation)}"
                : "null";
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
        internal readonly int StartFrame;
        internal readonly int EndFrame;
        internal readonly float FrameRate;
        internal readonly string ModelName;
        internal readonly int TrackDirtyIndex;
        internal readonly Vector3 TrackOffsetPosition;
        internal readonly Quaternion TrackOffsetRotation;

        internal KimodoTimelineConstraintCacheKey(
            int trackId,
            int animatorId,
            int avatarId,
            KimodoTimelineConstraintCacheRange range,
            string modelName,
            int trackDirtyIndex = 0,
            Vector3 trackOffsetPosition = default,
            Quaternion trackOffsetRotation = default)
        {
            TrackId = trackId;
            AnimatorId = animatorId;
            AvatarId = avatarId;
            StartFrame = range.StartFrame;
            EndFrame = range.EndFrame;
            FrameRate = range.FrameRate;
            ModelName = modelName ?? string.Empty;
            TrackDirtyIndex = trackDirtyIndex;
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
                StartFrame == other.StartFrame &&
                EndFrame == other.EndFrame &&
                FrameRate.Equals(other.FrameRate) &&
                TrackDirtyIndex == other.TrackDirtyIndex &&
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
                hash = hash * 397 ^ StartFrame;
                hash = hash * 397 ^ EndFrame;
                hash = hash * 397 ^ FrameRate.GetHashCode();
                hash = hash * 397 ^ TrackDirtyIndex;
                hash = hash * 397 ^ TrackOffsetPosition.GetHashCode();
                hash = hash * 397 ^ TrackOffsetRotation.GetHashCode();
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
        internal Vector3 RootOffsetPosition;
        internal Quaternion RootOffsetRotation = Quaternion.identity;
        internal Vector3[] TargetRootPositions;
        internal Quaternion[] TargetRootRotations;
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
            double timelineSampleTime = ResolveTimelineSampleTime(timelineTime, timelineFrameRate);
            if (!TryGetOrCreateMuscleClip(
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

            var sampleKey = new KimodoTimelineConstraintSampleKey(timelineSampleTime, markerType);
            if (entry.MarkerSamples.TryGetValue(sampleKey, out KimodoMarkerSampleResult cachedSample))
            {
                sample = cachedSample.Clone();
                sample.sampleTime = exportedSampleTime;
                KimodoConstraintPoseDiagnostics.LogMarkerSample(
                    "MarkerCacheHit",
                    sample,
                    $"timelineSampleTime={timelineSampleTime:R}s exportedSampleTime={exportedSampleTime:R}s");
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
                        range.ResolveLocalSampleTime(timelineSampleTime),
                        applyRootOffset: true,
                        entry.RootOffsetPosition,
                        entry.RootOffsetRotation,
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

                if (!TryResolveCachedTargetRootPose(
                        entry,
                        range,
                        timelineSampleTime,
                        out Vector3 targetRootPosition,
                        out Quaternion targetRootRotation))
                {
                    sample = null;
                    error = "Timeline target root pose cache is missing or invalid.";
                    return false;
                }

                ApplyTargetRootPose(
                    sample,
                    targetRootPosition,
                    targetRootRotation,
                    exportedSampleTime);
                KimodoConstraintPoseDiagnostics.LogBoneSample(
                    "TimelineClipToTargetBone",
                    targetSample,
                    $"timelineSampleTime={timelineSampleTime:R}s localClipTime={range.ResolveLocalSampleTime(timelineSampleTime):R}s marker='{markerType}'");
                KimodoConstraintPoseDiagnostics.LogMarkerSample(
                    "TargetBoneToMarker",
                    sample,
                    $"timelineSampleTime={timelineSampleTime:R}s marker='{markerType}'");
                KimodoMarkerSampleResult cached = sample.Clone();
                cached.sampleTime = timelineSampleTime;
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
                context.Track.GetInstanceID(),
                context.Animator.GetInstanceID(),
                context.SourceAvatar != null ? context.SourceAvatar.GetInstanceID() : 0,
                range,
                modelName,
                KimodoTimelinePreviewRefreshUtility.GetDirtyIndex(context.Track),
                trackOffsetPosition,
                trackOffsetRotation);
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
            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    modelName,
                    sampler.TargetCache,
                    out _,
                    out _,
                    out Transform[] targetJoints,
                    out error) ||
                targetJoints == null ||
                targetJoints.Length == 0 ||
                targetJoints[0] == null)
            {
                sampler.Dispose();
                error = string.IsNullOrWhiteSpace(error)
                    ? "Timeline target profile root joint is missing."
                    : error;
                return false;
            }

            Transform targetRootJoint = targetJoints[0];
            var targetRootPositions = new Vector3[range.BakedFrameCount];
            var targetRootRotations = new Quaternion[range.BakedFrameCount];
            AnimationClip clip = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext targetSamplingContext = null;
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
                            out error,
                            logDiagnostics: sampleFrame == ResolveTimelineSampleFrame(timelineTime, range.FrameRate)))
                    {
                        return false;
                    }

                    samples.Add(muscleSample);
                }

                if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                        samples,
                        range.FrameRate,
                        out clip,
                        out error))
                {
                    return false;
                }
                if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                        clip,
                        sampler.TargetCache,
                        "KimodoTimelineConstraintCache_TargetHumanoid",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        applyRootOffset: true,
                        trackOffsetPosition,
                        trackOffsetRotation,
                        out targetSamplingContext,
                        out error))
                {
                    return false;
                }

                for (int i = 0; i < range.BakedFrameCount; i++)
                {
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                            targetSamplingContext,
                            i / range.FrameRate,
                            out error))
                    {
                        return false;
                    }
                    targetRootPositions[i] = targetRootJoint.position;
                    targetRootRotations[i] = targetRootJoint.rotation.normalized;
                }

                clip.name = "KimodoTimelineConstraintCache";
                entry = new KimodoTimelineConstraintCacheEntry
                {
                    Clip = clip,
                    RootOffsetPosition = trackOffsetPosition,
                    RootOffsetRotation = trackOffsetRotation,
                    TargetRootPositions = targetRootPositions,
                    TargetRootRotations = targetRootRotations
                };
                Entries[key] = entry;
                clip = null;
                return true;
            }
            finally
            {
                KimodoRetargetClipSamplingUtility.DestroyClipSamplingContext(targetSamplingContext);
                sampler.Dispose();
                if (clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
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
                    key.TrackOffsetPosition.Equals(current.TrackOffsetPosition) &&
                    key.TrackOffsetRotation.Equals(current.TrackOffsetRotation) &&
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

        private static bool TryResolveCachedTargetRootPose(
            KimodoTimelineConstraintCacheEntry entry,
            KimodoTimelineConstraintCacheRange range,
            double timelineTime,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            IReadOnlyList<Vector3> positions = entry?.TargetRootPositions;
            IReadOnlyList<Quaternion> rotations = entry?.TargetRootRotations;
            if (positions == null || rotations == null || positions.Count == 0 || positions.Count != rotations.Count)
            {
                return false;
            }

            float frame = Mathf.Clamp(
                range.ResolveLocalSampleTime(timelineTime) * range.FrameRate,
                0f,
                positions.Count - 1);
            int first = Mathf.FloorToInt(frame);
            int second = Mathf.Min(first + 1, positions.Count - 1);
            float blend = frame - first;
            position = Vector3.Lerp(positions[first], positions[second], blend);
            rotation = Quaternion.Slerp(rotations[first], rotations[second], blend).normalized;
            return true;
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
            Vector3 forward = Vector3.ProjectOnPlane(targetRootRotation * Vector3.forward, Vector3.up);
            if (forward.sqrMagnitude <= 1e-8f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            sample.kimodoRootPosition = new Vector3(
                targetRootPosition.x,
                sample.kimodoRootPosition.y,
                targetRootPosition.z);
            sample.unityRootPos = targetRootPosition;
            sample.unityRootRot = Quaternion.LookRotation(forward, Vector3.up);
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

    internal sealed class KimodoTimelinePoseSampler : IDisposable
    {
        private readonly KimodoTimelineInOutConstraintContext context;
        private readonly HumanPoseHandler sourcePoseHandler;
        private readonly float sourceHumanScale;
        private readonly Avatar originalAnimatorAvatar;
        private readonly bool restoreAnimatorAvatar;
        private readonly double originalTime;
        private readonly DirectorWrapMode originalWrapMode;
        private bool disposed;

        private KimodoTimelinePoseSampler(
            KimodoTimelineInOutConstraintContext context,
            HumanPoseHandler sourcePoseHandler,
            float sourceHumanScale,
            SkeletonCache targetCache,
            Vector3 rootOffsetPosition,
            Quaternion rootOffsetRotation,
            bool rootPoseIncludesOffset,
            Avatar originalAnimatorAvatar,
            bool restoreAnimatorAvatar,
            double originalTime,
            DirectorWrapMode originalWrapMode)
        {
            this.context = context;
            this.sourcePoseHandler = sourcePoseHandler;
            this.sourceHumanScale = sourceHumanScale;
            this.originalAnimatorAvatar = originalAnimatorAvatar;
            this.restoreAnimatorAvatar = restoreAnimatorAvatar;
            TargetCache = targetCache;
            RootOffsetPosition = rootOffsetPosition;
            RootOffsetRotation = rootOffsetRotation;
            RootPoseIncludesOffset = rootPoseIncludesOffset;
            this.originalTime = originalTime;
            this.originalWrapMode = originalWrapMode;
        }

        internal SkeletonCache TargetCache { get; }
        internal float SourceHumanScale => sourceHumanScale;
        internal Vector3 RootOffsetPosition { get; }
        internal Quaternion RootOffsetRotation { get; }
        internal bool RootPoseIncludesOffset { get; }

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
            Avatar originalAnimatorAvatar = context.Animator.avatar;
            bool restoreAnimatorAvatar = !ReferenceEquals(originalAnimatorAvatar, context.SourceAvatar);
            double originalTime = context.Director.time;
            DirectorWrapMode originalWrapMode = context.Director.extrapolationMode;
            bool sourceStateTouched = false;
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

                sourceStateTouched = true;
                context.Director.extrapolationMode = DirectorWrapMode.Hold;
                if (restoreAnimatorAvatar)
                {
                    context.Animator.avatar = context.SourceAvatar;
                    context.Animator.Rebind();
                    context.Animator.Update(0f);
                    context.Director.RebuildGraph();
                }
                context.Director.time = originalTime;
                context.Director.Evaluate();
                sourceHandler = new HumanPoseHandler(context.SourceAvatar, context.Animator.transform);
                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    context.Track,
                    context.Animator,
                    out Vector3 rootOffsetPosition,
                    out Quaternion rootOffsetRotation,
                    out bool rootPoseIncludesOffset);
                sampler = new KimodoTimelinePoseSampler(
                    context,
                    sourceHandler,
                    sourceHumanScale,
                    targetCache,
                    rootOffsetPosition,
                    rootOffsetRotation,
                    rootPoseIncludesOffset,
                    originalAnimatorAvatar,
                    restoreAnimatorAvatar,
                    originalTime,
                    originalWrapMode);
                return true;
            }
            catch (Exception ex)
            {
                sourceHandler?.Dispose();
                if (sourceStateTouched ||
                    !ReferenceEquals(context.Animator != null ? context.Animator.avatar : null, originalAnimatorAvatar) ||
                    context.Director.extrapolationMode != originalWrapMode)
                {
                    RestoreSourceState(
                        context,
                        originalAnimatorAvatar,
                        restoreAnimatorAvatar,
                        originalTime,
                        originalWrapMode,
                        logFailures: false);
                }
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
            out string error,
            bool logDiagnostics = true)
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
                Vector3 bodyPosition = pose.bodyPosition * sourceHumanScale;
                Quaternion bodyRotation = pose.bodyRotation;
                sample = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(
                    context.SourceAvatar,
                    sourceHumanScale,
                    pose,
                    bone => ResolveSourceHumanBone(context.Animator, context.SourceAvatar, bone));
                Vector3 sourceBodyPosition = bodyPosition;
                Quaternion sourceBodyRotation = bodyRotation;
                if (RootPoseIncludesOffset)
                {
                    KimodoConstraintNormalizationUtility.NormalizeRootPose(
                        RootOffsetPosition,
                        RootOffsetRotation,
                        ref bodyPosition,
                        ref bodyRotation);
                }
                if (normalizeRootToAnchor)
                {
                    KimodoConstraintNormalizationUtility.NormalizeRootPose(
                        anchorRootPosition,
                        anchorRootRotation,
                        ref bodyPosition,
                        ref bodyRotation);
                }
                pose.bodyPosition = bodyPosition / sourceHumanScale;
                pose.bodyRotation = bodyRotation;
                sample.pose = pose;
                if (logDiagnostics)
                {
                    KimodoConstraintPoseDiagnostics.LogMuscleSample(
                        "TimelineEvaluateToSourceHumanPose",
                        timelineTime,
                        sample,
                        sourceHumanScale,
                        context.Animator,
                        context.SourceAvatar,
                        $"rootPoseIncludesOffset={RootPoseIncludesOffset} normalizeRootToAnchor={normalizeRootToAnchor} " +
                        $"sourceBodyMeters=({sourceBodyPosition.x:F5},{sourceBodyPosition.y:F5},{sourceBodyPosition.z:F5}) " +
                        $"sourceBodyRotation=({sourceBodyRotation.x:F5},{sourceBodyRotation.y:F5},{sourceBodyRotation.z:F5},{sourceBodyRotation.w:F5})");
                }
                return true;
            }
            catch (Exception ex)
            {
                sample = null;
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
                sourcePoseHandler.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][TimelineSample] Failed to dispose source pose handler: {ex.Message}");
            }

            RestoreSourceState(
                context,
                originalAnimatorAvatar,
                restoreAnimatorAvatar,
                originalTime,
                originalWrapMode,
                logFailures: true);
            TargetCache.Dispose();
        }

        private static void RestoreSourceState(
            KimodoTimelineInOutConstraintContext context,
            Avatar originalAnimatorAvatar,
            bool restoreAnimatorAvatar,
            double originalTime,
            DirectorWrapMode originalWrapMode,
            bool logFailures)
        {
            if (restoreAnimatorAvatar && context?.Animator != null)
            {
                try
                {
                    context.Animator.avatar = originalAnimatorAvatar;
                    context.Animator.Rebind();
                    context.Animator.Update(0f);
                }
                catch (Exception ex)
                {
                    if (logFailures)
                    {
                        Debug.LogWarning($"[Kimodo][TimelineSample] Failed to restore Animator Avatar: {ex.Message}");
                    }
                }
            }

            if (context?.Director == null)
            {
                return;
            }

            try
            {
                context.Director.extrapolationMode = originalWrapMode;
                if (restoreAnimatorAvatar)
                {
                    context.Director.RebuildGraph();
                }
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
                    if (!kimodoMarker.constraintEnabled)
                    {
                        continue;
                    }

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
