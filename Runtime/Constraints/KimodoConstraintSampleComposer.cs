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
                    validMask = new KimodoConstraintMask(),
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
                if (rootPosition?.rootOverride != null)
                {
                    result.rootOverride = rootPosition.rootOverride.Clone();
                    result.enableMask.root2DPosition = rootPosition.enableMask?.root2DPosition == true;
                    result.validMask.rootPosition = KimodoConstraintMask.FromSample(rootPosition).rootPosition;
                }
                KimodoMarkerSampleResult rootHeading = FindLatest(ordered, SampleChannel.Root2DHeading);
                result.enableMask.root2DHeading = result.enableMask.root2DPosition &&
                    rootHeading?.enableMask?.root2DHeading == true;
                result.validMask.rootHeading = result.enableMask.root2DHeading &&
                    KimodoConstraintMask.FromSample(rootHeading).rootHeading;

                CopyEffectorChannel(ordered, result, SampleChannel.LeftHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.LeftFootEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightFootEffector);
                result.enableMask.NormalizeDependencies();
                result.constraintMode = ResolveComposedMode(ordered, result);
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
                KimodoConstraintMask valid = KimodoConstraintMask.FromSample(canonical);
                KimodoSampleChannelMask enabled = canonical.enableMask;
                string mode = NormalizeMode(canonical.constraintMode);
                if (mode == "mix")
                {
                    AppendProtocolSample(output, canonical,
                        enabled?.muscle49 == true && valid.muscle, "fullbody");
                    AppendProtocolSample(output, canonical,
                        enabled?.root2DPosition == true && valid.rootPosition ||
                        enabled?.root2DHeading == true && valid.rootHeading,
                        "root2d");
                    AppendProtocolSample(output, canonical,
                        enabled?.leftHandEffector == true && valid.leftHand, "left-hand");
                    AppendProtocolSample(output, canonical,
                        enabled?.rightHandEffector == true && valid.rightHand, "right-hand");
                    AppendProtocolSample(output, canonical,
                        enabled?.leftFootEffector == true && valid.leftFoot, "left-foot");
                    AppendProtocolSample(output, canonical,
                        enabled?.rightFootEffector == true && valid.rightFoot, "right-foot");
                }
                else if (mode == "fullbody" || mode == "constraint")
                {
                    AppendProtocolSample(output, canonical,
                        enabled?.muscle49 == true && valid.muscle, "fullbody");
                }
                else if (mode == "root2d")
                {
                    AppendProtocolSample(output, canonical,
                        enabled?.root2DPosition == true && valid.rootPosition ||
                        enabled?.root2DHeading == true && valid.rootHeading,
                        "root2d");
                }
                else if (mode == "left-hand" || mode == "right-hand" ||
                         mode == "left-foot" || mode == "right-foot")
                {
                    AppendProtocolSample(output, canonical,
                        IsEffectorEnabled(enabled, valid, mode), mode);
                }
                else
                {
                    AppendProtocolSample(output, canonical,
                        enabled?.leftHandEffector == true && valid.leftHand, "left-hand");
                    AppendProtocolSample(output, canonical,
                        enabled?.rightHandEffector == true && valid.rightHand, "right-hand");
                    AppendProtocolSample(output, canonical,
                        enabled?.leftFootEffector == true && valid.leftFoot, "left-foot");
                    AppendProtocolSample(output, canonical,
                        enabled?.rightFootEffector == true && valid.rightFoot, "right-foot");
                }
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
            KimodoConstraintMask sourceValid = KimodoConstraintMask.FromSample(source);
            bool valid = IsValid(sourceValid, channel) && KimodoSampleDataLayout.IsValid(source.sampleData);
            SetValid(destination.validMask, channel, valid);
            SetEnabled(destination.enableMask, channel, IsEnabled(source.enableMask, channel));
            if (valid) Array.Copy(source.sampleData.data, offset, destination.sampleData.data, offset, count);
        }

        private static void CopyEffectorChannel(
            List<KimodoMarkerSampleResult> ordered,
            KimodoMarkerSampleResult destination,
            SampleChannel channel)
        {
            KimodoMarkerSampleResult source = FindLatest(ordered, channel);
            if (source == null) return;
            KimodoConstraintMask sourceValid = KimodoConstraintMask.FromSample(source);
            bool valid = IsValid(sourceValid, channel);
            KimodoRigidTransform value = channel switch
            {
                SampleChannel.LeftHandEffector => source.effectors?.leftHand,
                SampleChannel.RightHandEffector => source.effectors?.rightHand,
                SampleChannel.LeftFootEffector => source.effectors?.leftFoot,
                SampleChannel.RightFootEffector => source.effectors?.rightFoot,
                _ => null
            };
            valid &= IsValidTransform(value);
            SetValid(destination.validMask, channel, valid);
            SetEnabled(destination.enableMask, channel, IsEnabled(source.enableMask, channel));
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
            KimodoConstraintMask valid = KimodoConstraintMask.FromSample(sample);
            KimodoSampleChannelMask enabled = sample.enableMask;
            string mode = NormalizeMode(sample.constraintMode);
            bool fullBody = mode == "fullbody" || mode == "constraint" ||
                mode == "mix" && (enabled?.muscle49 == true && valid.muscle ||
                    enabled?.rootTQ == true && valid.rootTQ ||
                    enabled?.leftFootTQ == true && valid.leftFootTQ ||
                    enabled?.rightFootTQ == true && valid.rightFootTQ);
            bool root2D = mode == "root2d" ||
                mode == "mix" && (enabled?.root2DPosition == true && valid.rootPosition ||
                    enabled?.root2DHeading == true && valid.rootHeading);
            bool effector = mode == "effector" || mode == "ik" ||
                mode == "mix" || mode == "left-hand" || mode == "right-hand" ||
                mode == "left-foot" || mode == "right-foot";
            return channel switch
            {
                SampleChannel.Muscle49 => fullBody,
                SampleChannel.RootTQ => fullBody,
                SampleChannel.LeftFootTQ => fullBody,
                SampleChannel.RightFootTQ => fullBody,
                SampleChannel.Root2DPosition => root2D,
                SampleChannel.Root2DHeading => root2D,
                SampleChannel.LeftHandEffector => effector && mode != "right-hand" && mode != "left-foot" &&
                    mode != "right-foot" && enabled?.leftHandEffector == true && valid.leftHand,
                SampleChannel.RightHandEffector => effector && mode != "left-hand" && mode != "left-foot" &&
                    mode != "right-foot" && enabled?.rightHandEffector == true && valid.rightHand,
                SampleChannel.LeftFootEffector => effector && mode != "left-hand" && mode != "right-hand" &&
                    mode != "right-foot" && enabled?.leftFootEffector == true && valid.leftFoot,
                SampleChannel.RightFootEffector => effector && mode != "left-hand" && mode != "right-hand" &&
                    mode != "left-foot" && enabled?.rightFootEffector == true && valid.rightFoot,
                _ => false
            };
        }

        private static string ResolveComposedMode(
            List<KimodoMarkerSampleResult> ordered,
            KimodoMarkerSampleResult sample)
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                if (NormalizeMode(ordered[i]?.constraintMode) == "mix") return "mix";
            }
            KimodoConstraintMask valid = KimodoConstraintMask.FromSample(sample);
            KimodoSampleChannelMask enabled = sample.enableMask;
            bool fullBody = enabled?.muscle49 == true && valid.muscle ||
                enabled?.rootTQ == true && valid.rootTQ ||
                enabled?.leftFootTQ == true && valid.leftFootTQ ||
                enabled?.rightFootTQ == true && valid.rightFootTQ;
            bool root2D = enabled?.root2DPosition == true && valid.rootPosition ||
                enabled?.root2DHeading == true && valid.rootHeading;
            bool effector = enabled?.leftHandEffector == true && valid.leftHand ||
                enabled?.rightHandEffector == true && valid.rightHand ||
                enabled?.leftFootEffector == true && valid.leftFoot ||
                enabled?.rightFootEffector == true && valid.rightFoot;
            int familyCount = (fullBody ? 1 : 0) + (root2D ? 1 : 0) + (effector ? 1 : 0);
            if (familyCount > 1) return "mix";
            if (fullBody) return "fullbody";
            if (root2D) return "root2d";
            if (effector) return "effector";
            return "fullbody";
        }

        private static string NormalizeMode(string mode) =>
            (mode ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');

        private static bool IsEffectorEnabled(
            KimodoSampleChannelMask enabled, KimodoConstraintMask valid, string type) => type switch
        {
            "left-hand" => enabled?.leftHandEffector == true && valid.leftHand,
            "right-hand" => enabled?.rightHandEffector == true && valid.rightHand,
            "left-foot" => enabled?.leftFootEffector == true && valid.leftFoot,
            "right-foot" => enabled?.rightFootEffector == true && valid.rightFoot,
            _ => false
        };

        private static bool IsEnabled(KimodoSampleChannelMask mask, SampleChannel channel) => channel switch
        {
            SampleChannel.Muscle49 => mask?.muscle49 == true,
            SampleChannel.RootTQ => mask?.rootTQ == true,
            SampleChannel.LeftFootTQ => mask?.leftFootTQ == true,
            SampleChannel.RightFootTQ => mask?.rightFootTQ == true,
            SampleChannel.Root2DPosition => mask?.root2DPosition == true,
            SampleChannel.Root2DHeading => mask?.root2DHeading == true,
            SampleChannel.LeftHandEffector => mask?.leftHandEffector == true,
            SampleChannel.RightHandEffector => mask?.rightHandEffector == true,
            SampleChannel.LeftFootEffector => mask?.leftFootEffector == true,
            SampleChannel.RightFootEffector => mask?.rightFootEffector == true,
            _ => false
        };

        private static bool IsValid(KimodoConstraintMask mask, SampleChannel channel) => channel switch
        {
            SampleChannel.Muscle49 => mask?.muscle == true,
            SampleChannel.RootTQ => mask?.rootTQ == true,
            SampleChannel.LeftFootTQ => mask?.leftFootTQ == true,
            SampleChannel.RightFootTQ => mask?.rightFootTQ == true,
            SampleChannel.Root2DPosition => mask?.rootPosition == true,
            SampleChannel.Root2DHeading => mask?.rootHeading == true,
            SampleChannel.LeftHandEffector => mask?.leftHand == true,
            SampleChannel.RightHandEffector => mask?.rightHand == true,
            SampleChannel.LeftFootEffector => mask?.leftFoot == true,
            SampleChannel.RightFootEffector => mask?.rightFoot == true,
            _ => false
        };

        private static void SetEnabled(KimodoSampleChannelMask mask, SampleChannel channel, bool value)
        {
            if (mask == null) return;
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

        private static void SetValid(KimodoConstraintMask mask, SampleChannel channel, bool value)
        {
            if (mask == null) return;
            switch (channel)
            {
                case SampleChannel.Muscle49: mask.muscle = value; break;
                case SampleChannel.RootTQ: mask.rootTQ = value; break;
                case SampleChannel.LeftFootTQ: mask.leftFootTQ = value; break;
                case SampleChannel.RightFootTQ: mask.rightFootTQ = value; break;
                case SampleChannel.Root2DPosition: mask.rootPosition = value; break;
                case SampleChannel.Root2DHeading: mask.rootHeading = value; break;
                case SampleChannel.LeftHandEffector: mask.leftHand = value; break;
                case SampleChannel.RightHandEffector: mask.rightHand = value; break;
                case SampleChannel.LeftFootEffector: mask.leftFoot = value; break;
                case SampleChannel.RightFootEffector: mask.rightFoot = value; break;
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
