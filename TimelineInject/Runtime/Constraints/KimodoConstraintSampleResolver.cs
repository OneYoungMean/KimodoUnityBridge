using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using UnityEngine;

namespace TimelineInject
{
    /// <summary>Runtime-independent canonical constraint composition.  Editor
    /// and runtime convert this result to a rig only at their respective edges.</summary>
    public static class KimodoConstraintSampleResolver
    {
        /// <summary>Returns the effective pose for one unified marker without
        /// mutating its authored FullBody and Root2D data.</summary>
        public static KimodoMarkerSampleResult ResolveUnifiedSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null) return null;
            KimodoMarkerSampleResult resolved = sample.Clone();
            resolved.characterPose = ComposePose(new List<KimodoMarkerSampleResult> { sample });
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
                    hasPose |= group[i].characterPose != null && group[i].characterPose.TryValidate(out _);
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
                        group[i].characterPose = pose.Clone();
                    }
                }
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
            CopyRoot2DOverride(group, merged);
            merged.characterPose = hasCanonicalPose
                ? BuildUnifiedAuthoredPose(group, canonical)
                : null;
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
                if (source?.characterPose?.root != null && mask.muscle)
                {
                    authored.root.t = source.characterPose.root.t;
                    authored.root.q = source.characterPose.root.q;
                    break;
                }
            }
            // Preserve the authored IK values as well. `canonical` has already
            // re-expressed them relative to the resolved Root2D transform;
            // storing those values on the unified marker would make the next
            // resolve shift the targets a second time.
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult source = samples[i];
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(source?.mask, source?.constraintType);
                CharacterPose pose = source?.characterPose;
                if (pose == null) continue;
                if (mask.leftFoot) CopyTransform(pose.feet.left, authored.feet.left);
                if (mask.rightFoot) CopyTransform(pose.feet.right, authored.feet.right);
                if (mask.leftHand) CopyTransform(pose.hands.left, authored.hands.left);
                if (mask.rightHand) CopyTransform(pose.hands.right, authored.hands.right);
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
                if (source == null || (!IsRoot2D(source) && !source.hasRoot2DOverride)) continue;
                CharacterPoseTransform root = ResolveRoot2DOverride(source);
                if (root == null) continue;
                destination.root2DOverride = new CharacterPoseTransform { t = root.t, q = root.q };
                destination.hasRoot2DOverride = true;
            }
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

            // FullBody → Root2D → Foot IK → Hand IK. Root2D is an internal
            // planar transform applied to the FullBody root with X/Z and yaw
            // removed. Only enabled limb goals remain fixed in world space.
            CopyFootGoals(samples, composed);
            CopyHandGoals(samples, composed);
            Vector3[] worldPositions = CaptureWorldGoalPositions(composed);
            Quaternion[] worldRotations = CaptureWorldGoalRotations(composed);
            bool[] worldLockedGoals = CollectWorldLockedGoals(samples);
            if (CopyRoot2D(samples, composed))
            {
                RestoreWorldGoalTransforms(composed, worldPositions, worldRotations, worldLockedGoals);
            }
            return composed;
        }

        private static bool[] CollectWorldLockedGoals(List<KimodoMarkerSampleResult> samples)
        {
            var result = new bool[4];
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(samples[i]?.mask, samples[i]?.constraintType);
                result[0] |= mask.leftHand;
                result[1] |= mask.rightHand;
                result[2] |= mask.leftFoot;
                result[3] |= mask.rightFoot;
            }
            return result;
        }

        private static Vector3[] CaptureWorldGoalPositions(CharacterPose pose)
        {
            Quaternion rootRotation = pose.root.q.normalized;
            return new[]
            {
                pose.root.t + rootRotation * pose.hands.left.t,
                pose.root.t + rootRotation * pose.hands.right.t,
                pose.root.t + rootRotation * pose.feet.left.t,
                pose.root.t + rootRotation * pose.feet.right.t
            };
        }

        private static Quaternion[] CaptureWorldGoalRotations(CharacterPose pose)
        {
            Quaternion rootRotation = pose.root.q.normalized;
            return new[]
            {
                rootRotation * pose.hands.left.q,
                rootRotation * pose.hands.right.q,
                rootRotation * pose.feet.left.q,
                rootRotation * pose.feet.right.q
            };
        }

        private static void RestoreWorldGoalTransforms(
            CharacterPose pose,
            Vector3[] positions,
            Quaternion[] rotations,
            bool[] worldLockedGoals)
        {
            Quaternion inverseRoot = Quaternion.Inverse(pose.root.q.normalized);
            CharacterPoseTransform[] goals = { pose.hands.left, pose.hands.right, pose.feet.left, pose.feet.right };
            for (int i = 0; i < goals.Length; i++)
            {
                if (worldLockedGoals == null || i >= worldLockedGoals.Length || !worldLockedGoals[i]) continue;
                goals[i].t = inverseRoot * (positions[i] - pose.root.t);
                goals[i].q = inverseRoot * rotations[i];
            }
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
                if (!IsRoot2D(source) && !source.hasRoot2DOverride) continue;
                CharacterPoseTransform root = ResolveRoot2DOverride(source);
                if (root == null) continue;
                if (mask.rootPosition)
                {
                    Quaternion overrideYaw = PlanarRotation(root.q);
                    target.root.t = new Vector3(root.t.x, 0f, root.t.z) +
                        overrideYaw * new Vector3(0f, target.root.t.y, 0f);
                    changed = true;
                }
                if (mask.rootHeading && source.hasRootHeading)
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
            if (sample.hasRoot2DOverride && sample.root2DOverride != null) return sample.root2DOverride;
            return sample.characterPose?.root; // Legacy root2d records.
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
            if (string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase) &&
                source.hasRoot2DOverride && source.root2DOverride != null && sample.characterPose != null)
            {
                sample.characterPose.root.t = source.root2DOverride.t;
                sample.characterPose.root.q = source.root2DOverride.q;
            }
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
