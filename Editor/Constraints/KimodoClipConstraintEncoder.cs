using System;
using System.Collections.Generic;
using System.Threading;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoClipConstraintEncoder
    {
        internal static byte[] EncodeTimeline(
            TimelineClip timelineClip,
            string modelName,
            int frameCount,
            float frameRate,
            int runtimeTrimStartFrame,
            KimodoInOutConstraintMode inOutMode,
            bool enableInConstraint,
            bool enableOutConstraint,
            CancellationToken token = default,
            bool includeFootContacts = false)
        {
            if (timelineClip == null) throw new ArgumentNullException(nameof(timelineClip));
            if (frameCount <= 0 || frameRate <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount), "ClipConstraint frame range is invalid.");
            }
            if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out string error))
            {
                throw new InvalidOperationException($"ClipConstraint requires Timeline sampling: {error}");
            }
            if (!KimodoTimelineSamplingSession.TryCreate(
                    context,
                    modelName,
                    out KimodoTimelineSamplingSession sampler,
                    out error))
            {
                throw new InvalidOperationException($"ClipConstraint Timeline sampler failed: {error}");
            }

            try
            {
                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        modelName,
                        sampler.TargetCache.skeletonRoot,
                        out string[] jointNames,
                        out int[] jointParents,
                        out Transform[] joints,
                        out error))
                {
                    throw new InvalidOperationException(error);
                }

                double[] timelineTimes = BuildTimelineSampleTimes(
                    context,
                    frameCount,
                    frameRate,
                    runtimeTrimStartFrame,
                    inOutMode,
                    enableInConstraint,
                    enableOutConstraint);
                if (!sampler.TryCaptureTargetBoneSamples(
                        timelineTimes,
                        frameRate,
                        out BoneSample[] samples,
                        out error))
                {
                    throw new InvalidOperationException(error);
                }

                var roots = new Vector3[frameCount];
                var rotations = new List<float>(frameCount * jointNames.Length * 4);
                int[] footJointIndices = includeFootContacts
                    ? ResolveFootContactJointIndices(jointNames)
                    : null;
                var footPositions = includeFootContacts
                    ? new Vector3[frameCount, KimodoFootContactTrackUtility.ChannelCount]
                    : null;
                Transform rootJoint = joints[0];
                if (rootJoint == null)
                {
                    throw new InvalidOperationException("ClipConstraint profile root joint is missing after Timeline retargeting.");
                }
                for (int frame = 0; frame < frameCount; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(
                            samples[frame],
                            sampler.TargetCache,
                            out error))
                    {
                        throw new InvalidOperationException(error);
                    }

                    Quaternion rootRotation = rootJoint.rotation.normalized;
                    roots[frame] = rootJoint.position;
                    if (footPositions != null)
                    {
                        for (int channel = 0; channel < KimodoFootContactTrackUtility.ChannelCount; channel++)
                        {
                            Transform footJoint = joints[footJointIndices[channel]];
                            footPositions[frame, channel] = footJoint.position;
                        }
                    }
                    for (int joint = 0; joint < joints.Length; joint++)
                    {
                        Quaternion value = joint == 0
                            ? rootRotation
                            : joints[joint] != null
                                ? joints[joint].localRotation.normalized
                                : Quaternion.identity;
                        rotations.Add(value.w);
                        rotations.Add(value.x);
                        rotations.Add(-value.y);
                        rotations.Add(-value.z);
                    }
                }

                byte[] footContacts = null;
                if (includeFootContacts)
                {
                    footContacts = TrySampleFootContacts(context, timelineTimes, out byte[] authoredContacts)
                        ? authoredContacts
                        : DetectFootContacts(footPositions, frameRate);
                }
                return KimodoRawMotionUtility.ToFlatBuffer(
                    new KimodoRawMotionData(
                        frameCount,
                        jointNames.Length,
                        frameRate,
                        jointNames,
                        jointParents,
                        roots,
                        rotations,
                        rootJointIndex: 0,
                        footContacts: footContacts),
                    modelName);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        private static int[] ResolveFootContactJointIndices(string[] jointNames)
        {
            return new[]
            {
                FindJointIndex(jointNames, "LeftFoot", "left_ankle_roll_skel", "left_ankle"),
                FindJointIndex(jointNames, "LeftToeBase", "left_toe_base", "left_foot"),
                FindJointIndex(jointNames, "RightFoot", "right_ankle_roll_skel", "right_ankle"),
                FindJointIndex(jointNames, "RightToeBase", "right_toe_base", "right_foot")
            };
        }

        private static int FindJointIndex(string[] jointNames, params string[] candidates)
        {
            for (int candidate = 0; candidate < candidates.Length; candidate++)
            {
                for (int index = 0; index < jointNames.Length; index++)
                {
                    if (string.Equals(jointNames[index], candidates[candidate], StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }
            }
            throw new InvalidOperationException("The selected model profile has no required foot-contact joint.");
        }

        private static bool TrySampleFootContacts(
            KimodoTimelineInOutConstraintContext context,
            IReadOnlyList<double> timelineTimes,
            out byte[] contacts)
        {
            contacts = new byte[timelineTimes.Count * KimodoFootContactTrackUtility.ChannelCount];
            for (int frame = 0; frame < timelineTimes.Count; frame++)
            {
                if (!KimodoTimelineFootContactSampler.TrySample(context, timelineTimes[frame], out byte[] values))
                {
                    contacts = null;
                    return false;
                }
                Array.Copy(values, 0, contacts, frame * KimodoFootContactTrackUtility.ChannelCount, values.Length);
            }
            return true;
        }

        private static byte[] DetectFootContacts(Vector3[,] footPositions, float frameRate)
        {
            const float heightTolerance = 0.1f;
            const float velocityThreshold = 0.15f;
            int frameCount = footPositions.GetLength(0);
            int channelCount = footPositions.GetLength(1);
            var minimumHeights = new float[channelCount];
            for (int channel = 0; channel < channelCount; channel++)
            {
                minimumHeights[channel] = float.PositiveInfinity;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    minimumHeights[channel] = Mathf.Min(minimumHeights[channel], footPositions[frame, channel].y);
                }
            }

            var contacts = new byte[frameCount * channelCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                int previous = frame == 0 ? Mathf.Min(1, frameCount - 1) : frame - 1;
                for (int channel = 0; channel < channelCount; channel++)
                {
                    float velocity = Vector3.Distance(footPositions[frame, channel], footPositions[previous, channel]) * frameRate;
                    contacts[frame * channelCount + channel] =
                        footPositions[frame, channel].y <= minimumHeights[channel] + heightTolerance &&
                        velocity < velocityThreshold
                            ? (byte)1
                            : (byte)0;
                }
            }
            return contacts;
        }

        internal static double[] BuildTimelineSampleTimes(
            KimodoTimelineInOutConstraintContext context,
            int frameCount,
            float frameRate,
            int runtimeTrimStartFrame,
            KimodoInOutConstraintMode inOutMode,
            bool enableInConstraint,
            bool enableOutConstraint)
        {
            if (context?.SourceClip == null) throw new ArgumentNullException(nameof(context));
            if (frameCount <= 0 || frameRate <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            var result = new double[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                result[frame] = context.SourceClip.start + (frame - runtimeTrimStartFrame) / frameRate;
            }

            if (inOutMode == KimodoInOutConstraintMode.Outside)
            {
                var request = new KimodoInOutConstraintRequest
                {
                    Mode = KimodoInOutConstraintMode.Outside,
                    TimelineContext = context
                };
                int firstOutputFrame = Mathf.Clamp(runtimeTrimStartFrame, 0, frameCount - 1);
                if (enableOutConstraint && context.NextTimelineClip != null)
                {
                    result[frameCount - 1] = KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(
                        request,
                        isBegin: false);
                }
                // Apply begin last so the previous Timeline frame (-1) wins when a one-frame
                // generation would otherwise overlap both boundaries.
                if (enableInConstraint && context.PreviousTimelineClip != null)
                {
                    result[firstOutputFrame] = KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(
                        request,
                        isBegin: true);
                }
            }
            return result;
        }

    }
}
