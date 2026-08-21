using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge
{
    public static class KimodoMarkerSamplingUtility
    {
        public static KimodoMarkerSampleResult NormalizeConstraintMarkerSample(
            KimodoConstraintMarker marker,
            KimodoMarkerSampleResult sample)
        {
            if (marker == null || !marker.constraintEnabled || sample == null)
            {
                return null;
            }

            KimodoMarkerSampleResult authored = marker.SampleData;
            KimodoMarkerSampleResult normalized = authored?.Clone() ?? new KimodoMarkerSampleResult();
            normalized.sampleTime = marker.time;
            normalized.constraintType = "constraint";
            normalized.constraintMode = marker.ConstraintMode == KimodoConstraintMode.Root2D
                ? "root2d"
                : marker.ConstraintMode == KimodoConstraintMode.Effector ? "effector" : "fullbody";
            normalized.enabled = marker.constraintEnabled;
            normalized.mask = KimodoConstraintMask.Resolve(authored?.mask, "constraint").Clone();
            normalized.validMask.root2DHeading = authored?.validMask?.root2DHeading == true;

            if (KimodoSampleDataLayout.IsValidLength(sample.sampleData) && sample.validMask?.Any == true)
            {
                normalized.sampleData = (float[])sample.sampleData.Clone();
                normalized.validMask = sample.validMask?.Clone() ?? new KimodoSampleChannelMask();
            }
            else if (KimodoSampleResultPoseUtility.TryDecode(sample, out CharacterAnimationCli.Unity.CharacterPose migratedPose, out _) &&
                     CharacterPoseMuscleAdapter.TryToSampleData(migratedPose, out float[] migratedData, out _))
            {
                normalized.sampleData = migratedData;
                normalized.validMask = new KimodoSampleChannelMask
                {
                    muscle49 = true,
                    rootTQ = true,
                    leftFootTQ = true,
                    rightFootTQ = true
                };
            }
            normalized.validMask ??= new KimodoSampleChannelMask();
            normalized.validMask.NormalizeDependencies();

            if (marker.autoSample && KimodoSampleResultPoseUtility.TryDecode(sample, out CharacterAnimationCli.Unity.CharacterPose sampledPose, out _))
            {
                CharacterAnimationCli.Unity.CharacterPose normalizedPose = KimodoSampleResultPoseUtility.DecodeOrDefault(normalized);
                switch (marker.ConstraintMode)
                {
                    case KimodoConstraintMode.Root2D:
                        normalizedPose.root = CloneTransform(sampledPose.root);
                        normalized.validMask.root2DPosition = true;
                        normalized.validMask.root2DHeading = marker.Root2DData.allowHeading;
                        normalized.root2DOverride = CloneTransform(normalizedPose.root);
                        break;
                    case KimodoConstraintMode.Effector:
                        normalizedPose.muscles = (float[])sampledPose.muscles.Clone();
                        normalizedPose.root = CloneTransform(sampledPose.root);
                        CopyUnenabledGoals(normalized, sample, normalized.mask);
                        break;
                    default:
                        normalizedPose.muscles = (float[])sampledPose.muscles.Clone();
                        normalizedPose.root = CloneTransform(sampledPose.root);
                        CopyAutoSampleGoals(normalized, sample, normalized.mask, overwriteAll: true);
                        break;
                }
                KimodoSampleResultPoseUtility.TryEncode(normalized, normalizedPose, out _);
            }

            return normalized;
        }

        private static void CopyUnenabledGoals(
            KimodoMarkerSampleResult destination,
            KimodoMarkerSampleResult source,
            KimodoConstraintMask mask)
        {
            CopyAutoSampleGoals(destination, source, mask, overwriteAll: false);
        }

        private static void CopyAutoSampleGoals(
            KimodoMarkerSampleResult destination,
            KimodoMarkerSampleResult source,
            KimodoConstraintMask mask,
            bool overwriteAll)
        {
            if (!KimodoSampleResultPoseUtility.TryDecode(destination, out CharacterAnimationCli.Unity.CharacterPose destinationPose, out _) ||
                !KimodoSampleResultPoseUtility.TryDecode(source, out CharacterAnimationCli.Unity.CharacterPose sourcePose, out _)) return;
            mask ??= new KimodoConstraintMask();
            if ((overwriteAll || !mask.leftHand) && sourcePose.hands?.left != null) destination.effectors.hands.left = CloneTransform(sourcePose.hands.left);
            if ((overwriteAll || !mask.rightHand) && sourcePose.hands?.right != null) destination.effectors.hands.right = CloneTransform(sourcePose.hands.right);
            if ((overwriteAll || !mask.leftFoot) && sourcePose.feet?.left != null) destination.effectors.feet.left = CloneTransform(sourcePose.feet.left);
            if ((overwriteAll || !mask.rightFoot) && sourcePose.feet?.right != null) destination.effectors.feet.right = CloneTransform(sourcePose.feet.right);
            destinationPose.hands = new CharacterAnimationCli.Unity.CharacterPoseSides { left = CloneTransform(destination.effectors.hands.left), right = CloneTransform(destination.effectors.hands.right) };
            destinationPose.feet = new CharacterAnimationCli.Unity.CharacterPoseSides { left = CloneTransform(destination.effectors.feet.left), right = CloneTransform(destination.effectors.feet.right) };
            KimodoSampleResultPoseUtility.TryEncode(destination, destinationPose, out _);
        }

        private static CharacterAnimationCli.Unity.CharacterPoseTransform CloneTransform(
            CharacterAnimationCli.Unity.CharacterPoseTransform value)
        {
            return value != null
                ? new CharacterAnimationCli.Unity.CharacterPoseTransform { t = value.t, q = value.q }
                : new CharacterAnimationCli.Unity.CharacterPoseTransform();
        }

        private static string ResolveFixedEndEffectorJointName(string constraintType)
        {
            switch ((constraintType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "left-hand":
                    return "LeftHand";
                case "right-hand":
                    return "RightHand";
                case "left-foot":
                    return "LeftFoot";
                case "right-foot":
                    return "RightFoot";
                default:
                    return string.Empty;
            }
        }

        public static bool TryNormalizeConstraintMarkerSample(
            KimodoConstraintMarker marker,
            KimodoMarkerSampleResult sample,
            out KimodoMarkerSampleResult normalized,
            out string error)
        {
            error = string.Empty;
            normalized = NormalizeConstraintMarkerSample(marker, sample);
            if (normalized != null)
            {
                return true;
            }

            error = "failed to normalize unified constraint sample";
            return false;
        }

        public static KimodoMarkerSampleResult CreateDefaultMarkerSample(
            string modelName,
            Transform profileSkeletonRoot,
            string constraintType = "fullbody")
        {
            var pose = new CharacterAnimationCli.Unity.CharacterPose();
            if (profileSkeletonRoot != null)
            {
                pose.root.t = profileSkeletonRoot.position;
                pose.root.q = profileSkeletonRoot.rotation;
            }
            var result = new KimodoMarkerSampleResult
            {
                validMask = new KimodoSampleChannelMask
                {
                    muscle49 = true,
                    rootTQ = true,
                    leftFootTQ = true,
                    rightFootTQ = true
                },
                constraintType = "constraint",
                sampleTime = 0d,
                mask = KimodoConstraintMask.Resolve(null, "constraint")
            };
            KimodoSampleResultPoseUtility.TryEncode(result, pose, out _);
            return result;
        }

        internal static void ComposeCharacterPosesAtSameFrame(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            KimodoConstraintSampleComposer.ComposeCharacterPosesAtSameFrame(samples, frameRate);
        }

        /// <summary>Expands the single-marker representation into the unchanged
        /// QuickServer protocol families after all same-frame channels have been
        /// resolved to one canonical pose.</summary>
        public static List<KimodoMarkerSampleResult> ExpandUnifiedConstraintSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            return KimodoConstraintSampleComposer.ExpandProtocolSamples(samples, frameRate);
        }

        public static List<KimodoMarkerSampleResult> MergeAsUnifiedConstraintSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            return KimodoConstraintSampleComposer.MergeAsUnifiedSamples(samples, frameRate);
        }

        private static void CopyTransform(
            CharacterAnimationCli.Unity.CharacterPoseTransform source,
            CharacterAnimationCli.Unity.CharacterPoseTransform destination)
        {
            destination.t = source.t;
            destination.q = source.q;
        }

        public static List<string> BuildHighlightJointsForConstraint(
            string constraintType,
            List<string> jointNames,
            string modelName)
        {
            var output = new List<string>();
            string profileRootJointName = KimodoRigProfileDatabase.GetProfileRootJointNameForModel(modelName);
            if (!string.IsNullOrWhiteSpace(profileRootJointName))
            {
                output.Add(profileRootJointName);
            }

            if (string.Equals(constraintType, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                return output;
            }

            if (string.Equals(constraintType, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                string[] modelJointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
                if (modelJointNames != null)
                {
                    for (int i = 0; i < modelJointNames.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(modelJointNames[i]))
                        {
                            output.Add(modelJointNames[i]);
                        }
                    }
                }

                return output;
            }

            if (jointNames == null)
            {
                return output;
            }

            for (int i = 0; i < jointNames.Count; i++)
            {
                string name = jointNames[i];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    output.Add(name.Trim());
                }
            }

            return output;
        }

        public static List<string> BuildHighlightJointsForMarker(
            KimodoConstraintMarker marker,
            string modelName)
        {
            if (marker == null)
            {
                return new List<string>();
            }

            switch (marker.ConstraintMode)
            {
                case KimodoConstraintMode.Root2D:
                    return BuildHighlightJointsForConstraint("root2d", null, modelName);
                case KimodoConstraintMode.Effector:
                    return BuildHighlightJointsForConstraint(
                        "left-hand",
                        new List<string> { "LeftHand", "RightHand", "LeftFoot", "RightFoot" },
                        modelName);
                default:
                    return BuildHighlightJointsForConstraint("fullbody", null, modelName);
            }
        }

        public static bool TryResolveAnimationClipFromTimelineClip(
            TimelineClip timelineClip,
            out AnimationClip animationClip,
            out string error)
        {
            animationClip = null;
            error = string.Empty;

            if (!(timelineClip?.asset is AnimationPlayableAsset playableAsset) || playableAsset.clip == null)
            {
                error = "Source timeline clip does not contain an AnimationClip.";
                return false;
            }

            animationClip = playableAsset.clip;
            return true;
        }

        /// <summary>
        /// Resolves a Timeline-global time to the underlying AnimationClip
        /// evaluation time. The conversion is deliberately kept at this
        /// source-sampling boundary; markers never store this value.
        /// </summary>
        public static double ResolveAnimationSourceTime(TimelineClip timelineClip, double timelineTime)
        {
            if (timelineClip == null)
            {
                return Math.Max(0.0, timelineTime);
            }

            double clipRelativeTime = timelineClip.ToLocalTime(timelineTime);
            clipRelativeTime = Math.Max(0.0, Math.Min(timelineClip.duration, clipRelativeTime));
            double sourceSampleTime = timelineClip.clipIn + (clipRelativeTime * timelineClip.timeScale);
            if (sourceSampleTime < 0.0)
            {
                return 0.0;
            }

            return sourceSampleTime;
        }

        internal static bool TrySampleMarkerFromProfileSkeletonRaw(
            Animator animator,
            Transform skeletonRoot,
            string modelName,
            double globalTime,
            string markerType,
            string[] jointNamesOverride,
            int[] parentIndicesOverride,
            Transform[] jointsOverride,
            out KimodoMarkerSampleResult result,
            out string error,
            Transform endEffectorOverride = null)
        {
            result = null;
            error = string.Empty;

            Transform unityRoot = skeletonRoot != null ? skeletonRoot : (animator != null ? animator.transform : null);
            if (unityRoot == null)
            {
                error = "Skeleton root is null.";
                return false;
            }

            string[] jointNames = jointNamesOverride;
            int[] parentIndices = parentIndicesOverride;
            Transform[] joints = jointsOverride;
            if (jointNames == null || parentIndices == null || joints == null)
            {
                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        modelName,
                        unityRoot,
                        out jointNames,
                        out parentIndices,
                        out joints,
                        out error))
                {
                    return false;
                }
            }

            if (jointNames == null || parentIndices == null || joints == null)
            {
                error = "Profile skeleton data is incomplete.";
                return false;
            }

            if (jointNames.Length != parentIndices.Length || jointNames.Length != joints.Length)
            {
                error = $"Profile skeleton data length mismatch: names={jointNames.Length}, parents={parentIndices.Length}, joints={joints.Length}.";
                return false;
            }

            Transform profileRootJoint = joints[0];
            if (profileRootJoint == null)
            {
                error = "Profile skeleton root joint is null.";
                return false;
            }

            CharacterAnimationCli.Unity.CharacterPose initialPose = new CharacterAnimationCli.Unity.CharacterPose
            {
                root = new CharacterAnimationCli.Unity.CharacterPoseTransform
                {
                    t = profileRootJoint.position,
                    q = profileRootJoint.rotation
                }
            };
            result = new KimodoMarkerSampleResult
            {
                constraintType = "constraint",
                sampleTime = globalTime,
                mask = KimodoConstraintMask.ForType(markerType),
                validMask = new KimodoSampleChannelMask
                {
                    rootTQ = true,
                    root2DPosition = string.Equals(markerType, "root2d", StringComparison.OrdinalIgnoreCase),
                    root2DHeading = string.Equals(markerType, "root2d", StringComparison.OrdinalIgnoreCase)
                }
            };
            KimodoSampleResultPoseUtility.TryEncode(result, initialPose, out _);

            if (TryResolveEndEffectorBone(markerType, out HumanBodyBones endEffectorBone))
            {
                Transform endEffector = endEffectorOverride;
                if (endEffector == null &&
                    animator != null &&
                    KimodoRetargetCoreUtility.IsValidHumanoid(animator.avatar))
                {
                    try
                    {
                        endEffector = animator.GetBoneTransform(endEffectorBone);
                    }
                    catch (InvalidOperationException)
                    {
                        endEffector = null;
                    }
                }
                if (endEffector != null)
                {
                    CharacterAnimationCli.Unity.CharacterPoseTransform goal = endEffectorBone switch
                    {
                        HumanBodyBones.LeftHand => result.effectors.hands.left,
                        HumanBodyBones.RightHand => result.effectors.hands.right,
                        HumanBodyBones.LeftFoot => result.effectors.feet.left,
                        HumanBodyBones.RightFoot => result.effectors.feet.right,
                        _ => null
                    };
                    if (goal != null)
                    {
                        goal.t = Quaternion.Inverse(profileRootJoint.rotation) *
                            (endEffector.position - profileRootJoint.position);
                        goal.q = Quaternion.Inverse(profileRootJoint.rotation) * endEffector.rotation;
                    }
                }
            }
            return true;
        }

        internal static bool TryResolveEndEffectorBone(string markerType, out HumanBodyBones bone)
        {
            switch ((markerType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "left-hand":
                    bone = HumanBodyBones.LeftHand;
                    return true;
                case "right-hand":
                    bone = HumanBodyBones.RightHand;
                    return true;
                case "left-foot":
                    bone = HumanBodyBones.LeftFoot;
                    return true;
                case "right-foot":
                    bone = HumanBodyBones.RightFoot;
                    return true;
                default:
                    bone = HumanBodyBones.LastBone;
                    return false;
            }
        }

    }
}

