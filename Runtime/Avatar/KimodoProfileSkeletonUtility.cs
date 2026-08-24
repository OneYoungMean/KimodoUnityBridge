using System;
using UnityEngine;

namespace KimodoBridge
{
    public static class KimodoProfileSkeletonUtility
    {
        public static bool TryResolveProfileSkeleton(
            string modelName,
            RetargetSkeleton cache,
            out string[] jointNames,
            out int[] parentIndices,
            out Transform[] jointTransforms,
            out string error)
        {
            jointTransforms = Array.Empty<Transform>();
            if (!TryResolveProfileLayout(modelName, out jointNames, out parentIndices, out error))
            {
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(cache, out error))
            {
                return false;
            }

            return TryResolveJointTransforms(
                jointNames,
                cache.skeletonRoot,
                (string jointName, out Transform jointTransform, out bool ambiguous) =>
                    TryResolveHumanoidOrNamedTransform(
                        modelName,
                        cache,
                        jointName,
                        out jointTransform,
                        out ambiguous),
                out jointTransforms,
                out error);
        }

        public static bool TryResolveProfileSkeleton(
            string modelName,
            Transform root,
            out string[] jointNames,
            out int[] parentIndices,
            out Transform[] jointTransforms,
            out string error)
        {
            jointTransforms = Array.Empty<Transform>();
            if (!TryResolveProfileLayout(modelName, out jointNames, out parentIndices, out error))
            {
                return false;
            }

            if (root == null)
            {
                error = "Skeleton root is null.";
                return false;
            }

            return TryResolveJointTransforms(
                jointNames,
                root,
                (string jointName, out Transform jointTransform, out bool ambiguous) =>
                {
                    if (KimodoRetargetAvatarUtility.TryFindUniqueTransformByName(
                            root,
                            jointName,
                            out jointTransform,
                            out ambiguous))
                    {
                        return true;
                    }
                    ambiguous = false;
                    return TryResolveHumanoidTransform(root, modelName, jointName, out jointTransform);
                },
                out jointTransforms,
                out error);
        }

        private delegate bool TryResolveJointTransform(
            string jointName,
            out Transform jointTransform,
            out bool ambiguous);

        private static bool TryResolveHumanoidOrNamedTransform(
            string modelName,
            RetargetSkeleton cache,
            string jointName,
            out Transform jointTransform,
            out bool ambiguous)
        {
            jointTransform = null;
            ambiguous = false;
            if (KimodoRetargetAvatarUtility.TryGetUniqueCachedTransformByName(
                    cache,
                    jointName,
                    out jointTransform,
                    out ambiguous))
            {
                return true;
            }

            ambiguous = false;
            return TryResolveHumanoidTransform(modelName, cache, jointName, out jointTransform);
        }

        private static bool TryResolveHumanoidTransform(
            string modelName,
            RetargetSkeleton cache,
            string jointName,
            out Transform jointTransform)
        {
            jointTransform = null;
            if (cache?.humanBoneTransforms == null ||
                !KimodoRetargetCoreUtility.IsValidHumanoid(cache.avatar) ||
                !TryMapProfileJointToHumanBodyBone(modelName, jointName, out HumanBodyBones bone))
            {
                return false;
            }

            return cache.humanBoneTransforms.TryGetValue(bone, out jointTransform) && jointTransform != null;
        }

        private static bool TryResolveHumanoidTransform(
            Transform root,
            string modelName,
            string jointName,
            out Transform jointTransform)
        {
            jointTransform = null;
            Animator animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
            if (animator == null || !KimodoRetargetCoreUtility.IsValidHumanoid(animator.avatar) ||
                !TryMapProfileJointToHumanBodyBone(modelName, jointName, out HumanBodyBones bone))
            {
                return false;
            }

            jointTransform = animator.GetBoneTransform(bone);
            return jointTransform != null;
        }

