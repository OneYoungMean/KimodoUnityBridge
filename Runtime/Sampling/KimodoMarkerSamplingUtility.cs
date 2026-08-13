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
            KimodoConstraintMarkerBase marker,
            KimodoMarkerSampleResult sample)
        {
            if (marker == null || !marker.constraintEnabled || sample == null)
            {
                return null;
            }

            KimodoMarkerSampleResult cloned = sample.Clone();
            cloned.sampleTime = marker.time;
            cloned.mask = marker is KimodoConstraintMarker
                ? KimodoConstraintMask.Resolve(marker.SampleData?.mask, marker.ConstraintType).Clone()
                : KimodoConstraintMask.Resolve(cloned.mask, cloned.constraintType);
            if (cloned.jointNames == null)
            {
                cloned.jointNames = new List<string>();
            }

            if (marker is KimodoRoot2DConstraintMarker)
            {
                bool hasHeading = marker.SampleData != null && marker.SampleData.hasRootHeading;
                cloned.hasRootHeading = hasHeading;
                if (!hasHeading)
                {
                    cloned.rootHeading = Vector2.right;
                }

                cloned.localAxisAngles = new List<Vector3>();
                cloned.sampledJointIndices = new List<int>();
            }
            else if (marker is KimodoEndEffectorConstraintMarker)
            {
                string fixedJointName = ResolveFixedEndEffectorJointName(marker.ConstraintType);
                if (!string.IsNullOrEmpty(fixedJointName))
                {
                    // Fixed marker classes map to backend constraint classes whose
                    // canonical names are model-independent. Do not leak profile
                    // skeleton names or stale custom names into the JSON payload.
                    cloned.jointNames = new List<string> { fixedJointName };
                }
                else
                {
                    List<string> configured = sample.jointNames != null && sample.jointNames.Count > 0
                        ? sample.jointNames
                        : marker.SampleData != null && marker.SampleData.jointNames != null
                            ? marker.SampleData.jointNames
                            : null;
                    if (configured == null || configured.Count == 0)
                    {
                        return null;
                    }

                    cloned.jointNames = new List<string>(configured);
                }
            }

            cloned.constraintType = marker.ConstraintType;
            if (marker is KimodoConstraintMarker)
            {
                // A unified marker keeps its enabled channels; protocol records
                // are expanded only at export time.
                cloned.constraintType = "constraint";
            }
            cloned.hasRootHeading = marker is KimodoConstraintMarker
                ? cloned.mask.rootHeading && marker.SampleData.hasRootHeading
                : marker is KimodoRoot2DConstraintMarker && cloned.hasRootHeading;
            // These lists are retained only for legacy-marker migration.  New
            // unified samples are exported from characterPose and need none.
            if (marker is not KimodoConstraintMarker)
            {
                cloned.localAxisAngles ??= new List<Vector3>();
                cloned.sampledJointIndices ??= new List<int>();
                cloned.jointNames ??= new List<string>();
            }
            return cloned;
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
            KimodoConstraintMarkerBase marker,
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

            error = marker is KimodoEndEffectorConstraintMarker
                ? $"failed to normalize sample: end-effector marker '{marker.ConstraintType}' is missing jointNames"
                : "failed to normalize sample";
            return false;
        }

        public static KimodoMarkerSampleResult CreateDefaultMarkerSample(
            string modelName,
            Transform profileSkeletonRoot,
            string constraintType = "fullbody")
        {
            KimodoConstraintRigType resolvedRigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            string[] resolvedJointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            int jointCount = resolvedJointNames != null ? resolvedJointNames.Length : 0;

            var localAxes = new List<Vector3>(jointCount);
            var sampledIndices = new List<int>(jointCount);
            for (int i = 0; i < jointCount; i++)
            {
                localAxes.Add(Vector3.zero);
                sampledIndices.Add(i);
            }

            Vector3 kimodoRootPosition = Vector3.zero;
            Vector3 unityRootPosition = profileSkeletonRoot != null ? profileSkeletonRoot.position : Vector3.zero;
            if (profileSkeletonRoot != null)
            {
                string rootJointName = KimodoRigProfileDatabase.GetProfileRootJointNameForModel(modelName);
                Transform rootJoint = KimodoRetargetAvatarUtility.FindTransformByName(profileSkeletonRoot, rootJointName);
                if (rootJoint != null)
                {
                    kimodoRootPosition = rootJoint.position;
                }
            }

            string resolvedConstraintType = string.IsNullOrWhiteSpace(constraintType) ? "fullbody" : constraintType;
            return new KimodoMarkerSampleResult
            {
                constraintType = resolvedConstraintType,
                sampleTime = 0d,
                rigType = resolvedRigType,
                mask = KimodoConstraintMask.ForType(resolvedConstraintType),
                hasRootHeading = true,
                kimodoRootPosition = kimodoRootPosition,
                rootHeading = Vector2.right,
                unityRootPos = unityRootPosition,
                unityRootRot = Quaternion.identity,
                jointNames = resolvedJointNames != null ? new List<string>(resolvedJointNames) : new List<string>(),
                localAxisAngles = localAxes,
                sampledJointIndices = sampledIndices
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
            KimodoConstraintMarkerBase marker,
            string modelName)
        {
            if (marker == null)
            {
                return new List<string>();
            }

            List<string> names = marker.SampleData != null ? marker.SampleData.jointNames : null;
            return BuildHighlightJointsForConstraint(marker.ConstraintType, names, modelName);
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

            Vector3 kimodoRootPosition = profileRootJoint.position;
            Vector3 unityRootPos = unityRoot.position;
            Quaternion unityRootRot = unityRoot.rotation;

            Vector3 forward = profileRootJoint.forward;
            Vector2 unityHeading = new Vector2(forward.x, forward.z);
            if (unityHeading.sqrMagnitude <= 1e-8f)
            {
                unityHeading = new Vector2(1f, 0f);
            }
            else
            {
                unityHeading.Normalize();
            }

            Quaternion[] worldRots = new Quaternion[joints.Length];
            for (int i = 0; i < joints.Length; i++)
            {
                worldRots[i] = joints[i] != null ? joints[i].rotation : Quaternion.identity;
            }

            var unityLocalAxisAngles = new List<Vector3>(joints.Length);
            var sampledJointIndices = new List<int>(joints.Length);
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i] == null)
                {
                    unityLocalAxisAngles.Add(Vector3.zero);
                    continue;
                }

                int parent = parentIndices[i];
                if (parent >= 0 && (parent >= joints.Length || joints[parent] == null))
                {
                    // Parent unresolved for this profile slot; skip this joint to avoid invalid local rotation.
                    unityLocalAxisAngles.Add(Vector3.zero);
                    continue;
                }

                Quaternion local = parent >= 0 && parent < worldRots.Length
                    ? Quaternion.Inverse(worldRots[parent]) * worldRots[i]
                    : worldRots[i];
                unityLocalAxisAngles.Add(KimodoRuntimeUtility.QuaternionToAxisAngleVector(local));
                sampledJointIndices.Add(i);
            }

            result = new KimodoMarkerSampleResult
            {
                constraintType = markerType ?? string.Empty,
                sampleTime = globalTime,
                rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName),
                mask = KimodoConstraintMask.ForType(markerType),
                hasRootHeading = true,
                kimodoRootPosition = kimodoRootPosition,
                rootHeading = unityHeading,
                unityRootPos = unityRootPos,
                unityRootRot = unityRootRot,
                jointNames = jointNames != null ? new List<string>(jointNames) : new List<string>(),
                localAxisAngles = unityLocalAxisAngles,
                sampledJointIndices = sampledJointIndices
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
                    result.hasEndEffectorTargetPosition = true;
                    result.endEffectorTargetPositionRootLocal = Quaternion.Inverse(profileRootJoint.rotation) *
                        (endEffector.position - profileRootJoint.position);
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

