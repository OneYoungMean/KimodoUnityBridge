using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public static partial class KimodoRetargetPipeline
    {
        private static bool TryConvertMuscleToTargetBoneClip(
            AnimationClip muscleClip,
            Animator targetHumanoidAnimator,
            AnimationClip outputTargetBoneClip,
            out string error,
            float sampleRate = 30f)
        {
            error = string.Empty;
            GameObject targetTempRoot = null;
            if (muscleClip == null || targetHumanoidAnimator == null || outputTargetBoneClip == null)
            {
                error = "Input clip/animator/output is null.";
                return false;
            }

            if (targetHumanoidAnimator.avatar == null || !targetHumanoidAnimator.avatar.isValid || !targetHumanoidAnimator.avatar.isHuman)
            {
                error = "Target animator must have valid humanoid avatar.";
                return false;
            }

            try
            {
                float effectiveRate = sampleRate > 0f ? sampleRate : (muscleClip.frameRate > 0f ? muscleClip.frameRate : 30f);
                float duration = muscleClip.length > 0f ? muscleClip.length : 1f / Mathf.Max(1f, effectiveRate);
                int frameCount = Mathf.Max(2, Mathf.RoundToInt(duration * effectiveRate) + 1);

                Animator targetSamplingAnimator = CreateTempAnimatorForAvatar(targetHumanoidAnimator, targetHumanoidAnimator.avatar, out targetTempRoot);
                if (targetSamplingAnimator == null)
                {
                    error = "Failed to create target sampling animator.";
                    return false;
                }

                Transform root = targetSamplingAnimator.transform;
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                string[] paths = new string[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    paths[i] = AnimationUtility.CalculateTransformPath(all[i], root);
                }

                var px = new AnimationCurve[all.Length];
                var py = new AnimationCurve[all.Length];
                var pz = new AnimationCurve[all.Length];
                var qx = new AnimationCurve[all.Length];
                var qy = new AnimationCurve[all.Length];
                var qz = new AnimationCurve[all.Length];
                var qw = new AnimationCurve[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    px[i] = new AnimationCurve();
                    py[i] = new AnimationCurve();
                    pz[i] = new AnimationCurve();
                    qx[i] = new AnimationCurve();
                    qy[i] = new AnimationCurve();
                    qz[i] = new AnimationCurve();
                    qw[i] = new AnimationCurve();
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float t = FrameToTime(frame, frameCount, duration);
                    muscleClip.SampleAnimation(targetSamplingAnimator.gameObject, t);


                    for (int i = 0; i < all.Length; i++)
                    {
                        Transform bone = all[i];
                        Vector3 lp = bone.localPosition;
                        Quaternion lr = bone.localRotation;

                        px[i].AddKey(t, lp.x);
                        py[i].AddKey(t, lp.y);
                        pz[i].AddKey(t, lp.z);
                        qx[i].AddKey(t, lr.x);
                        qy[i].AddKey(t, lr.y);
                        qz[i].AddKey(t, lr.z);
                        qw[i].AddKey(t, lr.w);
                    }
                }

                outputTargetBoneClip.ClearCurves();
                outputTargetBoneClip.legacy = false;
                outputTargetBoneClip.frameRate = effectiveRate;
                for (int i = 0; i < all.Length; i++)
                {
                    string path = paths[i];
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", px[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", py[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", pz[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", qx[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", qy[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", qz[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", qw[i]);
                }
                outputTargetBoneClip.EnsureQuaternionContinuity();
                return true;
            }
            catch (Exception e)
            {
                error = $"Convert muscle->target bone failed: {e.Message}";
                return false;
            }
            finally
            {
                if (targetTempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(targetTempRoot);
                }
            }
        }

        private static bool TryConvertBoneClipToMuscleByAvatar(
            AnimationClip sourceBoneClip,
            Animator sourceHumanoidAnimator,
            AnimationClip outputMuscleClip,
            out string error,
            float sampleRate = 30f)
        {
            error = string.Empty;
            if (sourceBoneClip == null || sourceHumanoidAnimator == null || outputMuscleClip == null)
            {
                error = "Input clip/animator/output is null.";
                return false;
            }

            if (sourceHumanoidAnimator.avatar == null || !sourceHumanoidAnimator.avatar.isValid || !sourceHumanoidAnimator.avatar.isHuman)
            {
                error = "Source animator must have valid humanoid avatar.";
                return false;
            }

            try
            {
                float effectiveRate = sampleRate > 0f ? sampleRate : (sourceBoneClip.frameRate > 0f ? sourceBoneClip.frameRate : 30f);
                float duration = sourceBoneClip.length > 0f ? sourceBoneClip.length : 1f / Mathf.Max(1f, effectiveRate);
                int frameCount = Mathf.Max(2, Mathf.RoundToInt(duration * effectiveRate) + 1);

                GameObject samplerRig = UnityEngine.Object.Instantiate(sourceHumanoidAnimator.gameObject);
                samplerRig.hideFlags = HideFlags.HideAndDontSave;
                samplerRig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                samplerRig.transform.localScale = Vector3.one;

                GameObject avatarRig = UnityEngine.Object.Instantiate(sourceHumanoidAnimator.gameObject);
                avatarRig.hideFlags = HideFlags.HideAndDontSave;
                avatarRig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                avatarRig.transform.localScale = Vector3.one;

                try
                {
                    Animator samplerAnimator = samplerRig.GetComponent<Animator>();
                    Animator avatarAnimator = avatarRig.GetComponent<Animator>();
                    if (samplerAnimator == null || avatarAnimator == null)
                    {
                        error = "Failed to create sampler/avatar animators.";
                        return false;
                    }

                    // Sampler rig: no avatar solving, only raw transform sampling from clip.
                    samplerAnimator.enabled = false;
                    samplerAnimator.applyRootMotion = false;
                    samplerAnimator.avatar = null;
                    samplerAnimator.Rebind();
                    samplerAnimator.Update(0f);
                    // SampleAnimation resolves clip paths relative to the sampled GameObject root.
                    // Keep the full rig root here; using a deeper child root can make every binding miss
                    // and leave the rig in bind/T-pose.
                    Transform samplingRoot = samplerAnimator.transform;
                    string samplingRootPath = AnimationUtility.CalculateTransformPath(samplingRoot, samplerAnimator.transform);

                    // Avatar rig: receives copied local pose, then converts to humanoid muscle pose.
                    avatarAnimator.enabled = false;
                    avatarAnimator.applyRootMotion = false;
                    avatarAnimator.avatar = sourceHumanoidAnimator.avatar;
                    avatarAnimator.Rebind();
                    avatarAnimator.Update(0f);
                    Transform avatarPoseRoot = avatarAnimator.transform;
                    string avatarPoseRootPath = AnimationUtility.CalculateTransformPath(avatarPoseRoot, avatarAnimator.transform);

                    int sourceBindingCount = AnimationUtility.GetCurveBindings(sourceBoneClip).Length;
                    string firstBindingPath = sourceBindingCount > 0 ? AnimationUtility.GetCurveBindings(sourceBoneClip)[0].path : string.Empty;

                    HumanPoseHandler poseHandler = new HumanPoseHandler(avatarAnimator.avatar, avatarAnimator.transform);
                    HumanPose pose = new HumanPose();

                    outputMuscleClip.ClearCurves();
                    outputMuscleClip.legacy = false;
                    outputMuscleClip.frameRate = effectiveRate;

                    AnimationCurve[] muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
                    for (int i = 0; i < muscleCurves.Length; i++)
                    {
                        muscleCurves[i] = new AnimationCurve();
                    }

                    AnimationCurve rootTx = new AnimationCurve();
                    AnimationCurve rootTy = new AnimationCurve();
                    AnimationCurve rootTz = new AnimationCurve();
                    AnimationCurve rootQx = new AnimationCurve();
                    AnimationCurve rootQy = new AnimationCurve();
                    AnimationCurve rootQz = new AnimationCurve();
                    AnimationCurve rootQw = new AnimationCurve();
                    AnimationCurve leftFootTx = new AnimationCurve();
                    AnimationCurve leftFootTy = new AnimationCurve();
                    AnimationCurve leftFootTz = new AnimationCurve();
                    AnimationCurve leftFootQx = new AnimationCurve();
                    AnimationCurve leftFootQy = new AnimationCurve();
                    AnimationCurve leftFootQz = new AnimationCurve();
                    AnimationCurve leftFootQw = new AnimationCurve();
                    AnimationCurve rightFootTx = new AnimationCurve();
                    AnimationCurve rightFootTy = new AnimationCurve();
                    AnimationCurve rightFootTz = new AnimationCurve();
                    AnimationCurve rightFootQx = new AnimationCurve();
                    AnimationCurve rightFootQy = new AnimationCurve();
                    AnimationCurve rightFootQz = new AnimationCurve();
                    AnimationCurve rightFootQw = new AnimationCurve();

                    Transform leftFootBone = avatarAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    Transform rightFootBone = avatarAnimator.GetBoneTransform(HumanBodyBones.RightFoot);

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        float t = FrameToTime(frame, frameCount, duration);
                        sourceBoneClip.SampleAnimation(samplingRoot.gameObject, t);
                        int copiedCount = CopyLocalPoseByPathForSampling(samplingRoot, avatarPoseRoot);
                        if (copiedCount <= 0)
                        {
                            error = $"No transform pose copied from sampler root '{samplingRootPath}' to avatar root '{avatarPoseRootPath}'.";
                            return false;
                        }
                        poseHandler.GetHumanPose(ref pose);


                        for (int i = 0; i < HumanTrait.MuscleCount && i < pose.muscles.Length; i++)
                        {
                            muscleCurves[i].AddKey(t, pose.muscles[i]);
                        }

                        rootTx.AddKey(t, pose.bodyPosition.x);
                        rootTy.AddKey(t, pose.bodyPosition.y);
                        rootTz.AddKey(t, pose.bodyPosition.z);
                        rootQx.AddKey(t, pose.bodyRotation.x);
                        rootQy.AddKey(t, pose.bodyRotation.y);
                        rootQz.AddKey(t, pose.bodyRotation.z);
                        rootQw.AddKey(t, pose.bodyRotation.w);

                        IkGoalTQ leftFootGoal = new IkGoalTQ(Vector3.zero, Quaternion.identity);
                        if (leftFootBone != null)
                        {
                            leftFootGoal = ComputeIkGoalTQ(
                                avatarAnimator.avatar,
                                avatarAnimator.humanScale,
                                AvatarIKGoal.LeftFoot,
                                pose.bodyPosition,
                                pose.bodyRotation,
                                leftFootBone.position,
                                leftFootBone.rotation);
                        }

                        IkGoalTQ rightFootGoal = new IkGoalTQ(Vector3.zero, Quaternion.identity);
                        if (rightFootBone != null)
                        {
                            rightFootGoal = ComputeIkGoalTQ(
                                avatarAnimator.avatar,
                                avatarAnimator.humanScale,
                                AvatarIKGoal.RightFoot,
                                pose.bodyPosition,
                                pose.bodyRotation,
                                rightFootBone.position,
                                rightFootBone.rotation);
                        }

                        leftFootTx.AddKey(t, leftFootGoal.t.x);
                        leftFootTy.AddKey(t, leftFootGoal.t.y);
                        leftFootTz.AddKey(t, leftFootGoal.t.z);
                        leftFootQx.AddKey(t, leftFootGoal.q.x);
                        leftFootQy.AddKey(t, leftFootGoal.q.y);
                        leftFootQz.AddKey(t, leftFootGoal.q.z);
                        leftFootQw.AddKey(t, leftFootGoal.q.w);
                        rightFootTx.AddKey(t, rightFootGoal.t.x);
                        rightFootTy.AddKey(t, rightFootGoal.t.y);
                        rightFootTz.AddKey(t, rightFootGoal.t.z);
                        rightFootQx.AddKey(t, rightFootGoal.q.x);
                        rightFootQy.AddKey(t, rightFootGoal.q.y);
                        rightFootQz.AddKey(t, rightFootGoal.q.z);
                        rightFootQw.AddKey(t, rightFootGoal.q.w);
                    }


                    for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    {
                        outputMuscleClip.SetCurve(string.Empty, typeof(Animator), MusclePropertyNames[i], muscleCurves[i]);
                    }

                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.x", rootTx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.y", rootTy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.z", rootTz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.x", rootQx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.y", rootQy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.z", rootQz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.w", rootQw);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "LeftFootT.x", leftFootTx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "LeftFootT.y", leftFootTy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "LeftFootT.z", leftFootTz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "LeftFootQ.x", leftFootQx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "LeftFootQ.y", leftFootQy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "LeftFootQ.z", leftFootQz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "LeftFootQ.w", leftFootQw);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RightFootT.x", rightFootTx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RightFootT.y", rightFootTy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RightFootT.z", rightFootTz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RightFootQ.x", rightFootQx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RightFootQ.y", rightFootQy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RightFootQ.z", rightFootQz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RightFootQ.w", rightFootQw);
                    outputMuscleClip.EnsureQuaternionContinuity();
                    return true;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(samplerRig);
                    UnityEngine.Object.DestroyImmediate(avatarRig);
                }
            }
            catch (Exception e)
            {
                error = $"Convert source bone->muscle by avatar failed: {e.Message}";
                return false;
            }
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

        private readonly struct IkGoalTQ
        {
            public readonly Vector3 t;
            public readonly Quaternion q;

            public IkGoalTQ(Vector3 t, Quaternion q)
            {
                this.t = t;
                this.q = q;
            }
        }

        private static IkGoalTQ ComputeIkGoalTQ(
            Avatar avatar,
            float humanScale,
            AvatarIKGoal goal,
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            Vector3 skeletonWorldPosition,
            Quaternion skeletonWorldRotation)
        {
            if (avatar == null)
            {
                return new IkGoalTQ(Vector3.zero, Quaternion.identity);
            }

            int humanId = HumanIdFromIkGoal(goal);
            if (humanId == (int)HumanBodyBones.LastBone)
            {
                return new IkGoalTQ(Vector3.zero, Quaternion.identity);
            }

            Quaternion postRotation = GetAvatarPostRotation(avatar, humanId);
            float axisLength = GetAvatarAxisLength(avatar, humanId);

            Quaternion goalQ = skeletonWorldRotation * postRotation;
            Vector3 goalT = skeletonWorldPosition;
            if (goal == AvatarIKGoal.LeftFoot || goal == AvatarIKGoal.RightFoot)
            {
                goalT += goalQ * new Vector3(axisLength, 0f, 0f);
            }

            Quaternion invRootQ = Quaternion.Inverse(bodyRotation);
            goalT = invRootQ * (goalT - bodyPosition);
            goalQ = invRootQ * goalQ;
            if (humanScale > 0f)
            {
                goalT /= humanScale;
            }

            return new IkGoalTQ(goalT, goalQ);
        }

        private static int HumanIdFromIkGoal(AvatarIKGoal goal)
        {
            switch (goal)
            {
                case AvatarIKGoal.LeftFoot:
                    return (int)HumanBodyBones.LeftFoot;
                case AvatarIKGoal.RightFoot:
                    return (int)HumanBodyBones.RightFoot;
                case AvatarIKGoal.LeftHand:
                    return (int)HumanBodyBones.LeftHand;
                case AvatarIKGoal.RightHand:
                    return (int)HumanBodyBones.RightHand;
                default:
                    return (int)HumanBodyBones.LastBone;
            }
        }

        private static Quaternion GetAvatarPostRotation(Avatar avatar, int humanId)
        {
            var method = typeof(Avatar).GetMethod("GetPostRotation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method == null)
            {
                return Quaternion.identity;
            }

            return (Quaternion)method.Invoke(avatar, new object[] { humanId });
        }

        private static float GetAvatarAxisLength(Avatar avatar, int humanId)
        {
            var method = typeof(Avatar).GetMethod("GetAxisLength", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method == null)
            {
                return 0f;
            }

            object value = method.Invoke(avatar, new object[] { humanId });
            return value is float f ? f : 0f;
        }

        private static Transform FindTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    stack.Push(current.GetChild(i));
                }
            }

            return null;
        }

        private static Transform[] ResolveSoma30Joints(Transform root)
        {
            var joints = new Transform[Soma30Names.Length];
            for (int i = 0; i < Soma30Names.Length; i++)
            {
                joints[i] = FindTransformByName(root, Soma30Names[i]) ?? root;
            }
            return joints;
        }

        private static Vector3 QuaternionToAxisAngleVector(Quaternion q)
        {
            q.Normalize();
            q.ToAngleAxis(out float degrees, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis == Vector3.zero)
            {
                return Vector3.zero;
            }

            if (degrees > 180f)
            {
                degrees -= 360f;
            }

            float radians = degrees * Mathf.Deg2Rad;
            return axis.normalized * radians;
        }

        private static readonly string[] Soma30Names =
        {
            "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "Jaw", "LeftEye", "RightEye",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandThumbEnd", "LeftHandMiddleEnd",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandThumbEnd", "RightHandMiddleEnd",
            "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase", "RightLeg", "RightShin", "RightFoot", "RightToeBase"
        };

        private static readonly int[] Soma30Parents =
        {
            -1, 0, 1, 2, 3, 4, 5, 6, 6, 6, 3, 10, 11, 12, 13, 13, 3, 16, 17, 18, 19, 19, 0, 22, 23, 24, 0, 26, 27, 28
        };

        private static readonly string[] MusclePropertyNames = BuildMusclePropertyNames();

        private static string[] BuildMusclePropertyNames()
        {
            string[] names = new string[HumanTrait.MuscleCount];
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                string n = HumanTrait.MuscleName[i];
                if (i >= 55)
                {
                    string[] parts = n.Split(' ');
                    if (parts.Length == 2)
                    {
                        parts[0] += "Hand.";
                        parts[1] += ".";
                        n = string.Join(" ", parts);
                    }
                }

                names[i] = n;
            }

            return names;
        }
    }
}
