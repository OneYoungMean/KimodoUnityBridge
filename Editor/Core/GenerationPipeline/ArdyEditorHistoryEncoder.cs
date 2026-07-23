using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class ArdyEditorHistoryEncoder
    {
        private const double TimelineBoundaryEpsilonSeconds = 1e-6;

        internal static bool TryEncode(
            ArdyEditorHistorySource source,
            KimodoMotionModelProfile profile,
            out byte[] payload,
            out string error)
        {
            payload = null;
            error = string.Empty;
            if (source?.TimelineContext == null || source.RangeEndSeconds <= source.RangeStartSeconds)
            {
                error = "ARDY Timeline history range is missing or empty.";
                return false;
            }
            if (!KimodoTimelinePoseSampler.TryCreate(
                    source.TimelineContext,
                    profile.ModelName,
                    out KimodoTimelinePoseSampler sampler,
                    out error))
            {
                return false;
            }
            try
            {
                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        profile.ModelName,
                        sampler.TargetCache.skeletonRoot,
                        out string[] jointNames,
                        out int[] jointParents,
                        out Transform[] joints,
                        out error))
                {
                    return false;
                }

                int maxFrames = Math.Max(
                    profile.FramesPerToken,
                    profile.MaxContextFrames - profile.HorizonFrames);
                maxFrames -= maxFrames % profile.FramesPerToken;
                double timelineDuration = source.RangeEndSeconds - source.RangeStartSeconds;
                int requestedFrames = Math.Max(
                    profile.FramesPerToken,
                    (int)Math.Floor(timelineDuration * profile.SourceFps + 1e-9));
                int frameCount = Math.Min(maxFrames, requestedFrames);
                frameCount -= frameCount % profile.FramesPerToken;
                if (frameCount <= 0)
                {
                    error = "ARDY history source is shorter than one model token.";
                    return false;
                }

                int availableFrames = Math.Min(
                    frameCount,
                    Math.Max(1, (int)Math.Floor(timelineDuration * profile.SourceFps + 1e-9)));
                double latestSampleTime = Math.Max(
                    source.RangeStartSeconds,
                    source.RangeEndSeconds - TimelineBoundaryEpsilonSeconds);
                double timelineStart = Math.Max(
                    source.RangeStartSeconds,
                    latestSampleTime - (availableFrames - 1) / profile.SourceFps);
                var rootPositions = new Vector3[frameCount];
                var rotations = new List<float>(frameCount * jointNames.Length * 4);
                var footContacts = new byte[frameCount * KimodoFootContactTrackUtility.ChannelCount];
                bool hasFootContacts = true;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    // ponytail: pad a sub-token Timeline history with its latest sampled pose.
                    double sampleTime = frame < availableFrames
                        ? timelineStart + frame / profile.SourceFps
                        : latestSampleTime;
                    if (!sampler.TryEvaluate(sampleTime, out error))
                    {
                        return false;
                    }

                    if (!sampler.TryGetSourceHipsPose(
                            out Vector3 rootPosition,
                            out Quaternion rootRotation,
                            out error))
                    {
                        return false;
                    }
                    NormalizeRootPose(source, ref rootPosition, ref rootRotation);
                    rootPositions[frame] = rootPosition;
                    for (int joint = 0; joint < joints.Length; joint++)
                    {
                        Quaternion unity = joint == 0
                            ? rootRotation.normalized
                            : joints[joint] != null
                                ? joints[joint].localRotation.normalized
                                : Quaternion.identity;
                        rotations.Add(unity.w);
                        rotations.Add(unity.x);
                        rotations.Add(-unity.y);
                        rotations.Add(-unity.z);
                    }

                    if (hasFootContacts &&
                        KimodoTimelineFootContactSampler.TrySample(
                            source.TimelineContext,
                            sampleTime,
                            out byte[] sampledContacts))
                    {
                        Array.Copy(
                            sampledContacts,
                            0,
                            footContacts,
                            frame * KimodoFootContactTrackUtility.ChannelCount,
                            KimodoFootContactTrackUtility.ChannelCount);
                    }
                    else
                    {
                        hasFootContacts = false;
                    }
                }

                var motion = new KimodoRawMotionData(
                    frameCount,
                    jointNames.Length,
                    profile.SourceFps,
                    jointNames,
                    jointParents,
                    rootPositions,
                    rotations,
                    rootJointIndex: 0,
                    footContacts: hasFootContacts ? footContacts : null);
                payload = KimodoRawMotionUtility.ToFlatBuffer(motion, profile.ModelName);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                sampler.Dispose();
            }
        }

        internal static void NormalizeRootPose(
            ArdyEditorHistorySource source,
            ref Vector3 rootPosition,
            ref Quaternion rootRotation)
        {
            if (source == null || !source.NormalizeRootToAnchor)
            {
                return;
            }

            Quaternion inverseAnchor = Quaternion.Inverse(source.AnchorRootRotation);
            rootPosition = inverseAnchor * (rootPosition - source.AnchorRootPosition);
            rootRotation = inverseAnchor * rootRotation;
        }
    }
}
