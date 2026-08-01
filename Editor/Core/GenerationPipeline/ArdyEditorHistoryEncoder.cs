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

                if (!sampler.TryCaptureMuscleSamples(timelineTimes, out MuscleSample[] muscleSamples, out error) ||
                    !KimodoRetargetSamplingUtility.TryRetargetMuscleSamplesToBoneSamples(
                        muscleSamples,
                        profile.SourceFps,
                        sampler.TargetCache,
                        out BoneSample[] targetSamples,
                        out error))
                {
                    return false;
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(
                            targetSamples[frame],
                            sampler.TargetCache,
                            out error))
                    {
                        return false;
                    }

                    // Keep the History in the ARDY skeleton space; Unity world placement is a separate anchor.
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

                Vector3 firstRootBeforeNormalize = rootPositions[0];
                Vector3 lastRootBeforeNormalize = rootPositions[frameCount - 1];
                Quaternion firstRotationBeforeNormalize = rootRotations[0];
                Quaternion lastRotationBeforeNormalize = rootRotations[frameCount - 1];
                NormalizeRootPosesToLast(rootPositions, rootRotations);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int rootRotationIndex = frame * joints.Length * 4;
                    Quaternion rootRotation = rootRotations[frame];
                    rotations[rootRotationIndex + 0] = rootRotation.w;
                    rotations[rootRotationIndex + 1] = rootRotation.x;
                    rotations[rootRotationIndex + 2] = -rootRotation.y;
                    rotations[rootRotationIndex + 3] = -rootRotation.z;
                }

                // Use the exact time passed through the batch sampler so its Timeline-frame cache key is identical.
                double anchorSampleTime = timelineTimes[frameCount - 1];
                if (!sampler.TryGetSourceHipsPose(
                        anchorSampleTime,
                        out source.TimelineWorldAnchorPosition,
                        out source.TimelineWorldAnchorRotation,
                        out error))
                {
                    return false;
                }
                source.HasTimelineWorldAnchor = true;
                Debug.Log(
                    $"[Kimodo][ArdyHistoryNormalize] frames={frameCount} " +
                    $"sampleRange={timelineTimes[0]:F6}->{timelineTimes[frameCount - 1]:F6} " +
                    $"rootBeforeFirst={firstRootBeforeNormalize:F6} rootBeforeLast={lastRootBeforeNormalize:F6} " +
                    $"rootAfterFirst={rootPositions[0]:F6} rootAfterLast={rootPositions[frameCount - 1]:F6} " +
                    $"rootYawBeforeFirst={ResolvePlanarRotation(firstRotationBeforeNormalize).eulerAngles:F6} " +
                    $"rootYawBeforeLast={ResolvePlanarRotation(lastRotationBeforeNormalize).eulerAngles:F6} " +
                    $"sourceHipsWorld={source.TimelineWorldAnchorPosition:F6} " +
                    $"sourceHipsYaw={ResolvePlanarRotation(source.TimelineWorldAnchorRotation).eulerAngles:F6}.");

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

        internal static void NormalizeRootPosesToLast(
            Vector3[] rootPositions,
            Quaternion[] rootRotations)
        {
            if (rootPositions == null ||
                rootRotations == null ||
                rootPositions.Length == 0 ||
                rootPositions.Length != rootRotations.Length)
            {
                throw new ArgumentException("ARDY History root pose arrays are empty or mismatched.");
            }

            int last = rootPositions.Length - 1;
            Vector3 anchorPosition = new Vector3(rootPositions[last].x, 0f, rootPositions[last].z);
            Quaternion anchorRotation = ResolvePlanarRotation(rootRotations[last]);
            Quaternion inverseAnchorRotation = Quaternion.Inverse(anchorRotation);
            for (int frame = 0; frame < rootPositions.Length; frame++)
            {
                Vector3 position = rootPositions[frame];
                Quaternion rotation = rootRotations[frame];
                Vector3 planarPosition = inverseAnchorRotation *
                    (new Vector3(position.x, 0f, position.z) - anchorPosition);
                rootPositions[frame] = new Vector3(planarPosition.x, position.y, planarPosition.z);
                rootRotations[frame] = (inverseAnchorRotation * rotation).normalized;
            }
        }

        private static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

    }
}
