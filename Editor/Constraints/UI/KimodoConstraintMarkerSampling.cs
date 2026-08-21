using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
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
        private static readonly Dictionary<int, AutoSampleCacheEntry> AutoSampleCache = new Dictionary<int, AutoSampleCacheEntry>();

        private struct MarkerSamplingContext
        {
            public TrackAsset Track;
            public Animator Animator;
            public Avatar SourceAvatar;
            public string ModelName;
            public int CacheTimeFrames;
        }

private sealed class AutoSampleCacheEntry
        {
            public AutoSampleSignatureSnapshot Snapshot;
            public bool Success;
            public string Error;
        }

private struct AutoSampleSignatureSnapshot
        {
            public string ConstraintType;
            public double GlobalTime;
            public string ModelName;
            public int TrackId;
            public int AnimatorId;
            public int SourceAvatarId;
            public int SourceAvatarDirtyCount;
            public int SourceSignature;
            public int CacheTimeFrames;
            public Vector3 TrackOffsetPosition;
            public Quaternion TrackOffsetRotation;
            public bool HasRootHeading;
        }

public static bool TryUpdateAutoSampleMarkerData(KimodoConstraintMarker marker, bool forceRefresh, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!marker.constraintEnabled)
            {
                KimodoConstraintMarkerEditorUtility.ClearMarkerPoseCachePreview(marker, keepIfOverrideWindowOpen: false);
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
            KimodoLocalAvatarUtility.AvatarResolveResult sourceAvatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, animator);
            Avatar sourceAvatar = sourceAvatarResult.Avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = $"Resolve source avatar failed: {sourceAvatarResult.Error}";
                return false;
            }

            MarkerSamplingContext context = new MarkerSamplingContext
            {
                Track = track,
                Animator = animator,
                SourceAvatar = sourceAvatar,
                ModelName = ResolveModelName(referenceClip),
                CacheTimeFrames = KimodoPlayableClipGenerationSettings.instance.TimelineConstraintCacheTimeFrames
            };

            int id = KimodoUnityObjectIdUtility.IdHash(marker);
            if (!forceRefresh &&
                AutoSampleCache.TryGetValue(id, out AutoSampleCacheEntry cached) &&
                AutoSampleSnapshotMatches(marker, context, cached.Snapshot))
            {
                error = cached.Error ?? string.Empty;
                return cached.Success;
            }

            double sampleTime = marker.time;
            var timelineContext = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = null,
                Track = track,
                Director = director,
                Animator = animator,
                SourceAvatar = sourceAvatar,
                ModelName = context.ModelName
            };
            string samplingType = "fullbody";
            if (!KimodoTimelineConstraintClipCache.TrySampleMarker(
                    timelineContext,
                    sampleTime,
                    sampleTime,
                    samplingType,
                    context.ModelName,
                    forceRefresh,
                    out KimodoMarkerSampleResult sample,
                    out error))
            {
                AutoSampleCache[id] = new AutoSampleCacheEntry
                {
                    Snapshot = BuildAutoSampleSnapshot(marker, context, marker.SampleData),
                    Success = false,
                    Error = error ?? string.Empty
                };
                return false;
            }

            float timelineFrameRate = KimodoTimelineConstraintClipCache.ResolveTimelineFrameRate(timelineContext);
            int timelineFrame = KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(
                sampleTime,
                timelineFrameRate);
            double timelineSampleTime = KimodoTimelineConstraintClipCache.ResolveTimelineSampleTime(
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
                sample.mask = KimodoConstraintMask.Resolve(marker.SampleData.mask, "constraint").Clone();
            }
            KimodoMarkerSampleResult preview = MergeAutoSampledChannels(marker, sample);
            if (preview == null)
            {
                error = "failed to build marker sample";
                AutoSampleCache[id] = new AutoSampleCacheEntry
                {
                    Snapshot = BuildAutoSampleSnapshot(marker, context, marker.SampleData),
                    Success = false,
                    Error = error
                };
                return false;
            }

            if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                    marker, preview, out error))
            {
                AutoSampleCache[id] = new AutoSampleCacheEntry
                {
                    Snapshot = BuildAutoSampleSnapshot(marker, context, marker.SampleData),
                    Success = false,
                    Error = error ?? string.Empty
                };
                return false;
            }

            AutoSampleCache[id] = new AutoSampleCacheEntry
            {
                Snapshot = BuildAutoSampleSnapshot(marker, context, preview),
                Success = true,
                Error = string.Empty
            };
            return true;
        }

