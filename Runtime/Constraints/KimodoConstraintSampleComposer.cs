using System;
using System.Collections.Generic;
using KimodoUnityBridge;

namespace KimodoBridge
{
    /// <summary>
    /// Runtime composition for canonical SampleResult channels.
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
                if (sample == null || !sample.enabled) continue;
                if (sample.creationOrder == 0) sample.creationOrder = i + 1L;
                sample.enableMask ??= new KimodoConstraintMask();
                sample.validMask ??= new KimodoConstraintMask();
                normalized.Add(sample);
            }

            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(normalized, frameRate).Values)
            {
                if (group.Count == 0) continue;
                var ordered = new List<KimodoMarkerSampleResult>(group);
                ordered.Sort((a, b) => a.creationOrder.CompareTo(b.creationOrder));
                if (ordered.Count == 1)
                {
                    output.Add(ordered[0]);
                    continue;
                }
                var result = new KimodoMarkerSampleResult
                {
                    sampleTime = ordered[ordered.Count - 1].sampleTime,
                    constraintMode = "mix",
                    enabled = true,
                    sampleData = new MuscleSample(),
                    enableMask = new KimodoConstraintMask(),
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
                if (!KimodoConstraintMask.IsActive(result, "muscle"))
                {
                    MuscleSample supportPose = CloneLatestSupportSampleData(
                        ordered,
                        out KimodoConstraintMask supportValid);
                    if (supportPose != null)
                    {
                        result.sampleData = supportPose;
                        result.enableMask.muscle = false;
                        result.validMask.muscle = supportValid.muscle;
                        result.validMask.rootTQ = supportValid.rootTQ;
                        result.validMask.leftFootTQ = supportValid.leftFootTQ;
                        result.validMask.rightFootTQ = supportValid.rightFootTQ;
                    }
                }

                KimodoMarkerSampleResult rootPosition = FindLatest(ordered, SampleChannel.Root2DPosition);
                if (rootPosition != null)
                {
                    if (rootPosition.rootOverride == null)
                        throw new InvalidOperationException("Root2D position is valid but its payload is missing.");
                    result.rootOverride = rootPosition.rootOverride.Clone();
                    result.enableMask.rootPosition = rootPosition.enableMask?.rootPosition == true;
                    result.validMask.rootPosition = KimodoConstraintMask.FromSample(rootPosition).rootPosition;
                }
                KimodoMarkerSampleResult rootHeading = FindLatest(ordered, SampleChannel.Root2DHeading);
                if (rootHeading != null)
                {
                    if (rootHeading.rootOverride == null)
                        throw new InvalidOperationException("Root2D heading is valid but its payload is missing.");
                    result.rootOverride.q = rootHeading.rootOverride.q;
                    result.enableMask.rootHeading = result.enableMask.rootPosition &&
                        rootHeading.enableMask?.rootHeading == true;
                    result.validMask.rootHeading = result.validMask.rootPosition &&
                        KimodoConstraintMask.FromSample(rootHeading).rootHeading;
                }

                CopyEffectorChannel(ordered, result, SampleChannel.LeftHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.LeftFootEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightFootEffector);
                result.constraintMode = ResolveComposedMode(ordered);
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
            IReadOnlyList<KimodoMarkerSampleResult> samples, double frameRate) =>
            ComposeCanonicalSamples(samples, frameRate);

        public static List<KimodoMarkerSampleResult> MergeAsUnifiedSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples, double frameRate) =>
            ComposeCanonicalSamples(samples, frameRate);

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
            bool valid = IsValid(sourceValid, channel);
            SetValid(destination.validMask, channel, valid);
            SetEnabled(destination.enableMask, channel, IsEnabled(source.enableMask, channel));
            if (!valid) return;
            if (!KimodoSampleDataLayout.IsValid(source.sampleData))
                throw new InvalidOperationException($"{channel} is valid but its sampleData payload is malformed.");
            Array.Copy(source.sampleData.data, offset, destination.sampleData.data, offset, count);
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
            SetValid(destination.validMask, channel, valid);
            SetEnabled(destination.enableMask, channel, IsEnabled(source.enableMask, channel));
            if (!valid) return;
            if (value == null)
                throw new InvalidOperationException($"{channel} is valid but its effector payload is missing.");
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
                if (sample != null && sample.enabled && DeclaresChannel(sample, channel)) return sample;
            }
            return null;
        }

        private static bool DeclaresChannel(KimodoMarkerSampleResult sample, SampleChannel channel)
        {
            string mode = NormalizeMode(sample.constraintMode);
            bool fullBody = mode == "fullbody" || mode == "mix";
            bool root2D = mode == "root2d" || mode == "mix";
            bool effector = mode == "effector" ||
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
                    mode != "right-foot",
                SampleChannel.RightHandEffector => effector && mode != "left-hand" && mode != "left-foot" &&
                    mode != "right-foot",
                SampleChannel.LeftFootEffector => effector && mode != "left-hand" && mode != "right-hand" &&
                    mode != "right-foot",
                SampleChannel.RightFootEffector => effector && mode != "left-hand" && mode != "right-hand" &&
                    mode != "left-foot",
                _ => false
            };
        }

        private static MuscleSample CloneLatestSupportSampleData(
            List<KimodoMarkerSampleResult> ordered,
            out KimodoConstraintMask supportValid)
        {
            supportValid = null;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = ordered.Count - 1; i >= 0; i--)
                {
                    KimodoMarkerSampleResult sample = ordered[i];
                    string mode = NormalizeMode(sample.constraintMode);
                    bool effector = mode == "effector" ||
                        mode == "left-hand" || mode == "right-hand" ||
                        mode == "left-foot" || mode == "right-foot";
                    if (pass == 0 ? !effector : mode != "root2d") continue;
                    KimodoConstraintMask valid = KimodoConstraintMask.FromSample(sample);
                    if (!valid.muscle) continue;
                    if (!KimodoSampleDataLayout.IsValid(sample.sampleData))
                        throw new InvalidOperationException("Muscle data is valid but its sampleData payload is malformed.");
                    supportValid = valid;
                    return sample.sampleData.Clone();
                }
            }
            return null;
        }

        private static string ResolveComposedMode(
            List<KimodoMarkerSampleResult> ordered)
        {
            bool fullBody = false;
            bool root2D = false;
            bool effector = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                KimodoMarkerSampleResult source = ordered[i];
                string mode = NormalizeMode(source?.constraintMode);
                if (mode == "mix") return "mix";
                fullBody |= mode == "fullbody";
                root2D |= mode == "root2d";
                effector |= mode == "effector" ||
                    mode == "left-hand" || mode == "right-hand" ||
                    mode == "left-foot" || mode == "right-foot";
            }
            int familyCount = (fullBody ? 1 : 0) + (root2D ? 1 : 0) + (effector ? 1 : 0);
            if (familyCount > 1) return "mix";
            if (fullBody) return "fullbody";
            if (root2D) return "root2d";
            if (effector) return "effector";
            return "fullbody";
        }

        private static string NormalizeMode(string mode) =>
            KimodoConstraintInternal.NormalizeMode(mode);

        private static bool IsEnabled(KimodoConstraintMask mask, SampleChannel channel) => channel switch
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

        private static void SetEnabled(KimodoConstraintMask mask, SampleChannel channel, bool value)
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
