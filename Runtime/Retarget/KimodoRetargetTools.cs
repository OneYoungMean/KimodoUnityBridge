using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

namespace KimodoUnityMotionTools
{
    public interface IGetMuscleData
    {
        bool TryGetPoseHandle(out HumanPoseHandler poseHandle, out string error);
        bool TryGetPose(float time, ref HumanPose pose, out string error);
    }

    public static class KimodoRetargetTools
    {
        public static readonly HumanBodyBones[] BonesToUse =
        {
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            HumanBodyBones.RightToes
        };

        public sealed class BoneFrame
        {
            public string[] boneNames;
            public Vector3[] localPositions;
            public Quaternion[] localRotations;
        }

        public sealed class RetargetResult
        {
            public string[] boneNames;
            public BoneFrame[] frames;
            public HumanPose[] poses;
            public Vector3 rootPosition;
            public Quaternion rootRotation;
        }

        public static bool TryRetarget(
            IGetMuscleData sourceMuscleData,
            Avatar targetAvatar,
            float sampleRate,
            float duration,
            out RetargetResult result,
            out string error)
        {
            result = null;
            error = string.Empty;

            if (sourceMuscleData == null)
            {
                error = "Source muscle data is null.";
                return false;
            }

            if (!IsValidHumanoid(targetAvatar))
            {
                error = "Target avatar is null/invalid/non-humanoid.";
                return false;
            }

            float effectiveRate = sampleRate > 0f ? sampleRate : 30f;
            float effectiveDuration = duration > 0f ? duration : 1f / Mathf.Max(1f, effectiveRate);
            int frameCount = Mathf.Max(2, Mathf.RoundToInt(effectiveDuration * effectiveRate) + 1);

            GameObject targetRoot = null;
            try
            {
                targetRoot = new GameObject("KimodoRetargetTools_TargetRuntime");
                targetRoot.hideFlags = HideFlags.None;
                targetRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                targetRoot.transform.localScale = Vector3.one;

                if (!TryBuildHumanoidHierarchy(targetAvatar, targetRoot.transform, out Transform skeletonRoot, out error))
                {
                    return false;
                }

                if (skeletonRoot == null)
                {
                    error = "Failed to resolve target skeleton root.";
                    return false;
                }

                Animator targetAnimator = skeletonRoot.gameObject.GetComponent<Animator>();
                if (targetAnimator == null)
                {
                    targetAnimator = skeletonRoot.gameObject.AddComponent<Animator>();
                }

                targetAnimator.avatar = targetAvatar;
                targetAnimator.runtimeAnimatorController = null;
                targetAnimator.applyRootMotion = false;
                targetAnimator.enabled = false;
                targetAnimator.Rebind();
                targetAnimator.Update(0f);
                var targetPoseHandler = new HumanPoseHandler(targetAvatar, skeletonRoot);

                if (!TryBuildBoneNameTable(skeletonRoot, out string[] boneNames, out string boneError))
                {
                    error = boneError;
                    return false;
                }

                var frames = new BoneFrame[frameCount];
                var poses = new HumanPose[frameCount];
                var pose = new HumanPose();
                var targetPose = new HumanPose();
                Vector3 rootPosition = Vector3.zero;
                Quaternion rootRotation = Quaternion.identity;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float time = FrameToTime(frame, frameCount, effectiveDuration);
                    if (!sourceMuscleData.TryGetPose(time, ref pose, out error))
                    {
                        return false;
                    }

                    if (!TrySetHumanPose(targetAvatar, skeletonRoot, ref pose, out error))
                    {
                        return false;
                    }

                    frames[frame] = CaptureBoneFrame(skeletonRoot, boneNames);
                    targetPoseHandler.GetHumanPose(ref targetPose);
                    TryCopyHumanPose(targetPose, out poses[frame]);
                    if (frame == 0)
                    {
                        Transform hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
                        if (hips != null)
                        {
                            rootPosition = hips.position;
                            rootRotation = hips.rotation;
                        }
                    }
                }

                result = new RetargetResult
                {
                    boneNames = boneNames,
                    frames = frames,
                    poses = poses,
                    rootPosition = rootPosition,
                    rootRotation = rootRotation
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (targetRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(targetRoot);
                }
            }
        }

