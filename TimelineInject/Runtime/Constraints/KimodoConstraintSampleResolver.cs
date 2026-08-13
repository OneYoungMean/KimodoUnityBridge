using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;

namespace TimelineInject
{
    /// <summary>Runtime-independent canonical constraint composition.  Editor
    /// and runtime convert this result to a rig only at their respective edges.</summary>
    public static class KimodoConstraintSampleResolver
    {
        public static void ComposeCharacterPosesAtSameFrame(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            if (samples == null || samples.Count < 2 || frameRate <= 0.0) return;
            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(samples, frameRate).Values)
            {
                if (group.Count < 2) continue;
                bool hasPose = false;
                for (int i = 0; i < group.Count; i++)
                {
                    hasPose |= group[i].characterPose != null && group[i].characterPose.TryValidate(out _);
                }
                if (!hasPose) continue;
                CharacterPose pose = ComposePose(group);
                for (int i = 0; i < group.Count; i++) group[i].characterPose = pose.Clone();
            }
        }

        public static List<KimodoMarkerSampleResult> ExpandProtocolSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            var output = new List<KimodoMarkerSampleResult>();
            if (samples == null) return output;
            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(samples, frameRate).Values)
            {
                CharacterPose canonical = ComposePose(group);
                bool hasCanonicalPose = false;
                bool hasUnified = false;
                for (int i = 0; i < group.Count; i++)
                {
                    hasCanonicalPose |= group[i].characterPose != null && group[i].characterPose.TryValidate(out _);
                    if (string.Equals(group[i].constraintType, "constraint", StringComparison.OrdinalIgnoreCase))
                    {
                        hasUnified = true;
                    }
                }

                if (hasUnified)
                {
                    KimodoMarkerSampleResult merged = CreateUnifiedSample(group, canonical, hasCanonicalPose);
                    if (merged != null)
                    {
                        AppendProtocolSamples(
                            output,
                            merged,
                            canonical,
                            hasCanonicalPose,
                            KimodoConstraintMask.Resolve(merged.mask, merged.constraintType));
                    }
                    continue;
                }

                for (int i = 0; i < group.Count; i++)
                {
                    KimodoMarkerSampleResult source = group[i];
                    KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source.mask, source.constraintType);
                    if (group.Count == 1)
                    {
                        output.Add(source.Clone());
                    }
                    else if (!hasCanonicalPose)
                    {
                        output.Add(source.Clone());
                    }
                    else
                    {
                        KimodoMarkerSampleResult projected = source.Clone();
                        projected.characterPose = canonical.Clone();
                        output.Add(projected);
                    }
                }
            }
            return output;
        }

        /// <summary>Composes same-frame samples into the one Marker format used
        /// by Timeline authoring.</summary>
        public static List<KimodoMarkerSampleResult> MergeAsUnifiedSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            var output = new List<KimodoMarkerSampleResult>();
            if (samples == null || frameRate <= 0.0) return output;

            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(samples, frameRate).Values)
            {
                CharacterPose canonical = ComposePose(group);
                bool hasCanonicalPose = false;
                for (int i = 0; i < group.Count; i++)
                {
                    hasCanonicalPose |= group[i].characterPose != null && group[i].characterPose.TryValidate(out _);
                }

                KimodoMarkerSampleResult merged = CreateUnifiedSample(group, canonical, hasCanonicalPose);
                if (merged != null) output.Add(merged);
            }
            return output;
        }

        private static SortedDictionary<int, List<KimodoMarkerSampleResult>> GroupByFrame(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
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

        private static KimodoMarkerSampleResult CreateUnifiedSample(
            List<KimodoMarkerSampleResult> group,
            CharacterPose canonical,
            bool hasCanonicalPose)
        {
            if (group == null || group.Count == 0) return null;
            KimodoMarkerSampleResult seed = null;
            var mask = new KimodoConstraintMask();
            bool hasHeading = false;
            int headingPriority = -1;
            for (int i = 0; i < group.Count; i++)
            {
                KimodoMarkerSampleResult source = group[i];
                if (source == null) continue;
                if (seed == null || SeedPriority(source) > SeedPriority(seed)) seed = source;
                KimodoConstraintMask sourceMask = KimodoConstraintMask.Resolve(source.mask, source.constraintType);
                mask.muscle |= sourceMask.muscle;
                mask.rootPosition |= sourceMask.rootPosition;
                mask.rootHeading |= sourceMask.rootHeading;
                mask.leftFoot |= sourceMask.leftFoot;
                mask.rightFoot |= sourceMask.rightFoot;
                mask.leftHand |= sourceMask.leftHand;
                mask.rightHand |= sourceMask.rightHand;
                if (sourceMask.rootHeading)
                {
                    int sourcePriority = IsRoot2D(source) ? 1 : 0;
                    if (sourcePriority >= headingPriority)
                    {
                        hasHeading = source.hasRootHeading;
                        headingPriority = sourcePriority;
                    }
                }
            }
            if (seed == null) return null;

            KimodoMarkerSampleResult merged = seed.Clone();
            merged.constraintType = "constraint";
            merged.mask = mask;
            merged.hasRootHeading = hasHeading;
            merged.characterPose = hasCanonicalPose ? canonical.Clone() : null;
            return merged;
        }

        private static int SeedPriority(KimodoMarkerSampleResult sample)
        {
            if (sample == null) return -1;
            bool hasPose = sample.characterPose != null && sample.characterPose.TryValidate(out _);
            KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample.mask, sample.constraintType);
            if (hasPose && mask.muscle) return 3;
            return hasPose ? 1 : 0;
        }

        private static CharacterPose ComposePose(List<KimodoMarkerSampleResult> samples)
        {
            KimodoMarkerSampleResult seed = null;
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample.mask, sample.constraintType);
                if (sample.characterPose != null && mask.muscle)
                {
                    seed = sample;
                }
            }
            if (seed == null)
            {
                for (int i = 0; i < samples.Count && seed == null; i++)
                {
                    if (samples[i].characterPose != null) seed = samples[i];
                }
            }
            CharacterPose composed = seed?.characterPose?.Clone();
            if (composed == null) return new CharacterPose();

            // Muscle → Foot IK → Hand IK → Root2D. T/Q are Humanoid IK goals;
            // Root2D is intentionally last so it moves the solved body as one.
            CopyFootGoals(samples, composed);
            CopyHandGoals(samples, composed);
            CopyRoot2D(samples, composed);
            return composed;
        }

        private static void CopyFootGoals(List<KimodoMarkerSampleResult> samples, CharacterPose target)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                if (source == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source.mask, source.constraintType);
                if (source.characterPose == null) continue;
                if (mask.leftFoot) CopyTransform(source.characterPose.feet.left, target.feet.left);
                if (mask.rightFoot) CopyTransform(source.characterPose.feet.right, target.feet.right);
            }
        }

        private static void CopyHandGoals(List<KimodoMarkerSampleResult> samples, CharacterPose target)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                if (source == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source.mask, source.constraintType);
                if (source.characterPose == null) continue;
                if (mask.leftHand) CopyTransform(source.characterPose.hands.left, target.hands.left);
                if (mask.rightHand) CopyTransform(source.characterPose.hands.right, target.hands.right);
            }
        }

        private static void CopyRoot2D(List<KimodoMarkerSampleResult> samples, CharacterPose target)
        {
            // FullBody establishes the base root; explicit Root2D is the final
            // whole-character transform regardless of marker enumeration order.
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                if (source == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source.mask, source.constraintType);
                if (source.characterPose == null || IsRoot2D(source)) continue;
                if (mask.rootPosition) target.root.t = source.characterPose.root.t;
                if (mask.rootHeading && source.hasRootHeading) target.root.q = source.characterPose.root.q;
            }
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                if (source == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source.mask, source.constraintType);
                if (source.characterPose == null || !IsRoot2D(source)) continue;
                if (mask.rootPosition) target.root.t = source.characterPose.root.t;
                if (mask.rootHeading && source.hasRootHeading) target.root.q = source.characterPose.root.q;
            }
        }

        private static bool IsRoot2D(KimodoMarkerSampleResult sample) =>
            string.Equals((sample?.constraintType ?? string.Empty).Replace('_', '-'), "root2d", StringComparison.OrdinalIgnoreCase);

        private static void AppendProtocolSamples(
            List<KimodoMarkerSampleResult> output,
            KimodoMarkerSampleResult source,
            CharacterPose canonical,
            bool hasCanonicalPose,
            KimodoConstraintMask mask)
        {
            if (mask.muscle) AppendProtocolSample(output, source, canonical, hasCanonicalPose, "fullbody");
            if (mask.rootPosition || mask.rootHeading)
            {
                KimodoMarkerSampleResult root2D = AppendProtocolSample(output, source, canonical, hasCanonicalPose, "root2d");
                root2D.hasRootHeading = mask.rootHeading && source.hasRootHeading;
            }
            if (mask.leftHand) AppendProtocolSample(output, source, canonical, hasCanonicalPose, "left-hand");
            if (mask.rightHand) AppendProtocolSample(output, source, canonical, hasCanonicalPose, "right-hand");
            if (mask.leftFoot) AppendProtocolSample(output, source, canonical, hasCanonicalPose, "left-foot");
            if (mask.rightFoot) AppendProtocolSample(output, source, canonical, hasCanonicalPose, "right-foot");
        }

        private static KimodoMarkerSampleResult AppendProtocolSample(
            List<KimodoMarkerSampleResult> output,
            KimodoMarkerSampleResult source,
            CharacterPose canonical,
            bool hasCanonicalPose,
            string type)
        {
            KimodoMarkerSampleResult sample = source.Clone();
            sample.constraintType = type;
            sample.characterPose = hasCanonicalPose ? canonical.Clone() : null;
            sample.mask = KimodoConstraintMask.ForType(type);
            output.Add(sample);
            return sample;
        }

        private static void CopyTransform(CharacterPoseTransform source, CharacterPoseTransform destination)
        {
            if (source == null || destination == null) return;
            destination.t = source.t;
            destination.q = source.q;
        }
    }
}