private static KimodoMarkerSampleResult MergeAutoSampledChannels(
            KimodoConstraintMarker marker,
            KimodoMarkerSampleResult sampled)
        {
            KimodoMarkerSampleResult result = marker?.SampleData?.Clone() ?? new KimodoMarkerSampleResult();
            if (sampled?.characterPose == null)
            {
                return result;
            }

            result.sampleTime = marker.time;
            result.constraintType = "constraint";
            result.mask = KimodoConstraintMask.Resolve(marker.SampleData.mask, "constraint").Clone();
            result.characterPose ??= sampled.characterPose.Clone();
            result.constraintMode = marker.ConstraintMode == KimodoConstraintMode.Root2D
                ? "root2d"
                : marker.ConstraintMode == KimodoConstraintMode.Effector ? "effector" : "fullbody";
            if (marker.autoSample)
            {
                result.characterPose.hands ??= new CharacterPoseSides();
                result.characterPose.feet ??= new CharacterPoseSides();
                result.characterPose.muscles = sampled.characterPose.muscles != null
                    ? (float[])sampled.characterPose.muscles.Clone()
                    : result.characterPose.muscles;
                if (sampled.characterPose.root != null)
                {
                    result.characterPose.root = new CharacterPoseTransform
                    {
                        t = sampled.characterPose.root.t,
                        q = sampled.characterPose.root.q
                    };
                }
                if (marker.ConstraintMode == KimodoConstraintMode.Root2D)
                {
                    result.hasRootHeading = marker.Root2DData.allowHeading;
                    result.hasRoot2DOverride = true;
                    result.root2DOverride = result.characterPose.root;
                }
                else
                {
                    CopyAutoSampleTargets(
                        result,
                        sampled,
                        overwriteAll: marker.ConstraintMode == KimodoConstraintMode.FullBody);
                }
            }

            return result;
        }

        private static void CopyAutoSampleTargets(
            KimodoMarkerSampleResult destination,
            KimodoMarkerSampleResult sampled,
            bool overwriteAll)
        {
            if (destination?.characterPose == null || sampled?.characterPose == null) return;
            if ((overwriteAll || !destination.mask.leftHand) && sampled.characterPose.hands?.left != null)
                destination.characterPose.hands.left = CloneTransform(sampled.characterPose.hands.left);
            if ((overwriteAll || !destination.mask.rightHand) && sampled.characterPose.hands?.right != null)
                destination.characterPose.hands.right = CloneTransform(sampled.characterPose.hands.right);
            if ((overwriteAll || !destination.mask.leftFoot) && sampled.characterPose.feet?.left != null)
                destination.characterPose.feet.left = CloneTransform(sampled.characterPose.feet.left);
            if ((overwriteAll || !destination.mask.rightFoot) && sampled.characterPose.feet?.right != null)
                destination.characterPose.feet.right = CloneTransform(sampled.characterPose.feet.right);
        }

        private static CharacterPoseTransform CloneTransform(CharacterPoseTransform value)
        {
            return value != null
                ? new CharacterPoseTransform { t = value.t, q = value.q }
                : new CharacterPoseTransform();
        }

