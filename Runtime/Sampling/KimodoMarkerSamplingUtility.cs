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
            bool useSampledValues = marker.autoSample;
            KimodoMarkerSampleResult normalized = useSampledValues && sample != null
                ? sample.Clone()
                : authored?.Clone() ?? new KimodoMarkerSampleResult();
            normalized.sampleTime = marker.time;
            normalized.constraintType = "constraint";
            normalized.constraintMode = marker.ConstraintMode == KimodoConstraintMode.Root2D
                ? "root2d"
                : marker.ConstraintMode == KimodoConstraintMode.Effector ? "effector" : "fullbody";
            normalized.enabled = marker.constraintEnabled;
            normalized.mask = KimodoConstraintMask.Resolve(authored?.mask, "constraint").Clone();
            if (!useSampledValues && sample != null &&
                KimodoSampleDataLayout.IsValidLength(sample.sampleData))
            {
                normalized.sampleData = (float[])sample.sampleData.Clone();
            }
            normalized.enableMask ??= new KimodoSampleChannelMask();
            normalized.enableMask.NormalizeDependencies();
            return normalized;
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
            var result = new KimodoMarkerSampleResult
            {
                enableMask = new KimodoSampleChannelMask
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

