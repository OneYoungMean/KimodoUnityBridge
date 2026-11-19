using System;
using System.Collections.Generic;
using KimodoUnityBridge;
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
            KimodoMarkerSampleResult normalized = useSampledValues
                ? sample.Clone()
                : authored?.Clone() ?? new KimodoMarkerSampleResult();
            if (useSampledValues)
            {
                normalized.enableMask = authored?.enableMask?.Clone() ?? new KimodoConstraintMask();
                PreserveAuthoredEffectorSpaces(authored, normalized);
            }
            normalized.sampleTime = marker.time;
            normalized.constraintMode = marker.ConstraintMode == KimodoConstraintMode.Root2D
                ? "root2d"
                : marker.ConstraintMode == KimodoConstraintMode.Effector ? "effector" : "fullbody";
            // constraintMode changes application behavior only. In particular,
            // Root2D keeps the complete sampled rootOverride payload.
            normalized.enabled = marker.constraintEnabled;
            if (!useSampledValues &&
                KimodoSampleDataLayout.IsValid(sample.sampleData))
            {
                normalized.sampleData = sample.sampleData.Clone();
            }
            normalized.enableMask ??= new KimodoConstraintMask();
            return normalized;
        }

        private static void PreserveAuthoredEffectorSpaces(
            KimodoMarkerSampleResult authored,
            KimodoMarkerSampleResult normalized)
        {
            if (authored?.effectors == null || normalized?.effectors == null) return;
            KimodoRigidTransform[] source =
            {
                normalized.effectors.leftHand,
                normalized.effectors.rightHand,
                normalized.effectors.leftFoot,
                normalized.effectors.rightFoot
            };
            KimodoRigidTransform[] authoredValues =
            {
                authored.effectors.leftHand,
                authored.effectors.rightHand,
                authored.effectors.leftFoot,
                authored.effectors.rightFoot
            };
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == null || authoredValues[i] == null) continue;
                KimodoConstraintSpace target = authoredValues[i].space;
                KimodoConstraintSpaceUtility.ConvertSpace(source[i], normalized.rootOverride, target);
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

            error = "failed to normalize constraint sample";
            return false;
        }

        public static KimodoMarkerSampleResult CreateDefaultMarkerSample(
            string modelName,
            Transform profileSkeletonRoot,
            string constraintType = "fullbody")
        {
            var result = new KimodoMarkerSampleResult
            {
                sampleValid = true,
                sampleData = new MuscleSample(),
                enableMask = KimodoConstraintMask.ForType("fullbody"),
                constraintMode = "fullbody",
                sampleTime = 0d,
            };
            result.sampleData.SetRoot(Vector3.zero, Quaternion.identity);
            return result;
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

            // TimelineClip.ToLocalTime already applies the clip start, clipIn,
            // timeScale, looping, and extrapolation rules. Do not apply those
            // factors a second time here.
            double sourceSampleTime = timelineClip.ToLocalTime(timelineTime);
            return Math.Max(0.0, sourceSampleTime);
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

