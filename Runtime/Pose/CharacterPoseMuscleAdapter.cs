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

        public static CharacterPose FromMuscleSample(MuscleSample sample, SkeletonCache cache)
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            Vector3 leftHandPosition = Vector3.zero;
            Quaternion leftHandRotation = Quaternion.identity;
            Vector3 rightHandPosition = Vector3.zero;
            Quaternion rightHandRotation = Quaternion.identity;
            Vector3 leftFootPosition = sample.leftFootPosition;
            Quaternion leftFootRotation = sample.leftFootRotation;
            Vector3 rightFootPosition = sample.rightFootPosition;
            Quaternion rightFootRotation = sample.rightFootRotation;
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
                root = new CharacterPoseTransform
                {
                    t = sample.pose.bodyPosition,
                    q = sample.pose.bodyRotation
                },
                hands = new CharacterPoseSides
                {
                    left = new CharacterPoseTransform { t = leftHandPosition, q = leftHandRotation },
                    right = new CharacterPoseTransform { t = rightHandPosition, q = rightHandRotation }
                },
                feet = new CharacterPoseSides
                {
                    left = new CharacterPoseTransform { t = leftFootPosition, q = leftFootRotation },
                    right = new CharacterPoseTransform { t = rightFootPosition, q = rightFootRotation }
                }
            };

            float[] source = sample.pose.muscles;
            for (int i = 0; i < UnityBodyMuscleIndices.Length; i++)
            {
                int sourceIndex = UnityBodyMuscleIndices[i];
                result.muscles[i] = source != null && sourceIndex < source.Length ? source[sourceIndex] : 0f;
            }

            return result;
        }

        /// <summary>Encodes the canonical body/root/foot payload into the
        /// fixed 70-value sampleData layout.</summary>
        public static float[] ToSampleData(CharacterPose pose)
        {
            if (pose == null)
            {
                throw new ArgumentNullException(nameof(pose));
            }
            if (!pose.TryValidate(out string error))
            {
                throw new InvalidOperationException(error);
            }

            float[] data = KimodoSampleDataLayout.CreateBuffer();
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
                pose.feet.left.t,
                pose.feet.left.q.normalized);
            KimodoSampleDataLayout.SetTransform(
                data,
                KimodoSampleDataLayout.RightFootTqOffset,
                pose.feet.right.t,
                pose.feet.right.q.normalized);
            return data;
        }

        public static float[] ToSampleData(MuscleSample sample, SkeletonCache cache = null)
        {
            return ToSampleData(FromMuscleSample(sample, cache));
        }

        /// <summary>Decodes valid 70-value data into the legacy CharacterPose
        /// adapter boundary. Hand effectors remain separate channels.</summary>
        public static bool TryFromSampleData(
            float[] data,
            out CharacterPose pose,
            out string error)
        {
            pose = null;
            if (!KimodoSampleDataLayout.TryValidate(data, out error))
            {
                return false;
            }

            pose = new CharacterPose();
            Array.Copy(data, KimodoSampleDataLayout.BodyMuscleOffset,
                pose.muscles, 0, KimodoSampleDataLayout.BodyMuscleCount);
            KimodoSampleDataLayout.GetTransform(
                data,
                KimodoSampleDataLayout.RootTqOffset,
                out pose.root.t,
                out pose.root.q);
            KimodoSampleDataLayout.GetTransform(
                data,
                KimodoSampleDataLayout.LeftFootTqOffset,
                out pose.feet.left.t,
                out pose.feet.left.q);
            KimodoSampleDataLayout.GetTransform(
                data,
                KimodoSampleDataLayout.RightFootTqOffset,
                out pose.feet.right.t,
                out pose.feet.right.q);

            if (!pose.TryValidate(out error))
            {
                pose = null;
                return false;
            }
            return true;
        }

        public static MuscleSample ToMuscleSample(CharacterPose pose)
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

            return new MuscleSample
            {
                pose = new HumanPose
                {
                    bodyPosition = pose.root.t,
                    bodyRotation = pose.root.q.normalized,
                    muscles = unityMuscles
                },
                leftFootPosition = pose.feet.left.t,
                leftFootRotation = pose.feet.left.q.normalized,
                rightFootPosition = pose.feet.right.t,
                rightFootRotation = pose.feet.right.q.normalized
            };
        }
    }
}