internal static bool TryRefreshMarkerCache(KimodoConstraintMarker marker, out string error)
        {
            error = string.Empty;
            if (!TryUpdateAutoSampleMarkerData(marker, forceRefresh: true, out error))
            {
                return false;
            }

            KimodoConstraintSelectionPreviewTool.ScheduleRefresh();
            SceneView.RepaintAll();
            return true;
        }

        internal static void ClearCaches()
        {
            AutoSampleCache.Clear();
        }

        internal static void ClearMarkerCache(KimodoConstraintMarker marker)
        {
            if (marker != null) AutoSampleCache.Remove(KimodoUnityObjectIdUtility.IdHash(marker));
        }

private static AutoSampleSignatureSnapshot BuildAutoSampleSnapshot(
            KimodoConstraintMarker marker,
            MarkerSamplingContext context,
            KimodoMarkerSampleResult sample = null)
        {
            KimodoMarkerSampleResult source = sample ?? marker?.SampleData;
            double globalTime = marker != null ? Math.Max(0.0, marker.time) : 0.0;
            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation);
            return new AutoSampleSignatureSnapshot
            {
                ConstraintType = marker != null ? marker.ConstraintType ?? string.Empty : string.Empty,
                GlobalTime = globalTime,
                ModelName = context.ModelName ?? string.Empty,
                TrackId = KimodoUnityObjectIdUtility.IdHash(context.Track),
                AnimatorId = KimodoUnityObjectIdUtility.IdHash(context.Animator),
                SourceAvatarId = KimodoUnityObjectIdUtility.IdHash(context.SourceAvatar),
                SourceAvatarDirtyCount = context.SourceAvatar != null ? EditorUtility.GetDirtyCount(context.SourceAvatar) : 0,
                SourceSignature = KimodoTimelineConstraintClipCache.ComputeSamplingSourceSignature(context.Track),
                CacheTimeFrames = context.CacheTimeFrames,
                TrackOffsetPosition = trackOffsetPosition,
                TrackOffsetRotation = trackOffsetRotation,
                HasRootHeading = source != null && source.hasRootHeading
            };
        }

private static bool AutoSampleSnapshotMatches(
            KimodoConstraintMarker marker,
            MarkerSamplingContext context,
            AutoSampleSignatureSnapshot snapshot)
        {
            KimodoMarkerSampleResult sample = marker != null ? marker.SampleData : null;
            double globalTime = marker != null ? Math.Max(0.0, marker.time) : 0.0;
            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 trackOffsetPosition,
                out Quaternion trackOffsetRotation);
            return string.Equals(snapshot.ConstraintType ?? string.Empty, marker != null ? marker.ConstraintType ?? string.Empty : string.Empty, StringComparison.Ordinal) &&
                Math.Abs(snapshot.GlobalTime - globalTime) <= 1e-9 &&
                string.Equals(snapshot.ModelName ?? string.Empty, context.ModelName ?? string.Empty, StringComparison.Ordinal) &&
                snapshot.TrackId == KimodoUnityObjectIdUtility.IdHash(context.Track) &&
                snapshot.AnimatorId == KimodoUnityObjectIdUtility.IdHash(context.Animator) &&
                snapshot.SourceAvatarId == KimodoUnityObjectIdUtility.IdHash(context.SourceAvatar) &&
                snapshot.SourceAvatarDirtyCount == (context.SourceAvatar != null ? EditorUtility.GetDirtyCount(context.SourceAvatar) : 0) &&
                snapshot.SourceSignature == KimodoTimelineConstraintClipCache.ComputeSamplingSourceSignature(context.Track) &&
                snapshot.CacheTimeFrames == context.CacheTimeFrames &&
                Vector3Approximately(snapshot.TrackOffsetPosition, trackOffsetPosition) &&
                QuaternionApproximately(snapshot.TrackOffsetRotation, trackOffsetRotation) &&
                snapshot.HasRootHeading == (sample != null && sample.hasRootHeading);
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

        private static bool Vector3Approximately(Vector3 left, Vector3 right)
        {
            return (left - right).sqrMagnitude <= 1e-10f;
        }

        private static bool QuaternionApproximately(Quaternion left, Quaternion right)
        {
            return Mathf.Abs(Quaternion.Dot(left, right)) >= 1f - 1e-10f;
        }
    }
}
