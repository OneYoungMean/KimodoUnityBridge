using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class ArdyEditorHistoryEncoder
    {
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
            if (!KimodoTimelineSamplingSession.TryCreate(
                    source.TimelineContext,
                    profile.ModelName,
                    out KimodoTimelineSamplingSession sampler,
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
                    KimodoFrameTimeUtility.SecondsToFrameCount(timelineDuration, profile.SourceFps));
                int frameCount = Math.Min(maxFrames, requestedFrames);
                frameCount -= frameCount % profile.FramesPerToken;
                if (frameCount <= 0)
                {
                    error = "ARDY history source is shorter than one model token.";
                    return false;
                }

                int availableFrames = Math.Min(
                    frameCount,
                    Math.Max(
                        1,
                        KimodoFrameTimeUtility.SecondsToFrameCount(timelineDuration, profile.SourceFps)));
                double latestSampleTime = ResolveLatestHistorySampleTime(source);
                double timelineStart = Math.Max(
                    source.RangeStartSeconds,
                    latestSampleTime - (availableFrames - 1) / profile.SourceFps);
                var rootPositions = new Vector3[frameCount];
                var rootRotations = new Quaternion[frameCount];
                var rotations = new List<float>(frameCount * jointNames.Length * 4);
                var footContacts = new byte[frameCount * KimodoFootContactTrackUtility.ChannelCount];
                var timelineTimes = new double[frameCount];
                Transform rootJoint = joints[0];
                if (rootJoint == null)
                {
                    error = "ARDY profile root joint is missing after Timeline retargeting.";
                    return false;
                }
                bool hasFootContacts = true;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    // ponytail: pad a sub-token Timeline history with its latest sampled pose.
                    double sampleTime = frame < availableFrames
                        ? timelineStart + frame / profile.SourceFps
                        : latestSampleTime;
                    timelineTimes[frame] = sampleTime;
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

                if (!sampler.TryCaptureTargetBoneSamples(
                        timelineTimes,
                        profile.SourceFps,
                        out BoneSample[] targetSamples,
                        out error))
                {
                    return false;
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                            targetSamples[frame],
                            sampler.TargetCache,
                            out error))
                    {
                        return false;
                    }

                    Quaternion rootRotation = rootJoint.rotation.normalized;
                    rootRotations[frame] = rootRotation;
                    rootPositions[frame] = rootJoint.position;
                    for (int joint = 0; joint < joints.Length; joint++)
                    {
                        Quaternion unity = joint == 0
                            ? rootRotation
                            : joints[joint] != null
                                ? joints[joint].localRotation.normalized
                                : Quaternion.identity;
                        rotations.Add(unity.w);
                        rotations.Add(unity.x);
                        rotations.Add(-unity.y);
                        rotations.Add(-unity.z);
                    }
                }

                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][ArdyHistory] frames={frameCount} " +
                    $"sampleRange={timelineTimes[0]:F6}->{timelineTimes[frameCount - 1]:F6} " +
                    $"rootFirst={rootPositions[0]:F6} rootLast={rootPositions[frameCount - 1]:F6}.");

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

        internal static double ResolveLatestHistorySampleTime(ArdyEditorHistorySource source)
        {
            var request = new KimodoInOutConstraintRequest
            {
                Mode = KimodoInOutConstraintMode.Outside,
                TimelineContext = source.TimelineContext
            };
            return Math.Max(
                source.RangeStartSeconds,
                KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(request, isBegin: true));
        }

    }
}
