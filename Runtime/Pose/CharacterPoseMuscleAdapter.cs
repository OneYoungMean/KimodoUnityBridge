using System;
using CharacterAnimationCli.Unity;
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
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
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
                    left = new CharacterPoseTransform { t = sample.leftHandPosition, q = sample.leftHandRotation },
                    right = new CharacterPoseTransform { t = sample.rightHandPosition, q = sample.rightHandRotation }
                },
                feet = new CharacterPoseSides
                {
                    left = new CharacterPoseTransform { t = sample.leftFootPosition, q = sample.leftFootRotation },
                    right = new CharacterPoseTransform { t = sample.rightFootPosition, q = sample.rightFootRotation }
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
                leftHandPosition = pose.hands.left.t,
                leftHandRotation = pose.hands.left.q.normalized,
                rightHandPosition = pose.hands.right.t,
                rightHandRotation = pose.hands.right.q.normalized,
                leftFootPosition = pose.feet.left.t,
                leftFootRotation = pose.feet.left.q.normalized,
                rightFootPosition = pose.feet.right.t,
                rightFootRotation = pose.feet.right.q.normalized
            };
        }
    }
}