        public static bool TryRetargetPose(
            HumanPose sourcePose,
            Avatar targetAvatar,
            out RetargetResult result,
            out string error)
        {
            result = null;
            error = string.Empty;

            if (!IsValidHumanoid(targetAvatar))
            {
                error = "Target avatar is null/invalid/non-humanoid.";
                return false;
            }

            GameObject targetRoot = null;
            try
            {
                targetRoot = new GameObject("KimodoRetargetTools_TargetRuntime");
                targetRoot.hideFlags = HideFlags.None;
                targetRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                targetRoot.transform.localScale = Vector3.one;

                if (!TryBuildHumanoidHierarchy(targetAvatar, targetRoot.transform, out Transform skeletonRoot, out error))
                {
                    return false;
                }

                if (skeletonRoot == null)
                {
                    error = "Failed to resolve target skeleton root.";
                    return false;
                }

                Animator targetAnimator = skeletonRoot.gameObject.GetComponent<Animator>();
                if (targetAnimator == null)
                {
                    targetAnimator = skeletonRoot.gameObject.AddComponent<Animator>();
                }

                targetAnimator.avatar = targetAvatar;
                targetAnimator.runtimeAnimatorController = null;
                targetAnimator.applyRootMotion = false;
                targetAnimator.enabled = false;
                targetAnimator.Rebind();
                targetAnimator.Update(0f);
                var targetPoseHandler = new HumanPoseHandler(targetAvatar, skeletonRoot);

                if (!TryBuildBoneNameTable(skeletonRoot, out string[] boneNames, out string boneError))
                {
                    error = boneError;
                    return false;
                }

                if (!TrySetHumanPose(targetAvatar, skeletonRoot, ref sourcePose, out error))
                {
                    return false;
                }

                var frames = new BoneFrame[1];
                var poses = new HumanPose[1];
                frames[0] = CaptureBoneFrame(skeletonRoot, boneNames);
                var targetPose = new HumanPose();
                targetPoseHandler.GetHumanPose(ref targetPose);
                TryCopyHumanPose(targetPose, out poses[0]);

                Vector3 rootPosition = Vector3.zero;
                Quaternion rootRotation = Quaternion.identity;
                Transform hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null)
                {
                    rootPosition = hips.position;
                    rootRotation = hips.rotation;
                }

                result = new RetargetResult
                {
                    boneNames = boneNames,
                    frames = frames,
                    poses = poses,
                    rootPosition = rootPosition,
                    rootRotation = rootRotation
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (targetRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(targetRoot);
                }
            }
        }

