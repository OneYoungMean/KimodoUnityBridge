using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Runtime composition for canonical SampleResult channels. Command-layer
    /// CharacterPose is not used by animation sampling or retarget evaluation.
    /// </summary>
    public static class KimodoConstraintSampleComposer
    {
        private enum SampleChannel
        {
            Muscle49, RootTQ, LeftFootTQ, RightFootTQ,
            Root2DPosition, Root2DHeading,
            LeftHandEffector, RightHandEffector, LeftFootEffector, RightFootEffector
        }

        public static List<KimodoMarkerSampleResult> ComposeCanonicalSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples, double frameRate)
        {
            var output = new List<KimodoMarkerSampleResult>();
            if (samples == null || frameRate <= 0.0) return output;

            var normalized = new List<KimodoMarkerSampleResult>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i]?.Clone();
                if (sample == null) continue;
                if (sample.creationOrder == 0) sample.creationOrder = i + 1L;
                sample.enableMask ??= new KimodoSampleChannelMask();
                sample.enableMask.NormalizeDependencies();
                normalized.Add(sample);
            }

            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(normalized, frameRate).Values)
            {
                if (group.Count == 0) continue;
                var ordered = new List<KimodoMarkerSampleResult>(group);
                ordered.Sort((a, b) => a.creationOrder.CompareTo(b.creationOrder));
                var result = new KimodoMarkerSampleResult
                {
                    sampleTime = ordered[ordered.Count - 1].sampleTime,
                    constraintMode = "mix",
                    enabled = true,
                    sampleData = new MuscleSample(),
                    enableMask = new KimodoSampleChannelMask(),
                    effectors = new KimodoConstraintEffectors()
                };

                CopyDataChannel(ordered, result, SampleChannel.Muscle49,
                    KimodoSampleDataLayout.BodyMuscleOffset, KimodoSampleDataLayout.BodyMuscleCount);
                CopyDataChannel(ordered, result, SampleChannel.RootTQ,
                    KimodoSampleDataLayout.RootTqOffset, KimodoSampleDataLayout.RootTqCount);
                CopyDataChannel(ordered, result, SampleChannel.LeftFootTQ,
                    KimodoSampleDataLayout.LeftFootTqOffset, KimodoSampleDataLayout.FootTqCount);
                CopyDataChannel(ordered, result, SampleChannel.RightFootTQ,
                    KimodoSampleDataLayout.RightFootTqOffset, KimodoSampleDataLayout.FootTqCount);

                KimodoMarkerSampleResult rootPosition = FindLatest(ordered, SampleChannel.Root2DPosition);
                if (rootPosition?.root2DOverride != null)
                {
                    result.root2DOverride = rootPosition.root2DOverride.Clone();
                    result.enableMask.root2DPosition = rootPosition.enableMask?.root2DPosition == true;
                }
                KimodoMarkerSampleResult rootHeading = FindLatest(ordered, SampleChannel.Root2DHeading);
                result.enableMask.root2DHeading = result.enableMask.root2DPosition &&
                    rootHeading?.enableMask?.root2DHeading == true;

                CopyEffectorChannel(ordered, result, SampleChannel.LeftHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.LeftFootEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightFootEffector);
                result.enableMask.NormalizeDependencies();
                output.Add(result);
            }
            return output;
        }

        public static KimodoMarkerSampleResult ResolveUnifiedSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null) return null;
            List<KimodoMarkerSampleResult> composed = ComposeCanonicalSamples(new[] { sample }, 30.0);
            return composed.Count == 0 ? sample.Clone() : composed[0];
        }

        public static List<KimodoMarkerSampleResult> ExpandProtocolSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples, double frameRate)
        {
            var output = new List<KimodoMarkerSampleResult>();
            foreach (KimodoMarkerSampleResult canonical in ComposeCanonicalSamples(samples, frameRate))
            {
                if (canonical == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.FromSample(canonical);
                AppendProtocolSample(output, canonical, mask.muscle, "fullbody");
                AppendProtocolSample(output, canonical, mask.rootPosition || mask.rootHeading, "root2d");
                AppendProtocolSample(output, canonical, mask.leftHand, "left-hand");
                AppendProtocolSample(output, canonical, mask.rightHand, "right-hand");
                AppendProtocolSample(output, canonical, mask.leftFoot, "left-foot");
                AppendProtocolSample(output, canonical, mask.rightFoot, "right-foot");
            }
            return output;
        }

        public static List<KimodoMarkerSampleResult> MergeAsUnifiedSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples, double frameRate) =>
            ComposeCanonicalSamples(samples, frameRate);

        private static void AppendProtocolSample(
            List<KimodoMarkerSampleResult> output,
            KimodoMarkerSampleResult source,
            bool enabled,
            string type)
        {
            if (!enabled) return;
            KimodoMarkerSampleResult sample = source.Clone();
            sample.constraintMode = type;
            output.Add(sample);
        }

        private static void CopyDataChannel(
            List<KimodoMarkerSampleResult> ordered,
            KimodoMarkerSampleResult destination,
            SampleChannel channel,
            int offset,
            int count)
        {
            KimodoMarkerSampleResult source = FindLatest(ordered, channel);
            if (source == null) return;
            bool valid = source.enableMask != null && IsValid(source.enableMask, channel) &&
                KimodoSampleDataLayout.IsValid(source.sampleData);
            SetValid(destination.enableMask, channel, valid);
            if (valid) Array.Copy(source.sampleData.data, offset, destination.sampleData.data, offset, count);
        }

        private static void CopyEffectorChannel(
            List<KimodoMarkerSampleResult> ordered,
            KimodoMarkerSampleResult destination,
            SampleChannel channel)
        {
            KimodoMarkerSampleResult source = FindLatest(ordered, channel);
            if (source == null) return;
            bool valid = source.enableMask != null && IsValid(source.enableMask, channel);
            KimodoRigidTransform value = channel switch
            {
                SampleChannel.LeftHandEffector => source.effectors?.leftHand,
                SampleChannel.RightHandEffector => source.effectors?.rightHand,
                SampleChannel.LeftFootEffector => source.effectors?.leftFoot,
                SampleChannel.RightFootEffector => source.effectors?.rightFoot,
                _ => null
            };
            valid &= IsValidTransform(value);
            SetValid(destination.enableMask, channel, valid);
            if (!valid) return;
            KimodoRigidTransform copy = value.Clone();
            switch (channel)
            {
                case SampleChannel.LeftHandEffector: destination.effectors.leftHand = copy; break;
                case SampleChannel.RightHandEffector: destination.effectors.rightHand = copy; break;
                case SampleChannel.LeftFootEffector: destination.effectors.leftFoot = copy; break;
                case SampleChannel.RightFootEffector: destination.effectors.rightFoot = copy; break;
            }
        }

        private static KimodoMarkerSampleResult FindLatest(
            List<KimodoMarkerSampleResult> ordered, SampleChannel channel)
        {
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                KimodoMarkerSampleResult sample = ordered[i];
                if (sample != null && sample.enabled && Participates(sample, channel)) return sample;
            }
            return null;
        }

        private static bool Participates(KimodoMarkerSampleResult sample, SampleChannel channel)
        {
            KimodoConstraintMask mask = KimodoConstraintMask.FromSample(sample);
            string mode = (sample.constraintMode ?? string.Empty)
                .Trim().ToLowerInvariant().Replace('_', '-');
            bool fullBody = mode == "fullbody" || mode == "constraint" || mode == "mix" || mask.muscle;
            bool root2D = mode == "root2d" || mode == "mix" || mask.rootPosition;
            bool effector = mode == "effector" || mode == "ik" || mask.AnyEndEffector;
            return channel switch
            {
                SampleChannel.Muscle49 => fullBody,
                SampleChannel.RootTQ => fullBody,
                SampleChannel.LeftFootTQ => fullBody,
                SampleChannel.RightFootTQ => fullBody,
                SampleChannel.Root2DPosition => root2D,
                SampleChannel.Root2DHeading => root2D,
                SampleChannel.LeftHandEffector => effector && mask.leftHand,
                SampleChannel.RightHandEffector => effector && mask.rightHand,
                SampleChannel.LeftFootEffector => effector && mask.leftFoot,
                SampleChannel.RightFootEffector => effector && mask.rightFoot,
                _ => false
            };
        }

        private static bool IsValid(KimodoSampleChannelMask mask, SampleChannel channel) => channel switch
        {
            SampleChannel.Muscle49 => mask.muscle49,
            SampleChannel.RootTQ => mask.rootTQ,
            SampleChannel.LeftFootTQ => mask.leftFootTQ,
            SampleChannel.RightFootTQ => mask.rightFootTQ,
            SampleChannel.Root2DPosition => mask.root2DPosition,
            SampleChannel.Root2DHeading => mask.root2DHeading,
            SampleChannel.LeftHandEffector => mask.leftHandEffector,
            SampleChannel.RightHandEffector => mask.rightHandEffector,
            SampleChannel.LeftFootEffector => mask.leftFootEffector,
            SampleChannel.RightFootEffector => mask.rightFootEffector,
            _ => false
        };

        private static void SetValid(KimodoSampleChannelMask mask, SampleChannel channel, bool value)
        {
            switch (channel)
            {
                case SampleChannel.Muscle49: mask.muscle49 = value; break;
                case SampleChannel.RootTQ: mask.rootTQ = value; break;
                case SampleChannel.LeftFootTQ: mask.leftFootTQ = value; break;
                case SampleChannel.RightFootTQ: mask.rightFootTQ = value; break;
                case SampleChannel.Root2DPosition: mask.root2DPosition = value; break;
                case SampleChannel.Root2DHeading: mask.root2DHeading = value; break;
                case SampleChannel.LeftHandEffector: mask.leftHandEffector = value; break;
                case SampleChannel.RightHandEffector: mask.rightHandEffector = value; break;
                case SampleChannel.LeftFootEffector: mask.leftFootEffector = value; break;
                case SampleChannel.RightFootEffector: mask.rightFootEffector = value; break;
            }
        }

        private static bool IsValidTransform(KimodoRigidTransform value)
        {
            if (value == null) return false;
            Quaternion q = value.q;
            return IsFinite(value.t.x) && IsFinite(value.t.y) && IsFinite(value.t.z) &&
                IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w) &&
                q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 1e-8f;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static SortedDictionary<int, List<KimodoMarkerSampleResult>> GroupByFrame(
            IReadOnlyList<KimodoMarkerSampleResult> samples, double frameRate)
        {
            var groups = new SortedDictionary<int, List<KimodoMarkerSampleResult>>();
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null) continue;
                int frame = KimodoFrameTimeUtility.SecondsToFrameIndex(sample.sampleTime, frameRate);
                if (!groups.TryGetValue(frame, out List<KimodoMarkerSampleResult> group))
                {
                    group = new List<KimodoMarkerSampleResult>();
                    groups.Add(frame, group);
                }
                group.Add(sample);
            }
            return groups;
        }
    }
}
