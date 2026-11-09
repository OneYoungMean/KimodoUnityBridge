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
            TimelineClip timelineClip, string modelName, int frameCount, float frameRate,
            int runtimeTrimStartFrame, CancellationToken token = default, bool includeFootContacts = false)
        {
            if (timelineClip == null) throw new ArgumentNullException(nameof(timelineClip));
            var times = new double[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
                times[frame] = timelineClip.start + (frame - runtimeTrimStartFrame) / (double)frameRate;
            return EncodeTimelineAtTimes(timelineClip, modelName, times, frameRate, token, includeFootContacts);
        }

        internal static byte[] EncodeTimelineAtTimes(
            TimelineClip timelineClip, string modelName, IReadOnlyList<double> timelineTimes,
            float frameRate, CancellationToken token = default, bool includeFootContacts = false)
        {
            if (timelineClip == null) throw new ArgumentNullException(nameof(timelineClip));
            if (timelineTimes == null || timelineTimes.Count == 0 || frameRate <= 0f)
                throw new ArgumentOutOfRangeException(nameof(timelineTimes), "ClipConstraint samples are invalid.");
            if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip, out KimodoTimelineInOutConstraintContext context, out string error))
                throw new InvalidOperationException($"ClipConstraint requires Timeline sampling: {error}");
            KimodoTimelineTrackOffsetUtility.CaptureWorldOffset(
                context.Track, context.Animator, out Vector3 position, out Quaternion rotation, out _);
            if (!KimodoTimelineSamplingSession.TryCreateForProfileEncoding(
                    context, modelName, out KimodoTimelineSamplingSession sampler, out error))
                throw new InvalidOperationException($"ClipConstraint Timeline sampler failed: {error}");
            using (sampler)
            {
                if (!sampler.TryCaptureTargetBoneSamples(timelineTimes, frameRate,
                        out BoneSample[] samples, out error, trackPosition: position, trackRotation: rotation))
                    throw new InvalidOperationException(error);
                byte[] contacts = includeFootContacts && TrySampleFootContacts(context, timelineTimes, out byte[] authored)
                    ? authored : null;
                return EncodeSamples(modelName, sampler.TargetCache, samples, frameRate, token, includeFootContacts, contacts);
            }
        }

        internal static byte[] EncodeOriginalAnimation(
            TimelineClip source, string modelName, int frameCount, float frameRate, CancellationToken token)
        {
            AnimationClip animation = (source?.asset as AnimationPlayableAsset)?.clip;
            if (animation == null)
                throw new InvalidOperationException("Inside requires an original AnimationClip.");
            if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    source, out KimodoTimelineInOutConstraintContext context, out string error))
                throw new InvalidOperationException(error);
            Avatar sourceAvatar = KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(context.Track, context.Animator).Avatar;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar targetAvatar, out error))
                throw new InvalidOperationException(error);
            if (!KimodoRetargetToolsEditor.TryGetOrCreateEditorBoneClip(
                    animation, sourceAvatar, targetAvatar, out AnimationClip retargeted, out _, out error))
                throw new InvalidOperationException(error);
            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    targetAvatar, "KimodoInsideReference", out RetargetSkeleton cache, out error))
                throw new InvalidOperationException(error);
            using (cache)
            {
                if (!KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                        retargeted, cache, "KimodoInsideSampler",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.RawTransform, out var sampler, out error))
                    throw new InvalidOperationException(error);
                using (sampler)
                {
                    var samples = new BoneSample[frameCount];
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!KimodoRetargetSamplingUtility.TrySampleBoneClipSession(
                                sampler, frame / (double)frameRate, out samples[frame], out error))
                            throw new InvalidOperationException(error);
                    }
                    return EncodeSamples(modelName, cache, samples, frameRate, token, true);
                }
            }
        }

        private static byte[] EncodeSamples(
            string modelName, RetargetSkeleton cache, BoneSample[] samples, float frameRate,
            CancellationToken token, bool includeFootContacts, byte[] authoredContacts = null)
        {
            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    modelName, cache, out string[] names, out int[] parents, out Transform[] joints, out string error))
                throw new InvalidOperationException(error);
            int frameCount = samples.Length;
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * names.Length * 4);
            int[] footIndices = includeFootContacts ? ResolveFootContactJointIndices(names) : null;
            var footPositions = footIndices != null
                ? new Vector3[frameCount, KimodoFootContactTrackUtility.ChannelCount] : null;
            for (int frame = 0; frame < frameCount; frame++)
            {
                token.ThrowIfCancellationRequested();
                if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(samples[frame], cache, out error))
                    throw new InvalidOperationException(error);
                roots[frame] = joints[0].position;
                if (footPositions != null)
                    for (int channel = 0; channel < KimodoFootContactTrackUtility.ChannelCount; channel++)
                        footPositions[frame, channel] = joints[footIndices[channel]].position;
                for (int joint = 0; joint < joints.Length; joint++)
                {
                    Quaternion value = joint == 0 ? joints[0].rotation.normalized :
                        joints[joint] != null ? joints[joint].localRotation.normalized : Quaternion.identity;
                    rotations.Add(value.w);
                    rotations.Add(value.x);
                    rotations.Add(-value.y);
                    rotations.Add(-value.z);
                }
            }
            byte[] contacts = authoredContacts ?? (footPositions == null ? null : DetectFootContacts(footPositions, frameRate));
            return KimodoRawMotionUtility.ToFlatBuffer(
                new KimodoRawMotionData(frameCount, names.Length, frameRate, names, parents,
                    roots, rotations, rootJointIndex: 0, footContacts: contacts), modelName);
        }

        private static int[] ResolveFootContactJointIndices(string[] jointNames)
        {
            int[] indices = new[]
            {
                FindJointIndex(jointNames, "LeftFoot", "left_ankle_roll_skel", "left_ankle"),
                FindJointIndex(jointNames, "LeftToeBase", "left_toe_base", "left_foot"),
                FindJointIndex(jointNames, "RightFoot", "right_ankle_roll_skel", "right_ankle"),
                FindJointIndex(jointNames, "RightToeBase", "right_toe_base", "right_foot")
            };
            return Array.IndexOf(indices, -1) >= 0 ? null : indices;
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
            return -1;
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

    }
}
