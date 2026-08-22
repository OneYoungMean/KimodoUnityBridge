using System;
using CharacterAnimationCli.Unity;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    public static class CharacterPoseMuscleAdapter
    {
        internal static readonly int[] UnityBodyMuscleIndices =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
            21, 22, 23, 24, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36,
            37, 38, 39, 40, 41, 42, 43, 44, 45,
            46, 47, 48, 49, 50, 51, 52, 53, 54
        };

        public static CharacterPose FromMuscleSample(MuscleSample sample)
        {
            return FromMuscleSample(sample, null);
        }

        public static CharacterPose FromMuscleSample(MuscleSample sample, RetargetSkeleton cache)
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            Vector3 leftHandPosition = Vector3.zero;
            Quaternion leftHandRotation = Quaternion.identity;
            Vector3 rightHandPosition = Vector3.zero;
            Quaternion rightHandRotation = Quaternion.identity;
            KimodoSampleDataLayout.GetTransform(
                sample.data,
                KimodoSampleDataLayout.LeftFootTqOffset,
                out Vector3 leftFootPosition,
                out Quaternion leftFootRotation);
            KimodoSampleDataLayout.GetTransform(
                sample.data,
                KimodoSampleDataLayout.RightFootTqOffset,
                out Vector3 rightFootPosition,
                out Quaternion rightFootRotation);
            if (cache != null)
            {
                cache.GetBonePose(HumanBodyBones.LeftHand, out leftHandPosition, out leftHandRotation);
                cache.GetBonePose(HumanBodyBones.RightHand, out rightHandPosition, out rightHandRotation);
                // Protocol effectors are emitted from the FK pose rebuilt from
                // current muscle data, not from stale authored FootT/Q goals.
                cache.GetBonePose(HumanBodyBones.LeftFoot, out leftFootPosition, out leftFootRotation);
                cache.GetBonePose(HumanBodyBones.RightFoot, out rightFootPosition, out rightFootRotation);
            }

            var result = new CharacterPose
            {
                muscleSample = sample.Clone(),
                root = new KimodoRigidTransform
                {
                    t = ReadRootPosition(sample),
                    q = ReadRootRotation(sample)
                },
                leftHand = new KimodoRigidTransform { t = leftHandPosition, q = leftHandRotation },
                rightHand = new KimodoRigidTransform { t = rightHandPosition, q = rightHandRotation },
                leftFoot = new KimodoRigidTransform { t = leftFootPosition, q = leftFootRotation },
                rightFoot = new KimodoRigidTransform { t = rightFootPosition, q = rightFootRotation }
            };

            for (int i = 0; i < UnityBodyMuscleIndices.Length; i++)
            {
                int sourceIndex = UnityBodyMuscleIndices[i];
                result.muscles[i] = i < KimodoSampleDataLayout.BodyMuscleCount
                    ? sample.data[i] : 0f;
            }

            return result;
        }

        internal static HumanPose ToHumanPose(MuscleSample sample)
        {
            if (sample == null || !sample.IsValid)
            {
                throw new ArgumentException("MuscleSample must contain a valid 70D payload.", nameof(sample));
            }

            var pose = new HumanPose
            {
                muscles = new float[HumanTrait.MuscleCount]
            };
            for (int i = 0; i < UnityBodyMuscleIndices.Length; i++)
            {
                pose.muscles[UnityBodyMuscleIndices[i]] = sample.data[i];
            }
            sample.GetRoot(out pose.bodyPosition, out pose.bodyRotation);
            return pose;
        }

        internal static void ToHumanPose(MuscleSample sample, out HumanPose pose)
        {
            pose = ToHumanPose(sample);
        }

        /// <summary>Encodes the canonical body/root/foot payload into the
        /// fixed 70-value sampleData layout.</summary>
        public static float[] ToSampleData(CharacterPose pose)
        {
            if (!TryToSampleData(pose, out float[] data, out string error))
            {
                throw new InvalidOperationException(error);
            }

            return data;
        }

        public static bool TryToSampleData(
            CharacterPose pose,
            out float[] data,
            out string error)
        {
            data = KimodoSampleDataLayout.CreateBuffer();
            if (pose == null)
            {
                error = "Character pose is null.";
                return false;
            }
            if (!pose.TryValidate(out error))
            {
                return false;
            }

            Array.Copy(pose.muscles, 0, data, KimodoSampleDataLayout.BodyMuscleOffset,
                KimodoSampleDataLayout.BodyMuscleCount);
            KimodoSampleDataLayout.SetTransform(
                data,
                KimodoSampleDataLayout.RootTqOffset,
                pose.root.t,
                pose.root.q.normalized);
            KimodoSampleDataLayout.SetTransform(
                data,
                KimodoSampleDataLayout.LeftFootTqOffset,
                pose.leftFoot.t,
                pose.leftFoot.q.normalized);
            KimodoSampleDataLayout.SetTransform(
                data,
                KimodoSampleDataLayout.RightFootTqOffset,
                pose.rightFoot.t,
                pose.rightFoot.q.normalized);
            return KimodoSampleDataLayout.TryValidate(data, out error);
        }

        public static float[] ToSampleData(MuscleSample sample, RetargetSkeleton cache = null)
        {
            return ToSampleData(FromMuscleSample(sample, cache));
        }

        /// <summary>Decodes valid 70-value data into the legacy CharacterPose
        /// adapter boundary. Hand effectors remain separate channels.</summary>
        public static bool TryFromSampleData(
            MuscleSample sample,
            out CharacterPose pose,
            out string error)
        {
            pose = null;
            if (sample == null)
            {
                error = "Muscle sample is null.";
                return false;
            }

            if (!KimodoSampleDataLayout.TryValidate(sample.data, out error))
            {
                return false;
            }

            pose = new CharacterPose
            {
                // CharacterPose is a command/JSON boundary DTO, but retain
                // the canonical atomic payload so callers do not need to
                // reconstruct it from the legacy split fields.
                muscleSample = sample.Clone()
            };
            Array.Copy(
                sample.data,
                KimodoSampleDataLayout.BodyMuscleOffset,
                pose.muscles,
                0,
                KimodoSampleDataLayout.BodyMuscleCount);
            KimodoSampleDataLayout.GetTransform(
                sample.data,
                KimodoSampleDataLayout.RootTqOffset,
                out Vector3 rootPosition,
                out Quaternion rootRotation);
            KimodoSampleDataLayout.GetTransform(
                sample.data,
                KimodoSampleDataLayout.LeftFootTqOffset,
                out Vector3 leftFootPosition,
                out Quaternion leftFootRotation);
            KimodoSampleDataLayout.GetTransform(
                sample.data,
                KimodoSampleDataLayout.RightFootTqOffset,
                out Vector3 rightFootPosition,
                out Quaternion rightFootRotation);
            pose.root.t = rootPosition;
            pose.root.q = rootRotation;
            pose.leftFoot.t = leftFootPosition;
            pose.leftFoot.q = leftFootRotation;
            pose.rightFoot.t = rightFootPosition;
            pose.rightFoot.q = rightFootRotation;

            if (!pose.TryValidate(out error))
            {
                pose = null;
                return false;
            }

            return true;
        }

        public static bool TryToSampleData(
            CharacterPose pose,
            MuscleSample sample,
            out string error)
        {
            if (!TryToSampleData(pose, out float[] data, out error))
            {
                return false;
            }
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }
            sample.data = data;
            return true;
        }

        public static MuscleSample ToMuscleSample(CharacterPose pose)
        {
            return ToMuscleSample(pose, includeTransformChannels: true);
        }

        /// <summary>
        /// Builds the muscle-only representation used by retarget sampling.
        /// This deliberately never reads or transports CharacterPose.root or
        /// the authored foot channels into MuscleSample -> BoneSample.
        /// </summary>
        public static MuscleSample ToBodyMuscleSample(CharacterPose pose)
        {
            return ToMuscleSample(pose, includeTransformChannels: false);
        }

        private static MuscleSample ToMuscleSample(
            CharacterPose pose,
            bool includeTransformChannels)
        {
            if (pose == null)
            {
                throw new ArgumentNullException(nameof(pose));
            }
            if (!pose.TryValidate(out string error))
            {
                throw new InvalidOperationException(error);
            }

            var unityMuscles = new float[HumanTrait.MuscleCount];
            for (int i = 0; i < UnityBodyMuscleIndices.Length; i++)
            {
                unityMuscles[UnityBodyMuscleIndices[i]] = pose.muscles[i];
            }

            var sample = new MuscleSample();
            for (int i = 0; i < UnityBodyMuscleIndices.Length; i++)
            {
                sample.data[i] = pose.muscles[i];
            }
            sample.SetRoot(
                includeTransformChannels ? pose.root.t : Vector3.zero,
                includeTransformChannels ? pose.root.q : Quaternion.identity);
            sample.SetLeftFoot(
                includeTransformChannels ? pose.leftFoot.t : Vector3.zero,
                includeTransformChannels ? pose.leftFoot.q : Quaternion.identity);
            sample.SetRightFoot(
                includeTransformChannels ? pose.rightFoot.t : Vector3.zero,
                includeTransformChannels ? pose.rightFoot.q : Quaternion.identity);
            return sample;
        }

        private static Vector3 ReadRootPosition(MuscleSample sample)
        {
            sample.GetRoot(out Vector3 position, out _);
            return position;
        }

        private static Quaternion ReadRootRotation(MuscleSample sample)
        {
            sample.GetRoot(out _, out Quaternion rotation);
            return rotation;
        }
    }
}
