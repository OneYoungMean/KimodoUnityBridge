using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools
{
    public static class KimodoMarkerSamplingUtility
    {
        public static string[] GetJointNamesForModel(string modelName)
        {
            return KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
        }

        public static string GetRootJointNameForModel(string modelName)
        {
            return KimodoRigProfileDatabase.GetRootJointNameForModel(modelName);
        }

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
            string root = GetRootJointNameForModel(modelName);
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
                string[] modelJointNames = GetJointNamesForModel(modelName);
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

        public static bool TrySampleMarker(
            Animator animator,
            Transform skeletonRoot,
            TimelineClip sourceClip,
            string modelName,
            double globalTime,
            string markerType,
            Avatar originAvatar,
            Avatar targetAvatar,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            AnimationClip animationClip = ExtractAnimationClip(sourceClip);
            if (animationClip != null && KimodoRetargetTools.IsValidHumanoid(originAvatar) && KimodoRetargetTools.IsValidHumanoid(targetAvatar))
            {
                return TrySampleMarkerViaNewRetargetCore(
                    animationClip,
                    markerType,
                    globalTime,
                    originAvatar,
                    targetAvatar,
                    modelName,
                    out result,
                    out error);
            }

            if (originAvatar != null || targetAvatar != null)
            {
                error = "Retarget sampling requires a valid source clip plus both originAvatar and targetAvatar.";
                result = null;
                return false;
            }

            if (animator == null)
            {
                error = "Animator is null.";
                result = null;
                return false;
            }

            Transform root = skeletonRoot != null ? skeletonRoot : animator.transform;
            if (root == null)
            {
                error = "Skeleton root is null.";
                result = null;
                return false;
            }

            return TrySampleMarkerRaw(animator, root, modelName, globalTime, markerType, out result, out error);
        }

        private static bool TrySampleMarkerViaNewRetargetCore(
            AnimationClip sourceClip,
            string markerType,
            double sampleTime,
            Avatar originAvatar,
            Avatar targetAvatar,
            string modelName,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;

            if (!KimodoRetargetTools.IsValidHumanoid(originAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!KimodoRetargetTools.IsValidHumanoid(targetAvatar))
            {
                error = "Target avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!KimodoRetargetTools.TryRetargetNew(
                    sourceClip,
                    originAvatar,
                    targetAvatar,
                    (float)sampleTime,
                    out BoneSample frame,
                    out error))
            {
                return false;
            }

            if (!KimodoRetargetTools.TryRetargetNew(frame, targetAvatar, out MuscleSample muscleFrame, out error))
            {
                return false;
            }

            return TryBuildMarkerSampleResultFromRetargetFrame(frame, muscleFrame != null ? muscleFrame.pose : new HumanPose(), modelName, markerType, sampleTime, out result, out error);
        }

        private static AnimationClip ExtractAnimationClip(TimelineClip sourceClip)
        {
            if (sourceClip?.asset is AnimationPlayableAsset playableAsset)
            {
                return playableAsset.clip;
            }

            return null;
        }

        public static bool TrySampleMarkerRaw(
            Animator animator,
            Transform skeletonRoot,
            string modelName,
            double globalTime,
            string markerType,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;

            if (animator == null)
            {
                error = "Animator is null.";
                return false;
            }

            Transform root = skeletonRoot != null ? skeletonRoot : animator.transform;
            if (root == null)
            {
                error = "Skeleton root is null.";
                return false;
            }

            ResolveProfile(modelName, out string[] jointNames, out int[] parentIndices);
            string rootJointName = jointNames != null && jointNames.Length > 0 ? jointNames[0] : "Hips";
            Transform pelvis = TryResolveTransformByJointName(rootJointName, root, animator) ?? root;

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

            Transform[] joints = ResolveJointTransforms(jointNames, root, animator);
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
                // Keep unresolved joints as null to avoid sampling wrong rotations from a fallback transform.
                transforms[i] = TryResolveTransformByJointName(name, root, animator);
            }

            return transforms;
        }

        private static Transform TryResolveTransformByJointName(string jointName, Transform searchRoot, Animator animator)
        {
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
                // SOMA30 aliases
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

                // SMPLX22 aliases
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

        private static bool TryBuildMarkerSampleResultFromRetargetFrame(
            BoneSample frame,
            HumanPose pose,
            string modelName,
            string markerType,
            double sampleTime,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;

            if (frame == null || frame.boneNames == null || frame.localRotations == null)
            {
                error = "frame data is empty.";
                return false;
            }

            KimodoRigProfileDatabase.ResolveProfile(modelName, out KimodoConstraintRigType rigType, out string[] jointNames, out _);
            if (jointNames == null || jointNames.Length == 0)
            {
                error = $"model joint layout not found for '{modelName}'.";
                return false;
            }

            Dictionary<string, Quaternion> rotationMap = BuildLeafRotationMap(frame);
            if (rotationMap == null || rotationMap.Count == 0)
            {
                error = "failed to build canonical joint rotation map.";
                return false;
            }

            var localAxisAngles = new List<Vector3>(jointNames.Length);
            var sampledJointIndices = new List<int>(jointNames.Length);
            for (int i = 0; i < jointNames.Length; i++)
            {
                string jointName = jointNames[i];
                if (string.IsNullOrWhiteSpace(jointName))
                {
                    error = "canonical joint layout contains empty joint name.";
                    return false;
                }

                if (!rotationMap.TryGetValue(jointName.Trim(), out Quaternion rotation))
                {
                    error = $"canonical joint '{jointName}' missing on sampled frame.";
                    return false;
                }

                localAxisAngles.Add(KimodoRuntimeUtility.QuaternionToAxisAngleVector(rotation));
                sampledJointIndices.Add(i);
            }

            result = new KimodoMarkerSampleResult
            {
                constraintType = markerType ?? string.Empty,
                sampleTime = sampleTime,
                rigType = rigType,
                hasRootHeading = true,
                rootPosition = pose.bodyPosition,
                rootHeading = Vector2.right,
                jointNames = new List<string>(jointNames),
                localAxisAngles = localAxisAngles,
                sampledJointIndices = sampledJointIndices
            };

            if (pose.bodyRotation != Quaternion.identity)
            {
                Vector3 forward = pose.bodyRotation * Vector3.forward;
                Vector2 heading = new Vector2(forward.x, forward.z);
                result.rootHeading = heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector2.right;
            }

            return true;
        }

        private static Dictionary<string, Quaternion> BuildLeafRotationMap(BoneSample BoneSample)
        {
            if (BoneSample == null || BoneSample.boneNames == null || BoneSample.localRotations == null)
            {
                return null;
            }

            var map = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
            int count = Mathf.Min(BoneSample.boneNames.Length, BoneSample.localRotations.Length);
            for (int i = 0; i < count; i++)
            {
                string leaf = ExtractLeafName(BoneSample.boneNames[i]);
                if (string.IsNullOrWhiteSpace(leaf) || map.ContainsKey(leaf))
                {
                    continue;
                }

                map[leaf.Trim()] = BoneSample.localRotations[i];
            }

            return map;
        }

        private static string ExtractLeafName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            int index = path.LastIndexOf('/');
            return index >= 0 && index < path.Length - 1 ? path.Substring(index + 1) : path;
        }
    }
}

