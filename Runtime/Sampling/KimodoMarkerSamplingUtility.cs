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
            normalized.mask = KimodoConstraintMask.Resolve(authored?.mask, "constraint").Clone();
            normalized.hasRootHeading = authored != null && authored.hasRootHeading;

            if (marker.autoSampleFullBody && sample.characterPose != null)
            {
                normalized.characterPose ??= sample.characterPose.Clone();
                normalized.characterPose.hands ??= new CharacterAnimationCli.Unity.CharacterPoseSides();
                normalized.characterPose.feet ??= new CharacterAnimationCli.Unity.CharacterPoseSides();
                normalized.characterPose.muscles = sample.characterPose.muscles != null
                    ? (float[])sample.characterPose.muscles.Clone()
                    : normalized.characterPose.muscles;
                if (sample.characterPose.root != null)
                {
                    normalized.characterPose.root = new CharacterAnimationCli.Unity.CharacterPoseTransform
                    {
                        t = sample.characterPose.root.t,
                        q = sample.characterPose.root.q
                    };
                }
                if (!normalized.mask.leftHand && sample.characterPose.hands?.left != null)
                {
                    normalized.characterPose.hands.left = new CharacterAnimationCli.Unity.CharacterPoseTransform
                    {
                        t = sample.characterPose.hands.left.t,
                        q = sample.characterPose.hands.left.q
                    };
                }
                if (!normalized.mask.rightHand && sample.characterPose.hands?.right != null)
                {
                    normalized.characterPose.hands.right = new CharacterAnimationCli.Unity.CharacterPoseTransform
                    {
                        t = sample.characterPose.hands.right.t,
                        q = sample.characterPose.hands.right.q
                    };
                }
                if (!normalized.mask.leftFoot && sample.characterPose.feet?.left != null)
                {
                    normalized.characterPose.feet.left = new CharacterAnimationCli.Unity.CharacterPoseTransform
                    {
                        t = sample.characterPose.feet.left.t,
                        q = sample.characterPose.feet.left.q
                    };
                }
                if (!normalized.mask.rightFoot && sample.characterPose.feet?.right != null)
                {
                    normalized.characterPose.feet.right = new CharacterAnimationCli.Unity.CharacterPoseTransform
                    {
                        t = sample.characterPose.feet.right.t,
                        q = sample.characterPose.feet.right.q
                    };
                }
            }

            if (marker.autoSampleRoot2D && sample.hasRoot2DOverride && sample.root2DOverride != null)
            {
                normalized.root2DOverride = new CharacterAnimationCli.Unity.CharacterPoseTransform
                {
                    t = sample.root2DOverride.t,
                    q = sample.root2DOverride.q
                };
                normalized.hasRoot2DOverride = true;
            }
            else if (marker.autoSampleRoot2D && sample.characterPose?.root != null)
            {
                Vector3 forward = Vector3.ProjectOnPlane(
                    sample.characterPose.root.q * Vector3.forward,
                    Vector3.up);
                normalized.root2DOverride = new CharacterAnimationCli.Unity.CharacterPoseTransform
                {
                    t = new Vector3(sample.characterPose.root.t.x, 0f, sample.characterPose.root.t.z),
                    q = forward.sqrMagnitude > 1e-8f
                        ? Quaternion.LookRotation(forward, Vector3.up)
                        : Quaternion.identity
                };
                normalized.hasRoot2DOverride = true;
            }
            return normalized;
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
            return new KimodoMarkerSampleResult
            {
                characterPose = pose,
                constraintType = "constraint",
                sampleTime = 0d,
                mask = KimodoConstraintMask.Resolve(null, "constraint"),
                hasRootHeading = true
            };
        }

        internal static void ComposeCharacterPosesAtSameFrame(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            KimodoConstraintSampleResolver.ComposeCharacterPosesAtSameFrame(samples, frameRate);
        }

        /// <summary>Expands the single-marker representation into the unchanged
        /// QuickServer protocol families after all same-frame channels have been
        /// resolved to one canonical pose.</summary>
        public static List<KimodoMarkerSampleResult> ExpandUnifiedConstraintSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            return KimodoConstraintSampleResolver.ExpandProtocolSamples(samples, frameRate);
        }

        public static List<KimodoMarkerSampleResult> MergeAsUnifiedConstraintSamples(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double frameRate)
        {
            return KimodoConstraintSampleResolver.MergeAsUnifiedSamples(samples, frameRate);
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

            KimodoConstraintMask mask = KimodoConstraintMask.Resolve(marker.SampleData?.mask, "constraint");
            return BuildHighlightJointsForConstraint(mask.muscle ? "fullbody" : "root2d", null, modelName);
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

        public static double ClampLocalSampleTime(TimelineClip timelineClip, double globalTime)
        {
            if (timelineClip == null)
            {
                return Math.Max(0.0, globalTime);
            }

            double localSampleTime = timelineClip.ToLocalTime(globalTime);
            if (localSampleTime < 0.0)
            {
                return 0.0;
            }

            if (localSampleTime > timelineClip.duration)
            {
                return timelineClip.duration;
            }

            return localSampleTime;
        }

        public static double ResolveSourceClipSampleTime(TimelineClip timelineClip, double globalTime)
        {
            if (timelineClip == null)
            {
                return Math.Max(0.0, globalTime);
            }

            double localSampleTime = ClampLocalSampleTime(timelineClip, globalTime);
            double sourceSampleTime = timelineClip.clipIn + (localSampleTime * timelineClip.timeScale);
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

            result = new KimodoMarkerSampleResult
            {
                characterPose = new CharacterAnimationCli.Unity.CharacterPose
                {
                    root = new CharacterAnimationCli.Unity.CharacterPoseTransform
                    {
                        t = profileRootJoint.position,
                        q = profileRootJoint.rotation
                    }
                },
                constraintType = "constraint",
                sampleTime = globalTime,
                mask = KimodoConstraintMask.ForType(markerType),
                hasRootHeading = true
            };

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
                        HumanBodyBones.LeftHand => result.characterPose.hands.left,
                        HumanBodyBones.RightHand => result.characterPose.hands.right,
                        HumanBodyBones.LeftFoot => result.characterPose.feet.left,
                        HumanBodyBones.RightFoot => result.characterPose.feet.right,
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

