using System;
using System.Collections.Generic;
using CharacterAnimationCli.Unity;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetClipWriter
    {
        // Unity's remaining humanoid muscles cover jaw, eyes and fingers.
        private static readonly int[] GeneratedMuscleIndices = CharacterPoseMuscleAdapter.UnityBodyMuscleIndices;

        /// <summary>Writes the 49 body-muscle channels for standalone muscle
        /// clips. The retarget transport uses
        /// <see cref="WriteRetargetMuscleCurves"/> so root/foot channels are
        /// preserved there without changing this export-only contract.</summary>
        internal static bool WriteBodyMuscleCurves(IReadOnlyList<MuscleSample> samples, AnimationClip clip, out string error)
        {
            return WriteMuscleCurves(samples, clip, out error, includeRootTransform: false, includeFootIkGoals: false);
        }

        /// <summary>
        /// Writes the complete MuscleSample transport consumed by the existing
        /// retarget PlayableGraph: 49 body muscles plus RootT/RootQ and the two
        /// foot T/Q channels. This is still one retarget pipeline; these are
        /// the channels of its temporary clip, not a second solver.
        /// </summary>
        internal static bool WriteRetargetMuscleCurves(
            IReadOnlyList<MuscleSample> samples,
            AnimationClip clip,
            out string error)
        {
            return WriteMuscleCurves(samples, clip, out error, includeRootTransform: true, includeFootIkGoals: true);
        }

        private static bool WriteMuscleCurves(
            IReadOnlyList<MuscleSample> samples,
            AnimationClip clip,
            out string error,
            bool includeRootTransform,
            bool includeFootIkGoals)
        {
            if (!ValidateWriteInputs(samples, clip, "Muscle", out error))
            {
                return false;
            }

            string[] muscleNames = HumanTrait.MuscleName;
            if (muscleNames == null || muscleNames.Length == 0)
            {
                error = "HumanTrait muscle list is empty.";
                return false;
            }

            for (int i = 0; i < GeneratedMuscleIndices.Length; i++)
            {
                if (GeneratedMuscleIndices[i] < 0 || GeneratedMuscleIndices[i] >= muscleNames.Length)
                {
                    error = $"Generated muscle index {GeneratedMuscleIndices[i]} is not available in this Unity version.";
                    return false;
                }
            }

            AnimationCurve rootTx = includeRootTransform ? new AnimationCurve() : null;
            AnimationCurve rootTy = includeRootTransform ? new AnimationCurve() : null;
            AnimationCurve rootTz = includeRootTransform ? new AnimationCurve() : null;
            AnimationCurve rootQx = includeRootTransform ? new AnimationCurve() : null;
            AnimationCurve rootQy = includeRootTransform ? new AnimationCurve() : null;
            AnimationCurve rootQz = includeRootTransform ? new AnimationCurve() : null;
            AnimationCurve rootQw = includeRootTransform ? new AnimationCurve() : null;
            AnimationCurve leftFootTx = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve leftFootTy = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve leftFootTz = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve leftFootQx = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve leftFootQy = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve leftFootQz = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve leftFootQw = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve rightFootTx = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve rightFootTy = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve rightFootTz = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve rightFootQx = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve rightFootQy = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve rightFootQz = includeFootIkGoals ? new AnimationCurve() : null;
            AnimationCurve rightFootQw = includeFootIkGoals ? new AnimationCurve() : null;

            var muscleCurves = new AnimationCurve[GeneratedMuscleIndices.Length];
            for (int i = 0; i < muscleCurves.Length; i++)
            {
                muscleCurves[i] = new AnimationCurve();
            }

            float frameRate = KimodoRetargetClipSamplingUtility.ResolveFrameRate(clip);
            bool hasPreviousRootRotation = false;
            Quaternion previousRootRotation = Quaternion.identity;
            for (int frame = 0; frame < samples.Count; frame++)
            {
                MuscleSample sample = samples[frame];
                if (sample == null)
                {
                    continue;
                }

                float time = frame / frameRate;
                HumanPose pose = CharacterPoseMuscleAdapter.ToHumanPose(sample);
                if (includeRootTransform)
                {
                    Quaternion rootRotation = ResolveContinuousRotation(
                        pose.bodyRotation,
                        ref previousRootRotation,
                        ref hasPreviousRootRotation);
                    AddVector3Key(time, pose.bodyPosition, rootTx, rootTy, rootTz);
                    AddQuaternionKey(time, rootRotation, rootQx, rootQy, rootQz, rootQw);
                }
                if (includeFootIkGoals)
                {
                    sample.GetLeftFoot(out Vector3 leftFootPosition, out Quaternion leftFootRotation);
                    sample.GetRightFoot(out Vector3 rightFootPosition, out Quaternion rightFootRotation);
                    AddVector3Key(time, leftFootPosition, leftFootTx, leftFootTy, leftFootTz);
                    AddQuaternionKey(time, leftFootRotation, leftFootQx, leftFootQy, leftFootQz, leftFootQw);
                    AddVector3Key(time, rightFootPosition, rightFootTx, rightFootTy, rightFootTz);
                    AddQuaternionKey(time, rightFootRotation, rightFootQx, rightFootQy, rightFootQz, rightFootQw);
                }
                for (int muscle = 0; muscle < muscleCurves.Length; muscle++)
                {
                    int unityMuscleIndex = GeneratedMuscleIndices[muscle];
                    float value = unityMuscleIndex < pose.muscles.Length ? pose.muscles[unityMuscleIndex] : 0f;
                    muscleCurves[muscle].AddKey(time, value);
                }
            }

            if (includeRootTransform)
            {
                SetAnimatorVector3Curves(clip, "RootT", rootTx, rootTy, rootTz);
                SetAnimatorQuaternionCurves(clip, "RootQ", rootQx, rootQy, rootQz, rootQw);
            }
            if (includeFootIkGoals)
            {
                SetAnimatorVector3Curves(clip, "LeftFootT", leftFootTx, leftFootTy, leftFootTz);
                SetAnimatorQuaternionCurves(clip, "LeftFootQ", leftFootQx, leftFootQy, leftFootQz, leftFootQw);
                SetAnimatorVector3Curves(clip, "RightFootT", rightFootTx, rightFootTy, rightFootTz);
                SetAnimatorQuaternionCurves(clip, "RightFootQ", rightFootQx, rightFootQy, rightFootQz, rightFootQw);
            }

            for (int muscle = 0; muscle < muscleCurves.Length; muscle++)
            {
                string muscleName = GetAnimatorMusclePropertyName(muscleNames[GeneratedMuscleIndices[muscle]]);
                if (!string.IsNullOrWhiteSpace(muscleName))
                {
                    SetFloatCurve(clip, muscleName, muscleCurves[muscle]);
                }
            }

            return true;
        }

        internal static bool WriteBoneCurves(IReadOnlyList<BoneSample> samples, AnimationClip clip, out string error)
        {
            if (!ValidateWriteInputs(samples, clip, "Bone", out error))
            {
                return false;
            }

            BoneSample first = samples[0];
            if (!ValidateBoneSampleForWrite(first, out error))
            {
                return false;
            }

            float frameRate = KimodoRetargetClipSamplingUtility.ResolveFrameRate(clip);
            string[] boneNames = first.boneNames;

            for (int i = 0; i < boneNames.Length; i++)
            {
                AnimationCurve posX = new AnimationCurve();
                AnimationCurve posY = new AnimationCurve();
                AnimationCurve posZ = new AnimationCurve();
                AnimationCurve rotX = new AnimationCurve();
                AnimationCurve rotY = new AnimationCurve();
                AnimationCurve rotZ = new AnimationCurve();
                AnimationCurve rotW = new AnimationCurve();
                bool hasPreviousRotation = false;
                Quaternion previousRotation = Quaternion.identity;

                for (int frame = 0; frame < samples.Count; frame++)
                {
                    BoneSample sample = samples[frame];
                    if (!IsBoneSampleFrameUsable(sample, i))
                    {
                        continue;
                    }

                    float time = frame / frameRate;
                    Vector3 localPosition = sample.localPositions[i];
                    Quaternion localRotation = ResolveContinuousRotation(
                        sample.localRotations[i],
                        ref previousRotation,
                        ref hasPreviousRotation);
                    AddVector3Key(time, localPosition, posX, posY, posZ);
                    AddQuaternionKey(time, localRotation, rotX, rotY, rotZ, rotW);
                }

                SetTransformCurves(
                    clip,
                    i == 0 ? string.Empty : boneNames[i],
                    posX,
                    posY,
                    posZ,
                    rotX,
                    rotY,
                    rotZ,
                    rotW);
            }

            return true;
        }

        internal static void SetFloatCurve(AnimationClip clip, string propertyName, AnimationCurve curve)
        {
            clip.SetCurve(string.Empty, typeof(Animator), propertyName, curve);
        }

        private static void SetAnimatorVector3Curves(
            AnimationClip clip,
            string propertyPrefix,
            AnimationCurve x,
            AnimationCurve y,
            AnimationCurve z)
        {
            SetFloatCurve(clip, propertyPrefix + ".x", x);
            SetFloatCurve(clip, propertyPrefix + ".y", y);
            SetFloatCurve(clip, propertyPrefix + ".z", z);
        }

        private static void SetAnimatorQuaternionCurves(
            AnimationClip clip,
            string propertyPrefix,
            AnimationCurve x,
            AnimationCurve y,
            AnimationCurve z,
            AnimationCurve w)
        {
            SetFloatCurve(clip, propertyPrefix + ".x", x);
            SetFloatCurve(clip, propertyPrefix + ".y", y);
            SetFloatCurve(clip, propertyPrefix + ".z", z);
            SetFloatCurve(clip, propertyPrefix + ".w", w);
        }

        private static void AddVector3Key(
            float time,
            Vector3 value,
            AnimationCurve x,
            AnimationCurve y,
            AnimationCurve z)
        {
            x.AddKey(time, value.x);
            y.AddKey(time, value.y);
            z.AddKey(time, value.z);
        }

        private static void AddQuaternionKey(
            float time,
            Quaternion value,
            AnimationCurve x,
            AnimationCurve y,
            AnimationCurve z,
            AnimationCurve w)
        {
            x.AddKey(time, value.x);
            y.AddKey(time, value.y);
            z.AddKey(time, value.z);
            w.AddKey(time, value.w);
        }

        private static void SetTransformCurves(
            AnimationClip clip,
            string path,
            AnimationCurve posX,
            AnimationCurve posY,
            AnimationCurve posZ,
            AnimationCurve rotX,
            AnimationCurve rotY,
            AnimationCurve rotZ,
            AnimationCurve rotW)
        {
            clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", posX);
            clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", posY);
            clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", posZ);
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rotX);
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rotY);
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rotZ);
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rotW);
        }

        private static Quaternion ResolveContinuousRotation(
            Quaternion rotation,
            ref Quaternion previousRotation,
            ref bool hasPreviousRotation)
        {
            Quaternion result = rotation;
            if (hasPreviousRotation && Quaternion.Dot(previousRotation, result) < 0f)
            {
                result = new Quaternion(-result.x, -result.y, -result.z, -result.w);
            }

            previousRotation = result;
            hasPreviousRotation = true;
            return result;
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

        private static bool ValidateWriteInputs<TSample>(
            IReadOnlyList<TSample> samples,
            AnimationClip clip,
            string sampleKind,
            out string error)
        {
            error = string.Empty;
            if (samples == null || samples.Count == 0)
            {
                error = $"{sampleKind} samples are empty.";
                return false;
            }

            if (clip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            return true;
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

        private static bool IsBoneSampleFrameUsable(BoneSample sample, int boneIndex)
        {
            return sample != null &&
                sample.IsValid &&
                boneIndex >= 0 &&
                boneIndex < sample.localPositions.Length &&
                boneIndex < sample.localRotations.Length;
        }
    }
}
