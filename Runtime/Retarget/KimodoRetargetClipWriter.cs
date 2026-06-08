using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetClipWriter
    {
        internal static bool WriteMuscleCurves(IReadOnlyList<MuscleSample> samples, AnimationClip clip, out string error)
        {
            error = string.Empty;
            if (samples == null || samples.Count == 0)
            {
                error = "Muscle samples are empty.";
                return false;
            }

            string[] muscleNames = HumanTrait.MuscleName;
            int muscleCount = Mathf.Min(HumanTrait.MuscleCount, muscleNames != null ? muscleNames.Length : 0);
            if (muscleCount <= 0)
            {
                error = "HumanTrait muscle list is empty.";
                return false;
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

            var muscleCurves = new AnimationCurve[muscleCount];
            for (int i = 0; i < muscleCount; i++)
            {
                muscleCurves[i] = new AnimationCurve();
            }

            float frameRate = clip.frameRate > 0f ? clip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
            for (int frame = 0; frame < samples.Count; frame++)
            {
                MuscleSample sample = samples[frame];
                if (sample == null)
                {
                    continue;
                }

                float time = (frame - 1f) / frameRate;
                HumanPose pose = sample.pose;
                EnsureHumanPoseMuscles(ref pose);

                rootTx.AddKey(time, pose.bodyPosition.x);
                rootTy.AddKey(time, pose.bodyPosition.y);
                rootTz.AddKey(time, pose.bodyPosition.z);
                rootQx.AddKey(time, pose.bodyRotation.x);
                rootQy.AddKey(time, pose.bodyRotation.y);
                rootQz.AddKey(time, pose.bodyRotation.z);
                rootQw.AddKey(time, pose.bodyRotation.w);
                leftFootTx.AddKey(time, sample.leftFootPosition.x);
                leftFootTy.AddKey(time, sample.leftFootPosition.y);
                leftFootTz.AddKey(time, sample.leftFootPosition.z);
                leftFootQx.AddKey(time, sample.leftFootRotation.x);
                leftFootQy.AddKey(time, sample.leftFootRotation.y);
                leftFootQz.AddKey(time, sample.leftFootRotation.z);
                leftFootQw.AddKey(time, sample.leftFootRotation.w);
                rightFootTx.AddKey(time, sample.rightFootPosition.x);
                rightFootTy.AddKey(time, sample.rightFootPosition.y);
                rightFootTz.AddKey(time, sample.rightFootPosition.z);
                rightFootQx.AddKey(time, sample.rightFootRotation.x);
                rightFootQy.AddKey(time, sample.rightFootRotation.y);
                rightFootQz.AddKey(time, sample.rightFootRotation.z);
                rightFootQw.AddKey(time, sample.rightFootRotation.w);

                for (int muscle = 0; muscle < muscleCount; muscle++)
                {
                    float value = muscle < pose.muscles.Length ? pose.muscles[muscle] : 0f;
                    muscleCurves[muscle].AddKey(time, value);
                }
            }

            SetFloatCurve(clip, "RootT.x", rootTx);
            SetFloatCurve(clip, "RootT.y", rootTy);
            SetFloatCurve(clip, "RootT.z", rootTz);
            SetFloatCurve(clip, "RootQ.x", rootQx);
            SetFloatCurve(clip, "RootQ.y", rootQy);
            SetFloatCurve(clip, "RootQ.z", rootQz);
            SetFloatCurve(clip, "RootQ.w", rootQw);
            SetFloatCurve(clip, "LeftFootT.x", leftFootTx);
            SetFloatCurve(clip, "LeftFootT.y", leftFootTy);
            SetFloatCurve(clip, "LeftFootT.z", leftFootTz);
            SetFloatCurve(clip, "LeftFootQ.x", leftFootQx);
            SetFloatCurve(clip, "LeftFootQ.y", leftFootQy);
            SetFloatCurve(clip, "LeftFootQ.z", leftFootQz);
            SetFloatCurve(clip, "LeftFootQ.w", leftFootQw);
            SetFloatCurve(clip, "RightFootT.x", rightFootTx);
            SetFloatCurve(clip, "RightFootT.y", rightFootTy);
            SetFloatCurve(clip, "RightFootT.z", rightFootTz);
            SetFloatCurve(clip, "RightFootQ.x", rightFootQx);
            SetFloatCurve(clip, "RightFootQ.y", rightFootQy);
            SetFloatCurve(clip, "RightFootQ.z", rightFootQz);
            SetFloatCurve(clip, "RightFootQ.w", rightFootQw);

            for (int muscle = 0; muscle < muscleCount; muscle++)
            {
                string muscleName = GetAnimatorMusclePropertyName(muscleNames[muscle]);
                if (!string.IsNullOrWhiteSpace(muscleName))
                {
                    SetFloatCurve(clip, muscleName, muscleCurves[muscle]);
                }
            }

            return true;
        }

        internal static bool WriteBoneCurves(IReadOnlyList<BoneSample> samples, AnimationClip clip, out string error)
        {
            error = string.Empty;
            if (samples == null || samples.Count == 0)
            {
                error = "Bone samples are empty.";
                return false;
            }

            BoneSample first = samples[0];
            if (!ValidateBoneSampleForWrite(first, out error))
            {
                return false;
            }

            float frameRate = clip.frameRate > 0f ? clip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
            string[] boneNames = first.boneNames;
            AnimationCurve rootTx = new AnimationCurve();
            AnimationCurve rootTy = new AnimationCurve();
            AnimationCurve rootTz = new AnimationCurve();
            AnimationCurve rootQx = new AnimationCurve();
            AnimationCurve rootQy = new AnimationCurve();
            AnimationCurve rootQz = new AnimationCurve();
            AnimationCurve rootQw = new AnimationCurve();

            for (int i = 0; i < boneNames.Length; i++)
            {
                if (i == 0)
                {
                    for (int frame = 0; frame < samples.Count; frame++)
                    {
                        BoneSample sample = samples[frame];
                        if (!IsBoneSampleFrameUsable(sample))
                        {
                            continue;
                        }

                        float time = (frame - 1f) / frameRate;
                        Vector3 rootPosition = sample.localPositions[0];
                        Quaternion rootRotation = sample.localRotations[0];
                        rootTx.AddKey(time, rootPosition.x);
                        rootTy.AddKey(time, rootPosition.y);
                        rootTz.AddKey(time, rootPosition.z);
                        rootQx.AddKey(time, rootRotation.x);
                        rootQy.AddKey(time, rootRotation.y);
                        rootQz.AddKey(time, rootRotation.z);
                        rootQw.AddKey(time, rootRotation.w);
                    }

                    continue;
                }

                AnimationCurve posX = new AnimationCurve();
                AnimationCurve posY = new AnimationCurve();
                AnimationCurve posZ = new AnimationCurve();
                AnimationCurve rotX = new AnimationCurve();
                AnimationCurve rotY = new AnimationCurve();
                AnimationCurve rotZ = new AnimationCurve();
                AnimationCurve rotW = new AnimationCurve();

                for (int frame = 0; frame < samples.Count; frame++)
                {
                    BoneSample sample = samples[frame];
                    if (sample == null || !sample.IsValid || i >= sample.localPositions.Length || i >= sample.localRotations.Length)
                    {
                        continue;
                    }

                    float time = (frame - 1f) / frameRate;
                    Vector3 localPosition = sample.localPositions[i];
                    Quaternion localRotation = sample.localRotations[i];
                    posX.AddKey(time, localPosition.x);
                    posY.AddKey(time, localPosition.y);
                    posZ.AddKey(time, localPosition.z);
                    rotX.AddKey(time, localRotation.x);
                    rotY.AddKey(time, localRotation.y);
                    rotZ.AddKey(time, localRotation.z);
                    rotW.AddKey(time, localRotation.w);
                }

                string path = boneNames[i];
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", posX);
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", posY);
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", posZ);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rotX);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rotY);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rotZ);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rotW);
            }

            SetFloatCurve(clip, "MotionT.x", rootTx);
            SetFloatCurve(clip, "MotionT.y", rootTy);
            SetFloatCurve(clip, "MotionT.z", rootTz);
            SetFloatCurve(clip, "MotionQ.x", rootQx);
            SetFloatCurve(clip, "MotionQ.y", rootQy);
            SetFloatCurve(clip, "MotionQ.z", rootQz);
            SetFloatCurve(clip, "MotionQ.w", rootQw);

            return true;
        }

        internal static void SetFloatCurve(AnimationClip clip, string propertyName, AnimationCurve curve)
        {
            clip.SetCurve(string.Empty, typeof(Animator), propertyName, curve);
        }

        internal static string GetAnimatorMusclePropertyName(string muscleName)
        {
            if (string.IsNullOrWhiteSpace(muscleName))
            {
                return string.Empty;
            }

            if (TryConvertFingerMusclePropertyName(muscleName, out string propertyName))
            {
                return propertyName;
            }

            return muscleName;
        }

        internal static bool TryConvertFingerMusclePropertyName(string muscleName, out string propertyName)
        {
            propertyName = null;

            string[] tokens = muscleName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3 || tokens.Length > 4)
            {
                return false;
            }

            string side = tokens[0];
            if (!string.Equals(side, "Left", StringComparison.Ordinal) &&
                !string.Equals(side, "Right", StringComparison.Ordinal))
            {
                return false;
            }

            string finger = tokens[1];
            if (!string.Equals(finger, "Thumb", StringComparison.Ordinal) &&
                !string.Equals(finger, "Index", StringComparison.Ordinal) &&
                !string.Equals(finger, "Middle", StringComparison.Ordinal) &&
                !string.Equals(finger, "Ring", StringComparison.Ordinal) &&
                !string.Equals(finger, "Little", StringComparison.Ordinal))
            {
                return false;
            }

            if (tokens.Length == 3 && string.Equals(tokens[2], "Spread", StringComparison.Ordinal))
            {
                propertyName = $"{side}Hand.{finger}.Spread";
                return true;
            }

            if (tokens.Length == 4 &&
                (string.Equals(tokens[2], "1", StringComparison.Ordinal) ||
                 string.Equals(tokens[2], "2", StringComparison.Ordinal) ||
                 string.Equals(tokens[2], "3", StringComparison.Ordinal)) &&
                string.Equals(tokens[3], "Stretched", StringComparison.Ordinal))
            {
                propertyName = $"{side}Hand.{finger}.{tokens[2]} Stretched";
                return true;
            }

            return false;
        }

        internal static void EnsureHumanPoseMuscles(ref HumanPose pose)
        {
            if (pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount)
            {
                pose.muscles = new float[HumanTrait.MuscleCount];
            }
        }

        private static bool ValidateBoneSampleForWrite(BoneSample sample, out string error)
        {
            error = string.Empty;
            if (sample == null || !sample.IsValid)
            {
                error = "Bone sample is invalid.";
                return false;
            }

            return true;
        }

        private static bool IsBoneSampleFrameUsable(BoneSample sample)
        {
            return sample != null &&
                sample.IsValid &&
                sample.localPositions.Length > 0 &&
                sample.localRotations.Length > 0;
        }
    }
}
