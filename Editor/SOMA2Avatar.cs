using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.Editor
{
    public static class SOMA2Avatar
    {
        private const float DefaultSampleRate = 30f;
        private const string TempRigName = "__SOMA2Avatar_TempRig";

        private static readonly string[] Soma77Names =
        {
            "Pelvis", "L_Hip", "L_Knee", "L_Foot", "L_Toes",
            "R_Hip", "R_Knee", "R_Foot", "R_Toes",
            "Spine1", "Spine2", "Spine3", "Neck", "Head",
            "L_Clavicle", "L_Shoulder", "L_Elbow", "L_Wrist", "L_Hand",
            "L_Thumb1", "L_Thumb2", "L_Thumb3",
            "L_Index1", "L_Index2", "L_Index3",
            "L_Middle1", "L_Middle2", "L_Middle3",
            "L_Ring1", "L_Ring2", "L_Ring3",
            "L_Pinky1", "L_Pinky2", "L_Pinky3",
            "R_Clavicle", "R_Shoulder", "R_Elbow", "R_Wrist", "R_Hand",
            "R_Thumb1", "R_Thumb2", "R_Thumb3",
            "R_Index1", "R_Index2", "R_Index3",
            "R_Middle1", "R_Middle2", "R_Middle3",
            "R_Ring1", "R_Ring2", "R_Ring3",
            "R_Pinky1", "R_Pinky2", "R_Pinky3",
            "Jaw", "L_Eye", "R_Eye", "L_Ear", "R_Ear",
            "L_Thumb4", "R_Thumb4",
            "L_Index4", "R_Index4",
            "L_Middle4", "R_Middle4",
            "L_Ring4", "R_Ring4",
            "L_Pinky4", "R_Pinky4",
            "UpperLip", "LowerLip",
            "L_Breast", "R_Breast",
            "L_Nipple", "R_Nipple",
            "L_HandThumb", "R_HandThumb"
        };

        private static readonly int[] Soma77Parents =
        {
            -1, 0, 1, 2, 3,
            0, 5, 6, 7,
            0, 9, 10, 11, 12,
            12, 14, 15, 16, 17,
            18, 19, 20, 21,
            22, 23, 24,
            25, 26, 27,
            28, 29, 30,
            31, 32, 33,
            12, 35, 36, 37, 38,
            39, 40, 41, 42,
            43, 44, 45,
            46, 47, 48,
            49, 50, 51,
            13, 53, 54, 55, 56,
            21, 45,
            24, 48,
            27, 51,
            30, 54,
            33, 57,
            13, 13,
            10, 10,
            10, 10,
            18, 38
        };

        private static readonly string[] CanonicalSomaPaths = BuildCanonicalSomaPaths();
        private static readonly string[] MusclePropertyNames = BuildMusclePropertyNames();

        public static bool BoneClipToMuscleClip(
            AnimationClip somaBoneClip,
            Animator somaHumanoidAnimator,
            AnimationClip outputMuscleClip,
            out string error,
            float sampleRate = DefaultSampleRate)
        {
            error = string.Empty;

            if (!ValidateInputs(somaBoneClip, somaHumanoidAnimator, outputMuscleClip, out error))
            {
                return false;
            }

            float effectiveSampleRate = ResolveSampleRate(sampleRate, somaBoneClip.frameRate);
            float duration = ResolveDuration(somaBoneClip, effectiveSampleRate);
            int frameCount = CalculateFrameCount(duration, effectiveSampleRate);

            if (!TryCreateSamplingRig(somaHumanoidAnimator, out GameObject tempRig, out Animator tempAnimator, out error))
            {
                return false;
            }

            try
            {
                HumanPoseHandler poseHandler = new HumanPoseHandler(tempAnimator.avatar, tempAnimator.transform);
                HumanPose pose = new HumanPose();

                outputMuscleClip.ClearCurves();
                outputMuscleClip.legacy = false;
                outputMuscleClip.frameRate = effectiveSampleRate;

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

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float t = FrameToTime(frame, frameCount, duration);
                    somaBoneClip.SampleAnimation(tempRig, t);
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
                }

                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                {
                    string prop = MusclePropertyNames[i];
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), prop, muscleCurves[i]);
                }

                outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.x", rootTx);
                outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.y", rootTy);
                outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.z", rootTz);
                outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.x", rootQx);
                outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.y", rootQy);
                outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.z", rootQz);
                outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.w", rootQw);

                outputMuscleClip.EnsureQuaternionContinuity();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Bone->Muscle conversion failed: {ex.Message}";
                return false;
            }
            finally
            {
                DestroyTempRig(tempRig);
            }
        }

        public static bool MuscleClipToSomaBoneClip(
            AnimationClip muscleClip,
            Animator somaHumanoidAnimator,
            AnimationClip outputSomaBoneClip,
            out string error,
            float sampleRate = DefaultSampleRate)
        {
            error = string.Empty;

            if (!ValidateInputs(muscleClip, somaHumanoidAnimator, outputSomaBoneClip, out error))
            {
                return false;
            }

            float effectiveSampleRate = ResolveSampleRate(sampleRate, muscleClip.frameRate);
            float duration = ResolveDuration(muscleClip, effectiveSampleRate);
            int frameCount = CalculateFrameCount(duration, effectiveSampleRate);

            if (!TryCreateSamplingRig(somaHumanoidAnimator, out GameObject tempRig, out Animator tempAnimator, out error))
            {
                return false;
            }

            try
            {
                if (!TryFindSomaBones(tempAnimator.transform, out Transform[] somaBones, out error))
                {
                    return false;
                }

                var curveLookup = BuildAnimatorCurveLookup(muscleClip);
                HumanPoseHandler poseHandler = new HumanPoseHandler(tempAnimator.avatar, tempAnimator.transform);
                HumanPose basePose = new HumanPose();
                poseHandler.GetHumanPose(ref basePose);

                outputSomaBoneClip.ClearCurves();
                outputSomaBoneClip.legacy = false;
                outputSomaBoneClip.frameRate = effectiveSampleRate;

                AnimationCurve[] posX = new AnimationCurve[Soma77Names.Length];
                AnimationCurve[] posY = new AnimationCurve[Soma77Names.Length];
                AnimationCurve[] posZ = new AnimationCurve[Soma77Names.Length];
                AnimationCurve[] rotX = new AnimationCurve[Soma77Names.Length];
                AnimationCurve[] rotY = new AnimationCurve[Soma77Names.Length];
                AnimationCurve[] rotZ = new AnimationCurve[Soma77Names.Length];
                AnimationCurve[] rotW = new AnimationCurve[Soma77Names.Length];

                for (int i = 0; i < Soma77Names.Length; i++)
                {
                    posX[i] = new AnimationCurve();
                    posY[i] = new AnimationCurve();
                    posZ[i] = new AnimationCurve();
                    rotX[i] = new AnimationCurve();
                    rotY[i] = new AnimationCurve();
                    rotZ[i] = new AnimationCurve();
                    rotW[i] = new AnimationCurve();
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float t = FrameToTime(frame, frameCount, duration);
                    HumanPose framePose = BuildPoseAtTime(curveLookup, basePose, t);
                    poseHandler.SetHumanPose(ref framePose);

                    for (int i = 0; i < somaBones.Length; i++)
                    {
                        Transform bone = somaBones[i];
                        Vector3 lp = bone.localPosition;
                        Quaternion lr = bone.localRotation;

                        posX[i].AddKey(t, lp.x);
                        posY[i].AddKey(t, lp.y);
                        posZ[i].AddKey(t, lp.z);

                        rotX[i].AddKey(t, lr.x);
                        rotY[i].AddKey(t, lr.y);
                        rotZ[i].AddKey(t, lr.z);
                        rotW[i].AddKey(t, lr.w);
                    }
                }

                for (int i = 0; i < Soma77Names.Length; i++)
                {
                    string path = CanonicalSomaPaths[i];
                    bool isRoot = Soma77Parents[i] < 0;

                    if (isRoot)
                    {
                        outputSomaBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", posX[i]);
                        outputSomaBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", posY[i]);
                        outputSomaBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", posZ[i]);
                    }

                    outputSomaBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rotX[i]);
                    outputSomaBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rotY[i]);
                    outputSomaBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rotZ[i]);
                    outputSomaBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rotW[i]);
                }

                outputSomaBoneClip.EnsureQuaternionContinuity();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Muscle->Bone conversion failed: {ex.Message}";
                return false;
            }
            finally
            {
                DestroyTempRig(tempRig);
            }
        }

        private static HumanPose BuildPoseAtTime(Dictionary<string, AnimationCurve> curveLookup, HumanPose basePose, float t)
        {
            HumanPose pose = basePose;

            for (int i = 0; i < HumanTrait.MuscleCount && i < pose.muscles.Length; i++)
            {
                string prop = MusclePropertyNames[i];
                if (curveLookup.TryGetValue(prop, out AnimationCurve curve) && curve != null)
                {
                    pose.muscles[i] = curve.Evaluate(t);
                }
            }

            Vector3 bodyPos = pose.bodyPosition;
            if (TryEvaluate(curveLookup, "RootT.x", t, out float tx)) bodyPos.x = tx;
            if (TryEvaluate(curveLookup, "RootT.y", t, out float ty)) bodyPos.y = ty;
            if (TryEvaluate(curveLookup, "RootT.z", t, out float tz)) bodyPos.z = tz;
            pose.bodyPosition = bodyPos;

            Quaternion bodyRot = pose.bodyRotation;
            if (TryEvaluate(curveLookup, "RootQ.x", t, out float qx)) bodyRot.x = qx;
            if (TryEvaluate(curveLookup, "RootQ.y", t, out float qy)) bodyRot.y = qy;
            if (TryEvaluate(curveLookup, "RootQ.z", t, out float qz)) bodyRot.z = qz;
            if (TryEvaluate(curveLookup, "RootQ.w", t, out float qw)) bodyRot.w = qw;
            if (bodyRot != Quaternion.identity)
            {
                bodyRot.Normalize();
            }
            pose.bodyRotation = bodyRot;

            return pose;
        }

        private static bool TryEvaluate(Dictionary<string, AnimationCurve> lookup, string prop, float t, out float value)
        {
            value = 0f;
            if (!lookup.TryGetValue(prop, out AnimationCurve curve) || curve == null)
            {
                return false;
            }

            value = curve.Evaluate(t);
            return true;
        }

        private static Dictionary<string, AnimationCurve> BuildAnimatorCurveLookup(AnimationClip clip)
        {
            var lookup = new Dictionary<string, AnimationCurve>(StringComparer.Ordinal);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (binding.type != typeof(Animator))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(binding.path))
                {
                    continue;
                }

                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                lookup[binding.propertyName] = curve;
            }
            return lookup;
        }

        private static bool ValidateInputs(AnimationClip inputClip, Animator animator, AnimationClip outputClip, out string error)
        {
            error = string.Empty;
            if (inputClip == null)
            {
                error = "Input clip is null.";
                return false;
            }

            if (outputClip == null)
            {
                error = "Output clip is null.";
                return false;
            }

            if (animator == null)
            {
                error = "Animator is null.";
                return false;
            }

            if (animator.avatar == null)
            {
                error = "Animator avatar is null.";
                return false;
            }

            if (!animator.avatar.isValid || !animator.avatar.isHuman)
            {
                error = "Animator avatar must be a valid Humanoid avatar.";
                return false;
            }

            return true;
        }

        private static bool TryCreateSamplingRig(Animator sourceAnimator, out GameObject tempRig, out Animator tempAnimator, out string error)
        {
            error = string.Empty;
            tempRig = null;
            tempAnimator = null;

            try
            {
                tempRig = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
                tempRig.name = TempRigName;
                tempRig.hideFlags = HideFlags.HideAndDontSave;
                tempRig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                tempRig.transform.localScale = Vector3.one;

                tempAnimator = tempRig.GetComponent<Animator>();
                if (tempAnimator == null)
                {
                    error = "Failed to clone Animator from source object.";
                    DestroyTempRig(tempRig);
                    tempRig = null;
                    return false;
                }

                tempAnimator.enabled = false;
                tempAnimator.applyRootMotion = false;
                tempAnimator.Rebind();
                tempAnimator.Update(0f);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to create temporary sampling rig: {ex.Message}";
                DestroyTempRig(tempRig);
                tempRig = null;
                tempAnimator = null;
                return false;
            }
        }

        private static void DestroyTempRig(GameObject tempRig)
        {
            if (tempRig == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(tempRig);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(tempRig);
            }
        }

        private static bool TryFindSomaBones(Transform root, out Transform[] somaBones, out string error)
        {
            error = string.Empty;
            somaBones = new Transform[Soma77Names.Length];

            var nameToTransform = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (!nameToTransform.ContainsKey(t.name))
                {
                    nameToTransform[t.name] = t;
                }
            }

            List<string> missing = new List<string>();
            for (int i = 0; i < Soma77Names.Length; i++)
            {
                if (!nameToTransform.TryGetValue(Soma77Names[i], out Transform t))
                {
                    missing.Add(Soma77Names[i]);
                }
                else
                {
                    somaBones[i] = t;
                }
            }

            if (missing.Count > 0)
            {
                string preview = string.Join(", ", missing.Take(10));
                string suffix = missing.Count > 10 ? " ..." : string.Empty;
                error = $"Missing SOMA77 bones on avatar: {preview}{suffix}";
                return false;
            }

            return true;
        }

        private static string[] BuildCanonicalSomaPaths()
        {
            string[] paths = new string[Soma77Names.Length];
            for (int i = 0; i < Soma77Names.Length; i++)
            {
                paths[i] = BuildPathRecursive(i, paths);
            }
            return paths;
        }

        private static string BuildPathRecursive(int index, string[] cache)
        {
            if (!string.IsNullOrEmpty(cache[index]))
            {
                return cache[index];
            }

            int parent = Soma77Parents[index];
            if (parent < 0)
            {
                cache[index] = $"SOMA/{Soma77Names[index]}";
            }
            else
            {
                cache[index] = $"{BuildPathRecursive(parent, cache)}/{Soma77Names[index]}";
            }

            return cache[index];
        }

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

        private static float ResolveSampleRate(float preferred, float clipFrameRate)
        {
            if (preferred > 0f)
            {
                return preferred;
            }

            if (clipFrameRate > 0f)
            {
                return clipFrameRate;
            }

            return DefaultSampleRate;
        }

        private static float ResolveDuration(AnimationClip clip, float sampleRate)
        {
            float duration = clip != null ? clip.length : 0f;
            if (duration > 0f)
            {
                return duration;
            }

            return 1f / Mathf.Max(1f, sampleRate);
        }

        private static int CalculateFrameCount(float duration, float sampleRate)
        {
            int count = Mathf.RoundToInt(duration * sampleRate) + 1;
            return Mathf.Max(2, count);
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
    }
}