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
            if (marker == null || sample == null)
            {
                return null;
            }

            KimodoMarkerSampleResult cloned = sample.Clone();
            cloned.constraintType = marker.ConstraintType;
            cloned.sampleTime = marker.time;
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
                List<string> configured = marker.SampleData != null && marker.SampleData.jointNames != null
                    ? marker.SampleData.jointNames
                    : null;
                if (configured == null || configured.Count == 0)
                {
                    configured = new List<string> { "LeftHand" };
                }

                cloned.jointNames = new List<string>(configured);
            }

            cloned.constraintType = marker.ConstraintType;
            cloned.hasRootHeading = marker is KimodoRoot2DConstraintMarker ? cloned.hasRootHeading : false;
            cloned.localAxisAngles ??= new List<Vector3>();
            cloned.sampledJointIndices ??= new List<int>();
            cloned.jointNames ??= new List<string>();
            return cloned;
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

            error = "failed to normalize sample";
            return false;
        }

        public static List<string> BuildHighlightJointsForConstraint(
            string constraintType,
            List<string> jointNames,
            string modelName)
        {
            var output = new List<string>();
            string root = KimodoRigProfileDatabase.GetRootJointNameForModel(modelName);
            if (!string.IsNullOrWhiteSpace(root))
            {
                output.Add(root);
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
            out string error)
        {
            result = null;
            error = string.Empty;

            Transform root = skeletonRoot != null ? skeletonRoot : (animator != null ? animator.transform : null);
            if (root == null)
            {
                error = "Skeleton root is null.";
                return false;
            }

            string[] jointNames = jointNamesOverride;
            int[] parentIndices = parentIndicesOverride;
            Transform[] joints = jointsOverride;
            if (jointNames == null || parentIndices == null || joints == null)
            {
                ResolveProfile(modelName, out jointNames, out parentIndices);
                joints = ResolveJointTransforms(jointNames, root, animator);
            }

            string rootJointName = jointNames != null && jointNames.Length > 0 ? jointNames[0] : "Hips";
            Transform pelvis = joints != null && joints.Length > 0 && joints[0] != null
                ? joints[0]
                : TryResolveTransformByJointName(rootJointName, root, animator) ?? root;

            Vector3 unityRootPosition = pelvis.position;

            Vector3 forward = pelvis.forward;
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

                string jointName = jointNames != null && i < jointNames.Length ? jointNames[i] : string.Empty;
                string normalized = string.IsNullOrWhiteSpace(jointName) ? string.Empty : jointName.Trim().ToLowerInvariant();
                bool useWorldRotation = normalized == "hips"
                    || normalized == "pelvis"
                    || normalized == "left_hip"
                    || normalized == "right_hip"
                    || normalized == "left_hip_pitch_skel"
                    || normalized == "right_hip_pitch_skel";

                Quaternion local;
                if (useWorldRotation)
                {
                    local = worldRots[i];
                }
                else
                {
                    local = parent >= 0 && parent < worldRots.Length
                        ? Quaternion.Inverse(worldRots[parent]) * worldRots[i]
                        : worldRots[i];
                }
                unityLocalAxisAngles.Add(KimodoRuntimeUtility.QuaternionToAxisAngleVector(local));
                sampledJointIndices.Add(i);
            }

            result = new KimodoMarkerSampleResult
            {
                constraintType = markerType ?? string.Empty,
                sampleTime = globalTime,
                rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName),
                hasRootHeading = true,
                rootPosition = unityRootPosition,
                rootHeading = unityHeading,
                jointNames = jointNames != null ? new List<string>(jointNames) : new List<string>(),
                localAxisAngles = unityLocalAxisAngles,
                sampledJointIndices = sampledJointIndices
            };
            return true;
        }

        private static void ResolveProfile(string modelName, out string[] jointNames, out int[] parentIndices)
        {
            KimodoRigProfileDatabase.ResolveProfile(modelName, out _, out jointNames, out parentIndices);
        }

        private static Transform[] ResolveJointTransforms(string[] names, Transform root, Animator animator)
        {
            int count = names != null ? names.Length : 0;
            var transforms = new Transform[count];
            if (root == null || count == 0)
            {
                return transforms;
            }

            for (int i = 0; i < count; i++)
            {
                string name = names[i];
                transforms[i] = TryResolveTransformByJointName(name, root, animator);
            }

            return transforms;
        }

        private static Transform TryResolveTransformByJointName(string jointName, Transform searchRoot, Animator animator)
        {
            if (searchRoot == null || string.IsNullOrWhiteSpace(jointName))
            {
                return null;
            }

            Transform byHuman = TryResolveViaHumanBone(jointName, animator);
            if (byHuman != null)
            {
                return byHuman;
            }

            return KimodoRetargetAvatarUtility.FindTransformByName(searchRoot, jointName);
        }

        private static Transform TryResolveViaHumanBone(string jointName, Animator animator)
        {
            if (animator == null || !animator.isHuman)
            {
                return null;
            }

            bool hasUpperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest) != null;
            switch (jointName)
            {
                case "Hips": return animator.GetBoneTransform(HumanBodyBones.Hips);
                case "Spine1": return animator.GetBoneTransform(HumanBodyBones.Spine);
                case "Spine2": return animator.GetBoneTransform(HumanBodyBones.Chest);
                case "Chest":
                    return hasUpperChest
                        ? animator.GetBoneTransform(HumanBodyBones.UpperChest)
                        : animator.GetBoneTransform(HumanBodyBones.Chest);
                case "Neck1": return animator.GetBoneTransform(HumanBodyBones.Neck);
                case "Neck2": return animator.GetBoneTransform(HumanBodyBones.Neck);
                case "Head": return animator.GetBoneTransform(HumanBodyBones.Head);
                case "Jaw": return animator.GetBoneTransform(HumanBodyBones.Jaw);
                case "LeftEye": return animator.GetBoneTransform(HumanBodyBones.LeftEye);
                case "RightEye": return animator.GetBoneTransform(HumanBodyBones.RightEye);
                case "LeftShoulder": return animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
                case "LeftArm": return animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                case "LeftForeArm": return animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                case "LeftHand": return animator.GetBoneTransform(HumanBodyBones.LeftHand);
                case "LeftHandThumbEnd": return animator.GetBoneTransform(HumanBodyBones.LeftThumbDistal);
                case "LeftHandMiddleEnd": return animator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal);
                case "RightShoulder": return animator.GetBoneTransform(HumanBodyBones.RightShoulder);
                case "RightArm": return animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                case "RightForeArm": return animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                case "RightHand": return animator.GetBoneTransform(HumanBodyBones.RightHand);
                case "RightHandThumbEnd": return animator.GetBoneTransform(HumanBodyBones.RightThumbDistal);
                case "RightHandMiddleEnd": return animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
                case "LeftLeg": return animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                case "LeftShin": return animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                case "LeftFoot": return animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                case "LeftToeBase": return animator.GetBoneTransform(HumanBodyBones.LeftToes);
                case "RightLeg": return animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                case "RightShin": return animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                case "RightFoot": return animator.GetBoneTransform(HumanBodyBones.RightFoot);
                case "RightToeBase": return animator.GetBoneTransform(HumanBodyBones.RightToes);
                case "pelvis": return animator.GetBoneTransform(HumanBodyBones.Hips);
                case "spine1": return animator.GetBoneTransform(HumanBodyBones.Spine);
                case "spine2": return animator.GetBoneTransform(HumanBodyBones.Chest);
                case "spine3":
                    return hasUpperChest
                        ? animator.GetBoneTransform(HumanBodyBones.UpperChest)
                        : animator.GetBoneTransform(HumanBodyBones.Chest);
                case "neck": return animator.GetBoneTransform(HumanBodyBones.Neck);
                case "head": return animator.GetBoneTransform(HumanBodyBones.Head);
                case "left_hip": return animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                case "left_knee": return animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                case "left_ankle": return animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                case "left_foot": return animator.GetBoneTransform(HumanBodyBones.LeftToes);
                case "right_hip": return animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                case "right_knee": return animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                case "right_ankle": return animator.GetBoneTransform(HumanBodyBones.RightFoot);
                case "right_foot": return animator.GetBoneTransform(HumanBodyBones.RightToes);
                case "left_collar": return animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
                case "left_shoulder": return animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                case "left_elbow": return animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                case "left_wrist": return animator.GetBoneTransform(HumanBodyBones.LeftHand);
                case "right_collar": return animator.GetBoneTransform(HumanBodyBones.RightShoulder);
                case "right_shoulder": return animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                case "right_elbow": return animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                case "right_wrist": return animator.GetBoneTransform(HumanBodyBones.RightHand);
                default: return null;
            }
        }

    }
}

