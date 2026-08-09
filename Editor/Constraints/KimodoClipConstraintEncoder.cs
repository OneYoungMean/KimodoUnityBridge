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
            CancellationToken token = default)
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

                return KimodoRawMotionUtility.ToFlatBuffer(
                    new KimodoRawMotionData(
                        frameCount,
                        jointNames.Length,
                        frameRate,
                        jointNames,
                        jointParents,
                        roots,
                        rotations,
                        rootJointIndex: 0),
                    modelName);
            }
            finally
            {
                sampler.Dispose();
            }
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