        private static bool TryMapProfileJointToHumanBodyBone(
            string modelName,
            string jointName,
            out HumanBodyBones bone)
        {
            bone = HumanBodyBones.LastBone;
            if (string.IsNullOrWhiteSpace(jointName)) return false;

            switch (jointName)
            {
                case "Hips": bone = HumanBodyBones.Hips; return true;
                case "Spine":
                case "Spine1": bone = HumanBodyBones.Spine; return true;
                case "Spine2":
                case "Chest": bone = HumanBodyBones.Chest; return true;
                case "Spine3": bone = HumanBodyBones.UpperChest; return true;
                case "Neck":
                case "Neck1":
                case "Neck2": bone = HumanBodyBones.Neck; return true;
                case "Head":
                case "HeadEnd": bone = HumanBodyBones.Head; return true;
                case "Jaw": bone = HumanBodyBones.Jaw; return true;
                case "LeftEye": bone = HumanBodyBones.LeftEye; return true;
                case "RightEye": bone = HumanBodyBones.RightEye; return true;
                case "LeftShoulder": bone = HumanBodyBones.LeftShoulder; return true;
                case "RightShoulder": bone = HumanBodyBones.RightShoulder; return true;
                case "LeftArm": bone = HumanBodyBones.LeftUpperArm; return true;
                case "RightArm": bone = HumanBodyBones.RightUpperArm; return true;
                case "LeftForeArm": bone = HumanBodyBones.LeftLowerArm; return true;
                case "RightForeArm": bone = HumanBodyBones.RightLowerArm; return true;
                case "LeftHand":
                case "LeftHandEnd": bone = HumanBodyBones.LeftHand; return true;
                case "RightHand":
                case "RightHandEnd": bone = HumanBodyBones.RightHand; return true;
                case "LeftLeg":
                case "LeftUpLeg": bone = HumanBodyBones.LeftUpperLeg; return true;
                case "RightLeg":
                case "RightUpLeg": bone = HumanBodyBones.RightUpperLeg; return true;
                case "LeftShin":
                case "LeftLowerLeg": bone = HumanBodyBones.LeftLowerLeg; return true;
                case "RightShin":
                case "RightLowerLeg": bone = HumanBodyBones.RightLowerLeg; return true;
                case "LeftFoot": bone = HumanBodyBones.LeftFoot; return true;
                case "RightFoot": bone = HumanBodyBones.RightFoot; return true;
                case "LeftToeBase":
                case "LeftToeEnd": bone = HumanBodyBones.LeftToes; return true;
                case "RightToeBase":
                case "RightToeEnd": bone = HumanBodyBones.RightToes; return true;
                case "LeftHandThumb1": bone = HumanBodyBones.LeftThumbProximal; return true;
                case "LeftHandThumb2": bone = HumanBodyBones.LeftThumbIntermediate; return true;
                case "LeftHandThumb3":
                case "LeftHandThumbEnd": bone = HumanBodyBones.LeftThumbDistal; return true;
                case "RightHandThumb1": bone = HumanBodyBones.RightThumbProximal; return true;
                case "RightHandThumb2": bone = HumanBodyBones.RightThumbIntermediate; return true;
                case "RightHandThumb3":
                case "RightHandThumbEnd": bone = HumanBodyBones.RightThumbDistal; return true;
                case "LeftHandIndex1": bone = HumanBodyBones.LeftIndexProximal; return true;
                case "LeftHandIndex2": bone = HumanBodyBones.LeftIndexIntermediate; return true;
                case "LeftHandIndex3":
                case "LeftHandIndex4":
                case "LeftHandIndexEnd": bone = HumanBodyBones.LeftIndexDistal; return true;
                case "RightHandIndex1": bone = HumanBodyBones.RightIndexProximal; return true;
                case "RightHandIndex2": bone = HumanBodyBones.RightIndexIntermediate; return true;
                case "RightHandIndex3":
                case "RightHandIndex4":
                case "RightHandIndexEnd": bone = HumanBodyBones.RightIndexDistal; return true;
                case "LeftHandMiddle1": bone = HumanBodyBones.LeftMiddleProximal; return true;
                case "LeftHandMiddle2": bone = HumanBodyBones.LeftMiddleIntermediate; return true;
                case "LeftHandMiddle3":
                case "LeftHandMiddle4":
                case "LeftHandMiddleEnd": bone = HumanBodyBones.LeftMiddleDistal; return true;
                case "RightHandMiddle1": bone = HumanBodyBones.RightMiddleProximal; return true;
                case "RightHandMiddle2": bone = HumanBodyBones.RightMiddleIntermediate; return true;
                case "RightHandMiddle3":
                case "RightHandMiddle4":
                case "RightHandMiddleEnd": bone = HumanBodyBones.RightMiddleDistal; return true;
                case "LeftHandRing1": bone = HumanBodyBones.LeftRingProximal; return true;
                case "LeftHandRing2": bone = HumanBodyBones.LeftRingIntermediate; return true;
                case "LeftHandRing3":
                case "LeftHandRing4":
                case "LeftHandRingEnd": bone = HumanBodyBones.LeftRingDistal; return true;
                case "RightHandRing1": bone = HumanBodyBones.RightRingProximal; return true;
                case "RightHandRing2": bone = HumanBodyBones.RightRingIntermediate; return true;
                case "RightHandRing3":
                case "RightHandRing4":
                case "RightHandRingEnd": bone = HumanBodyBones.RightRingDistal; return true;
                case "LeftHandPinky1": bone = HumanBodyBones.LeftLittleProximal; return true;
                case "LeftHandPinky2": bone = HumanBodyBones.LeftLittleIntermediate; return true;
                case "LeftHandPinky3":
                case "LeftHandPinky4":
                case "LeftHandPinkyEnd": bone = HumanBodyBones.LeftLittleDistal; return true;
                case "RightHandPinky1": bone = HumanBodyBones.RightLittleProximal; return true;
                case "RightHandPinky2": bone = HumanBodyBones.RightLittleIntermediate; return true;
                case "RightHandPinky3":
                case "RightHandPinky4":
                case "RightHandPinkyEnd": bone = HumanBodyBones.RightLittleDistal; return true;
            }

            return false;
        }

        private static bool TryResolveProfileLayout(
            string modelName,
            out string[] jointNames,
            out int[] parentIndices,
            out string error)
        {
            error = string.Empty;
            KimodoRigProfileDatabase.ResolveProfile(modelName, out _, out jointNames, out parentIndices);
            if (jointNames == null || jointNames.Length == 0)
            {
                error = $"Profile joint layout not found for '{modelName}'.";
                return false;
            }

            return true;
        }

        private static bool TryResolveJointTransforms(
            string[] jointNames,
            Transform root,
            TryResolveJointTransform tryResolveJointTransform,
            out Transform[] jointTransforms,
            out string error)
        {
            error = string.Empty;

            jointTransforms = new Transform[jointNames.Length];
            for (int i = 0; i < jointNames.Length; i++)
            {
                string jointName = jointNames[i];
                if (string.IsNullOrWhiteSpace(jointName))
                {
                    error = $"Profile joint at index {i} is empty.";
                    return false;
                }

                if (!tryResolveJointTransform(jointName, out jointTransforms[i], out bool ambiguous))
                {
                    error = ambiguous
                        ? $"Profile joint '{jointName}' matches multiple transforms under '{root.name}'."
                        : $"Profile joint '{jointName}' was not found under '{root.name}'.";
                    jointTransforms = Array.Empty<Transform>();
                    return false;
                }
            }

            return true;
        }
    }
}