        public static bool TryGetHumanPose(Avatar avatar, Transform root, out HumanPose pose, out string error)
        {
            pose = new HumanPose();
            error = string.Empty;

            if (!IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (root == null)
            {
                error = "Transform root is null.";
                return false;
            }

            try
            {
                var handler = new HumanPoseHandler(avatar, root);
                handler.GetHumanPose(ref pose);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TrySetHumanPose(Avatar avatar, Transform root, ref HumanPose pose, out string error)
        {
            error = string.Empty;

            if (!IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (root == null)
            {
                error = "Transform root is null.";
                return false;
            }

            try
            {
                var handler = new HumanPoseHandler(avatar, root);
                handler.SetHumanPose(ref pose);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsValidHumanoid(Avatar avatar)
        {
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        private static float FrameToTime(int frame, int frameCount, float duration)
        {
            if (frameCount <= 1)
            {
                return 0f;
            }

            float normalized = frame / (frameCount - 1f);
            return Mathf.Clamp01(normalized) * Mathf.Max(0f, duration);
        }

        private static BoneFrame CaptureBoneFrame(Transform root, string[] boneNames)
        {
            var boneMap = BuildPathMap(root, root);
            var frame = new BoneFrame
            {
                boneNames = boneNames,
                localPositions = new Vector3[boneNames.Length],
                localRotations = new Quaternion[boneNames.Length]
            };

            for (int i = 0; i < boneNames.Length; i++)
            {
                string path = boneNames[i];
                if (!boneMap.TryGetValue(path, out Transform t) || t == null)
                {
                    frame.localPositions[i] = Vector3.zero;
                    frame.localRotations[i] = Quaternion.identity;
                    continue;
                }

                frame.localPositions[i] = t.localPosition;
                frame.localRotations[i] = t.localRotation;
            }

            return frame;
        }

        private static bool TryBuildBoneNameTable(Transform root, out string[] boneNames, out string error)
        {
            error = string.Empty;
            boneNames = null;
            if (root == null)
            {
                error = "Target root is null.";
                return false;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var names = new List<string>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                string path = CalculateTransformPath(all[i], root);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                names.Add(path);
            }

            boneNames = names.ToArray();
            return true;
        }

        private static bool TryBuildHumanoidHierarchy(Avatar avatar, Transform root, out Transform skeletonRoot, out string error)
        {
            error = string.Empty;
            skeletonRoot = null;
            if (avatar == null || root == null)
            {
                error = "Avatar or root is null.";
                return false;
            }

            SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0)
            {
                error = "Avatar skeleton is empty.";
                return false;
            }

            var nodes = new List<(string name, string parentName, Transform transform, Vector3 position, Quaternion rotation, Vector3 scale)>(skeleton.Length);
            var firstByName = new Dictionary<string, Transform>(StringComparer.Ordinal);

            for (int i = 0; i < skeleton.Length; i++)
            {
                SkeletonBone bone = skeleton[i];
                string name = string.IsNullOrWhiteSpace(bone.name) ? $"Bone_{i}" : bone.name;
                string parentName = AvatarRuntimeAccess.GetSkeletonBoneParentNameOrEmpty(bone);
                GameObject go = new GameObject(name);
                go.hideFlags = HideFlags.HideAndDontSave;
                nodes.Add((name, parentName, go.transform, bone.position, bone.rotation, bone.scale));
                if (!firstByName.ContainsKey(name))
                {
                    firstByName[name] = go.transform;
                }
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                Transform parent = root;
                if (!string.IsNullOrWhiteSpace(node.parentName) && firstByName.TryGetValue(node.parentName, out Transform parentTx) && parentTx != null)
                {
                    parent = parentTx;
                }
                else if (string.IsNullOrWhiteSpace(node.parentName) && skeletonRoot == null)
                {
                    skeletonRoot = node.transform;
                }

                node.transform.SetParent(parent, false);
                node.transform.localPosition = node.position;
                node.transform.localRotation = node.rotation;
                node.transform.localScale = node.scale;
            }

            if (skeletonRoot == null && nodes.Count > 0)
            {
                skeletonRoot = nodes[0].transform;
            }

            return true;
        }

        private static Dictionary<string, Transform> BuildPathMap(Transform current, Transform root)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (current == null || root == null)
            {
                return map;
            }

            Transform[] all = current.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                string path = CalculateTransformPath(t, root);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (!map.ContainsKey(path))
                {
                    map.Add(path, t);
                }
            }

            return map;
        }

        public static bool TryCopyHumanPose(HumanPose source, out HumanPose copy)
        {
            copy = new HumanPose
            {
                bodyPosition = source.bodyPosition,
                bodyRotation = source.bodyRotation,
                muscles = source.muscles != null ? (float[])source.muscles.Clone() : Array.Empty<float>()
            };
            return true;
        }

        private static string CalculateTransformPath(Transform target, Transform root)
        {
            if (target == null || root == null)
            {
                return null;
            }

            if (target == root)
            {
                return target.name;
            }

            var names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }

            if (current != root)
            {
                return null;
            }

            names.Reverse();
            return string.Join("/", names);
        }
    }
}
