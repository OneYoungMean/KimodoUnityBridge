using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using UnityEngine;

namespace TimelineInject
{
    /// <summary>Runtime-independent canonical constraint composition.  Editor
    /// and runtime convert this result to a rig only at their respective edges.</summary>
    public static class KimodoConstraintSampleComposer
    {
        /// <summary>
        /// Composes canonical samples at the same frame. Constraint
        /// participation is selected by mode, validity by enableMask, and the
        /// last-created enabled constraint owns each participating channel.
        /// Invalid data never falls back to an older value.
        /// </summary>
        public static List<KimodoMarkerSampleResult> ComposeCanonicalSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            var output = new List<KimodoMarkerSampleResult>();
            if (samples == null || frameRate <= 0.0)
            {
                return output;
            }

            var normalizedSamples = new List<KimodoMarkerSampleResult>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i]?.Clone();
                if (sample == null) continue;
                if (sample.creationOrder == 0)
                {
                    // Legacy callers have no persisted order; input order is
                    // their creation-order contract and the later item wins.
                    sample.creationOrder = i + 1L;
                }
                MigrateLegacySample(sample);
                normalizedSamples.Add(sample);
            }

            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(normalizedSamples, frameRate).Values)
            {
                if (group.Count == 0) continue;
                var result = new KimodoMarkerSampleResult
                {
                    sampleTime = group[group.Count - 1].sampleTime,
                    constraintType = "constraint",
                    constraintMode = "mix",
                    enabled = true
                };
                var ordered = new List<KimodoMarkerSampleResult>(group);
                ordered.Sort(CompareCreationOrder);

                CopyDataChannel(ordered, result, SampleChannel.Muscle49,
                    KimodoSampleDataLayout.BodyMuscleOffset,
                    KimodoSampleDataLayout.BodyMuscleCount);
                CopyDataChannel(ordered, result, SampleChannel.RootTQ,
                    KimodoSampleDataLayout.RootTqOffset,
                    KimodoSampleDataLayout.RootTqCount);
                CopyDataChannel(ordered, result, SampleChannel.LeftFootTQ,
                    KimodoSampleDataLayout.LeftFootTqOffset,
                    KimodoSampleDataLayout.FootTqCount);
                CopyDataChannel(ordered, result, SampleChannel.RightFootTQ,
                    KimodoSampleDataLayout.RightFootTqOffset,
                    KimodoSampleDataLayout.FootTqCount);

                KimodoMarkerSampleResult rootPosition = FindLatest(ordered, SampleChannel.Root2DPosition);
                if (rootPosition != null)
                {
                    result.root2DOverride = rootPosition.root2DOverride != null
                        ? new CharacterPoseTransform
                        {
                            t = rootPosition.root2DOverride.t,
                            q = rootPosition.root2DOverride.q
                        }
                        : new CharacterPoseTransform();
                    result.enableMask.root2DPosition = rootPosition.enableMask?.root2DPosition == true;
                }

                KimodoMarkerSampleResult rootHeading = FindLatest(ordered, SampleChannel.Root2DHeading);
                if (rootHeading != null)
                {
                    result.enableMask.root2DHeading = result.enableMask.root2DPosition &&
                        rootHeading.enableMask?.root2DHeading == true;
                }

                CopyEffectorChannel(ordered, result, SampleChannel.LeftHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightHandEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.LeftFootEffector);
                CopyEffectorChannel(ordered, result, SampleChannel.RightFootEffector);
                result.enableMask.NormalizeDependencies();
                result.enableMask.NormalizeDependencies();
                result.mask = ToLegacyMask(result.enableMask);
                if (KimodoSampleDataLayout.TryDecodeCharacterPose(
                        result.sampleData,
                        out CharacterPose canonicalPose,
                        out _))
                {
                    KimodoSampleResultPoseUtility.TryEncode(result, canonicalPose, out _);
                }
                output.Add(result);
            }

            return output;
        }

        private enum SampleChannel
        {
            Muscle49,
            RootTQ,
            LeftFootTQ,
            RightFootTQ,
            Root2DPosition,
            Root2DHeading,
            LeftHandEffector,
            RightHandEffector,
            LeftFootEffector,
            RightFootEffector
        }

        private static int CompareCreationOrder(
            KimodoMarkerSampleResult left,
            KimodoMarkerSampleResult right)
        {
            return left.creationOrder.CompareTo(right.creationOrder);
        }

        private static KimodoMarkerSampleResult FindLatest(
            List<KimodoMarkerSampleResult> ordered,
            SampleChannel channel)
        {
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                KimodoMarkerSampleResult sample = ordered[i];
                if (sample != null && sample.enabled && Participates(sample, channel))
                {
                    return sample;
                }
            }
            return null;
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
            bool valid = source.enableMask != null && IsValid(source.enableMask, channel);
            SetValid(destination.enableMask, channel, valid);
            if (!valid || !KimodoSampleDataLayout.IsValidLength(source.sampleData)) return;
            Array.Copy(source.sampleData, offset, destination.sampleData, offset, count);
        }

        private static void CopyEffectorChannel(
            List<KimodoMarkerSampleResult> ordered,
            KimodoMarkerSampleResult destination,
            SampleChannel channel)
        {
            KimodoMarkerSampleResult source = FindLatest(ordered, channel);
            if (source == null) return;
            bool valid = source.enableMask != null && IsValid(source.enableMask, channel);
            SetValid(destination.enableMask, channel, valid);
            if (!valid || source.effectors == null) return;

            CharacterPoseTransform sourceTransform = channel switch
            {
                SampleChannel.LeftHandEffector => source.effectors.hands?.left,
                SampleChannel.RightHandEffector => source.effectors.hands?.right,
                SampleChannel.LeftFootEffector => source.effectors.feet?.left,
                SampleChannel.RightFootEffector => source.effectors.feet?.right,
                _ => null
            };
            if (!IsValidTransform(sourceTransform))
            {
                SetValid(destination.enableMask, channel, false);
                return;
            }
            CharacterPoseSides sides = channel switch
            {
                SampleChannel.LeftHandEffector => destination.effectors.hands,
                SampleChannel.RightHandEffector => destination.effectors.hands,
                SampleChannel.LeftFootEffector => destination.effectors.feet,
                SampleChannel.RightFootEffector => destination.effectors.feet,
                _ => null
            };
            if (sides == null) return;
            CharacterPoseTransform copy = new CharacterPoseTransform
            {
                t = sourceTransform.t,
                q = sourceTransform.q
            };
            if (channel == SampleChannel.LeftHandEffector) sides.left = copy;
            else if (channel == SampleChannel.RightHandEffector) sides.right = copy;
            else if (channel == SampleChannel.LeftFootEffector) sides.left = copy;
            else if (channel == SampleChannel.RightFootEffector) sides.right = copy;
        }

        private static bool Participates(KimodoMarkerSampleResult sample, SampleChannel channel)
        {
            string mode = (sample.constraintMode ?? sample.constraintType ?? string.Empty)
                .Trim().ToLowerInvariant().Replace('_', '-');
            bool fullBody = mode == "fullbody" || mode == "constraint" || mode == "mix" ||
                sample.mask?.muscle == true || sample.enableMask?.muscle49 == true;
            bool root2D = mode == "root2d" || mode == "mix" || sample.enableMask?.root2DPosition == true ||
                sample.enableMask?.root2DPosition == true;
            bool effector = mode == "effector" || sample.mask?.AnyEndEffector == true;
            return channel switch
            {
                SampleChannel.Muscle49 => fullBody,
                SampleChannel.RootTQ => fullBody,
                SampleChannel.LeftFootTQ => fullBody,
                SampleChannel.RightFootTQ => fullBody,
                SampleChannel.Root2DPosition => root2D,
                SampleChannel.Root2DHeading => root2D,
                SampleChannel.LeftHandEffector => effector && (sample.mask?.leftHand == true || sample.enableMask?.leftHandEffector == true),
                SampleChannel.RightHandEffector => effector && (sample.mask?.rightHand == true || sample.enableMask?.rightHandEffector == true),
                SampleChannel.LeftFootEffector => effector && (sample.mask?.leftFoot == true || sample.enableMask?.leftFootEffector == true),
                SampleChannel.RightFootEffector => effector && (sample.mask?.rightFoot == true || sample.enableMask?.rightFootEffector == true),
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

        private static bool IsValidTransform(CharacterPoseTransform value)
        {
            if (value == null) return false;
            Quaternion q = value.q;
            return IsFinite(value.t.x) && IsFinite(value.t.y) && IsFinite(value.t.z) &&
                IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w) &&
                q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 1e-8f;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static KimodoConstraintMask ToLegacyMask(KimodoSampleChannelMask valid)
        {
            return new KimodoConstraintMask
            {
                muscle = valid.muscle49,
                rootPosition = valid.root2DPosition,
                rootHeading = valid.root2DHeading,
                leftHand = valid.leftHandEffector,
                rightHand = valid.rightHandEffector,
                leftFoot = valid.leftFootEffector,
                rightFoot = valid.rightFootEffector
            };
        }

        private static void MigrateLegacySample(KimodoMarkerSampleResult sample)
        {
            if (sample.enableMask == null) sample.enableMask = new KimodoSampleChannelMask();
            if (sample.mask != null ||
                (!string.IsNullOrWhiteSpace(sample.constraintType) &&
                 !string.Equals(sample.constraintType, "constraint", StringComparison.OrdinalIgnoreCase)))
            {
                KimodoConstraintMask legacy = KimodoConstraintMask.Resolve(sample.mask, sample.constraintType);
                bool explicitRoot2D = string.Equals(sample.constraintType, "root2d", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sample.constraintMode, "root2d", StringComparison.OrdinalIgnoreCase) ||
                    sample.enableMask?.root2DPosition == true;
                sample.enableMask.root2DPosition |= explicitRoot2D && legacy.rootPosition;
                sample.enableMask.root2DHeading |= explicitRoot2D && legacy.rootHeading && sample.enableMask.root2DPosition;
                sample.enableMask.leftHandEffector |= legacy.leftHand;
                sample.enableMask.rightHandEffector |= legacy.rightHand;
                sample.enableMask.leftFootEffector |= legacy.leftFoot;
                sample.enableMask.rightFootEffector |= legacy.rightFoot;
            }
            sample.enableMask.NormalizeDependencies();
        }
        /// <summary>Returns the effective pose for one unified marker without
        /// mutating its authored FullBody and Root2D data.</summary>
        public static KimodoMarkerSampleResult ResolveUnifiedSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null) return null;
            KimodoMarkerSampleResult resolved = sample.Clone();
            KimodoSampleResultPoseUtility.TryEncode(
                resolved,
                ComposePose(new List<KimodoMarkerSampleResult> { sample }),
                out _);
            return resolved;
        }

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
                    hasPose |= TryGetPose(group[i], out _, out _);
                }
                if (!hasPose) continue;
                CharacterPose pose = ComposePose(group);
                for (int i = 0; i < group.Count; i++)
                {
                    // A unified marker owns separate authored FullBody and
                    // Root2D data.  Writing the resolved pose back here
                    // would bake Root2D into that source data.
                    if (!string.Equals(group[i].constraintType, "constraint", StringComparison.OrdinalIgnoreCase))
                    {
                        KimodoSampleResultPoseUtility.TryEncode(group[i], pose.Clone(), out _);
                    }
                }
            }
        }

        public static List<KimodoMarkerSampleResult> ExpandProtocolSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            if (HasMixSamples(samples))
            {
                return ExpandCanonicalSamples(samples, frameRate);
            }
            if (HasModeAwareSamples(samples))
            {
                return ExpandModeAwareSamples(samples, frameRate);
            }

            var output = new List<KimodoMarkerSampleResult>();
            if (samples == null) return output;
            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(samples, frameRate).Values)
            {
                CharacterPose canonical = ComposePose(group);
                bool hasCanonicalPose = false;
                bool hasUnified = false;
                for (int i = 0; i < group.Count; i++)
                {
                    hasCanonicalPose |= TryGetPose(group[i], out _, out _);
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
                        KimodoSampleResultPoseUtility.TryEncode(projected, canonical.Clone(), out _);
                        output.Add(projected);
                    }
                }
            }
            return output;
        }

        private static bool HasMixSamples(IReadOnlyList<KimodoMarkerSampleResult> samples)
        {
            if (samples == null) return false;
            for (int i = 0; i < samples.Count; i++)
            {
                if (string.Equals(samples[i]?.constraintMode, "mix", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<KimodoMarkerSampleResult> ExpandCanonicalSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            var output = new List<KimodoMarkerSampleResult>();
            foreach (KimodoMarkerSampleResult canonical in ComposeCanonicalSamples(samples, frameRate))
            {
                if (canonical == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(
                    canonical.mask,
                    "constraint");
                bool hasPose = TryGetPose(canonical, out CharacterPose canonicalPose, out _);
                AppendProtocolSamples(
                    output,
                    canonical,
                    canonicalPose,
                    hasPose,
                    mask);
            }
            return output;
        }

        private static bool HasModeAwareSamples(IReadOnlyList<KimodoMarkerSampleResult> samples)
        {
            if (samples == null) return false;
            for (int i = 0; i < samples.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(samples[i]?.constraintMode)) return true;
            }
            return false;
        }

        private static List<KimodoMarkerSampleResult> ExpandModeAwareSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            var output = new List<KimodoMarkerSampleResult>();
            foreach (List<KimodoMarkerSampleResult> group in GroupByFrame(samples, frameRate).Values)
            {
                KimodoMarkerSampleResult root = LastModeSample(group, "root2d");
                KimodoMarkerSampleResult fullBody = LastModeSample(group, "fullbody");
                KimodoMarkerSampleResult effectorSample = MergeEffectorModeSamples(group);

                if (root != null && fullBody != null)
                {
                    ApplyRoot2DOverlay(root, fullBody);
                }
                if (root != null && effectorSample != null)
                {
                    ApplyRoot2DOverlay(root, effectorSample);
                }

                // Keep the protocol family separate. A Root2D record remains
                // an explicit input even when it also overlays FullBody/effector data.
                if (root != null)
                {
                    KimodoMarkerSampleResult rootSample = root.Clone();
                    rootSample.constraintType = "root2d";
                    rootSample.mask = KimodoConstraintMask.ForType("root2d");
                    output.Add(rootSample);
                }

                if (fullBody != null)
                {
                    KimodoMarkerSampleResult fullSample = fullBody.Clone();
                    fullSample.constraintType = "fullbody";
                    output.Add(fullSample);
                }

                if (effectorSample != null)
                {
                    KimodoConstraintMask mask = KimodoConstraintMask.Resolve(effectorSample.mask, "effector");
                    AppendEffectorSample(output, effectorSample, mask.leftHand, "left-hand");
                    AppendEffectorSample(output, effectorSample, mask.rightHand, "right-hand");
                    AppendEffectorSample(output, effectorSample, mask.leftFoot, "left-foot");
                    AppendEffectorSample(output, effectorSample, mask.rightFoot, "right-foot");
                }
            }
            return output;
        }

        private static KimodoMarkerSampleResult LastModeSample(
            List<KimodoMarkerSampleResult> group,
            string mode)
        {
            KimodoMarkerSampleResult result = null;
            for (int i = 0; i < group.Count; i++)
            {
                if (string.Equals(group[i]?.constraintMode, mode, StringComparison.OrdinalIgnoreCase))
                {
                    result = group[i];
                }
            }
            return result;
        }

        private static KimodoMarkerSampleResult MergeEffectorModeSamples(List<KimodoMarkerSampleResult> group)
        {
            KimodoMarkerSampleResult result = null;
            for (int i = 0; i < group.Count; i++)
            {
                KimodoMarkerSampleResult source = group[i];
                if (!string.Equals(source?.constraintMode, "ik", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(source?.constraintMode, "effector", StringComparison.OrdinalIgnoreCase)) continue;
                if (result == null)
                {
                    result = source.Clone();
                    continue;
                }

                KimodoConstraintMask sourceMask = KimodoConstraintMask.Resolve(source.mask, "effector");
                result.mask ??= new KimodoConstraintMask();
                result.effectors ??= new KimodoConstraintEffectors();
                result.effectors.hands ??= new CharacterPoseSides();
                result.effectors.feet ??= new CharacterPoseSides();
                if (sourceMask.leftHand)
                {
                    result.effectors.hands.left = source.effectors?.hands?.left?.Clone();
                    result.mask.leftHand = true;
                }
                if (sourceMask.rightHand)
                {
                    result.effectors.hands.right = source.effectors?.hands?.right?.Clone();
                    result.mask.rightHand = true;
                }
                if (sourceMask.leftFoot)
                {
                    result.effectors.feet.left = source.effectors?.feet?.left?.Clone();
                    result.mask.leftFoot = true;
                }
                if (sourceMask.rightFoot)
                {
                    result.effectors.feet.right = source.effectors?.feet?.right?.Clone();
                    result.mask.rightFoot = true;
                }
                if (TryGetPose(source, out CharacterPose sourcePose, out _) &&
                    TryGetPose(result, out CharacterPose resultPose, out _))
                {
                    resultPose.root = sourcePose.root.Clone();
                    resultPose.muscles = (float[])sourcePose.muscles.Clone();
                    KimodoSampleResultPoseUtility.TryEncode(result, resultPose, out _);
                }
                result.enableMask.root2DHeading = source.enableMask?.root2DHeading == true;
            }
            return result;
        }

        private static void AppendEffectorSample(
            List<KimodoMarkerSampleResult> output,
            KimodoMarkerSampleResult source,
            bool enabled,
            string type)
        {
            if (!enabled) return;
            KimodoMarkerSampleResult sample = source.Clone();
            sample.constraintType = type;
            sample.mask = KimodoConstraintMask.ForType(type);
            output.Add(sample);
        }

        private static void ApplyRoot2DOverlay(
            KimodoMarkerSampleResult rootSample,
            KimodoMarkerSampleResult targetSample)
        {
            if (!TryGetPose(rootSample, out CharacterPose root, out _) ||
                !TryGetPose(targetSample, out CharacterPose target, out _))
            {
                return;
            }

            Quaternion targetYaw = PlanarRotation(target.root.q);
            Quaternion rootYaw = PlanarRotation(root.root.q);
            target.root.t = new Vector3(root.root.t.x, target.root.t.y, root.root.t.z);
            if (rootSample.enableMask?.root2DHeading == true)
            {
                target.root.q = rootYaw * Quaternion.Inverse(targetYaw) * target.root.q;
            }
            KimodoSampleResultPoseUtility.TryEncode(targetSample, target, out _);
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
                    hasCanonicalPose |= TryGetPose(group[i], out _, out _);
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
                        hasHeading = source.enableMask?.root2DHeading == true;
                        headingPriority = sourcePriority;
                    }
                }
            }
            if (seed == null) return null;

            KimodoMarkerSampleResult merged = seed.Clone();
            merged.constraintType = "constraint";
            merged.mask = mask;
            merged.enableMask.root2DHeading = hasHeading && merged.enableMask.root2DPosition;
            merged.effectors ??= new KimodoConstraintEffectors();
            for (int i = 0; i < group.Count; i++)
            {
                KimodoMarkerSampleResult source = group[i];
                KimodoConstraintMask sourceMask = KimodoConstraintMask.Resolve(source?.mask, source?.constraintType);
                if (source?.effectors == null) continue;
                if (sourceMask.leftHand && source.effectors.hands?.left != null)
                    merged.effectors.hands.left = source.effectors.hands.left.Clone();
                if (sourceMask.rightHand && source.effectors.hands?.right != null)
                    merged.effectors.hands.right = source.effectors.hands.right.Clone();
                if (sourceMask.leftFoot && source.effectors.feet?.left != null)
                    merged.effectors.feet.left = source.effectors.feet.left.Clone();
                if (sourceMask.rightFoot && source.effectors.feet?.right != null)
                    merged.effectors.feet.right = source.effectors.feet.right.Clone();
            }
            CopyRoot2DOverride(group, merged);
            if (hasCanonicalPose)
            {
                KimodoSampleResultPoseUtility.TryEncode(
                    merged,
                    BuildUnifiedAuthoredPose(group, canonical),
                    out _);
            }
            else
            {
                merged.enableMask.muscle49 = false;
                merged.enableMask.rootTQ = false;
                merged.enableMask.leftFootTQ = false;
                merged.enableMask.rightFootTQ = false;
            }
            return merged;
        }

        private static CharacterPose BuildUnifiedAuthoredPose(
            List<KimodoMarkerSampleResult> samples,
            CharacterPose canonical)
        {
            CharacterPose authored = canonical?.Clone();
            if (authored == null || samples == null) return authored;
            // The FullBody root is authored independently from the resolved
            // Root2D result. Keep it intact when importing legacy records.
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source?.mask, source?.constraintType);
                if (TryGetPose(source, out CharacterPose sourcePose, out _) &&
                    sourcePose.root != null && mask.muscle)
                {
                    authored.root.t = sourcePose.root.t;
                    authored.root.q = sourcePose.root.q;
                    break;
                }
            }
            return authored;
        }

        private static void CopyRoot2DOverride(
            List<KimodoMarkerSampleResult> samples,
            KimodoMarkerSampleResult destination)
        {
            if (samples == null || destination == null) return;
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                if (source == null || (!IsRoot2D(source) && source.enableMask?.root2DPosition != true)) continue;
                CharacterPoseTransform root = ResolveRoot2DOverride(source);
                if (root == null) continue;
                destination.root2DOverride = new CharacterPoseTransform { t = root.t, q = root.q };
                destination.enableMask.root2DPosition = true;
            }
        }

        private static int SeedPriority(KimodoMarkerSampleResult sample)
        {
            if (sample == null) return -1;
            bool hasPose = TryGetPose(sample, out _, out _);
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
                if (TryGetPose(sample, out _, out _) && mask.muscle)
                {
                    seed = sample;
                }
            }
            if (seed == null)
            {
                for (int i = 0; i < samples.Count && seed == null; i++)
                {
                    if (TryGetPose(samples[i], out _, out _)) seed = samples[i];
                }
            }
            CharacterPose composed = TryGetPose(seed, out CharacterPose seedPose, out _)
                ? seedPose.Clone()
                : new CharacterPose();

            // Root2D modifies only the root transport payload. Effector targets are
            // independent scene-space data and are merged separately.
            CopyRoot2D(samples, composed);
            return composed;
        }

        private static bool CopyRoot2D(List<KimodoMarkerSampleResult> samples, CharacterPose target)
        {
            bool changed = false;
            // The seed pose is the FullBody base. A unified marker's separate
            // Root2D values (or a legacy root2d record) are an internal planar
            // parent transform: Root2D * FullBody(with X/Z and yaw cleared).
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                if (source == null) continue;
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source.mask, source.constraintType);
                if (!IsRoot2D(source) && source.enableMask?.root2DPosition != true) continue;
                CharacterPoseTransform root = ResolveRoot2DOverride(source);
                if (root == null) continue;
                if (mask.rootPosition)
                {
                    Quaternion overrideYaw = PlanarRotation(root.q);
                    target.root.t = new Vector3(root.t.x, 0f, root.t.z) +
                        overrideYaw * new Vector3(0f, target.root.t.y, 0f);
                    changed = true;
                }
                if (mask.rootHeading && source.enableMask?.root2DHeading == true)
                {
                    Quaternion fullBodyYaw = PlanarRotation(target.root.q);
                    target.root.q = PlanarRotation(root.q) * Quaternion.Inverse(fullBodyYaw) * target.root.q;
                    changed = true;
                }
            }
            return changed;
        }

        private static Quaternion PlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward, Vector3.up)
                : Quaternion.identity;
        }

        private static CharacterPoseTransform ResolveRoot2DOverride(KimodoMarkerSampleResult sample)
        {
            if (sample == null) return null;
            if (sample.enableMask?.root2DPosition == true && sample.root2DOverride != null)
            {
                return sample.root2DOverride;
            }
            // Root2D is an explicit world-space channel. Never fall back to
            // sampleData.rootTQ/CharacterPose.root: rootTQ is not hips world
            // data and must remain independent of Root2D.
            return null;
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
                root2D.enableMask.root2DHeading = mask.rootHeading && source.enableMask?.root2DHeading == true;
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
            if (hasCanonicalPose)
            {
                KimodoSampleResultPoseUtility.TryEncode(sample, canonical.Clone(), out _);
            }
            else
            {
                sample.enableMask.muscle49 = false;
                sample.enableMask.rootTQ = false;
                sample.enableMask.leftFootTQ = false;
                sample.enableMask.rightFootTQ = false;
            }
            if (string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase) &&
                source.enableMask?.root2DPosition == true && source.root2DOverride != null &&
                TryGetPose(sample, out CharacterPose protocolPose, out _))
            {
                protocolPose.root.t = source.root2DOverride.t;
                protocolPose.root.q = source.root2DOverride.q;
                KimodoSampleResultPoseUtility.TryEncode(sample, protocolPose, out _);
            }
            sample.mask = KimodoConstraintMask.ForType(type);
            output.Add(sample);
            return sample;
        }

        private static bool TryGetPose(
            KimodoMarkerSampleResult sample,
            out CharacterPose pose,
            out string error)
        {
            return KimodoSampleResultPoseUtility.TryDecode(sample, out pose, out error);
        }

    }
}
