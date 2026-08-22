using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Runtime-only conversion between the canonical 70D MuscleSample and
    /// Unity's HumanPose API boundary. Command-layer CharacterPose is not
    /// involved in animation sampling or retarget evaluation.
    /// </summary>
    internal static class KimodoMuscleSampleHumanPoseAdapter
    {
        internal static readonly int[] UnityBodyMuscleIndices =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
            21, 22, 23, 24, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36,
            37, 38, 39, 40, 41, 42, 43, 44, 45,
            46, 47, 48, 49, 50, 51, 52, 53, 54
        };

        internal static HumanPose ToHumanPose(MuscleSample sample)
        {
            if (sample == null || !sample.IsValid)
            {
                throw new System.ArgumentException(
                    "MuscleSample must contain a valid 70D payload.",
                    nameof(sample));
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

        internal static void EnsureMuscles(ref HumanPose pose)
        {
            if (pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount)
            {
                pose.muscles = new float[HumanTrait.MuscleCount];
            }
        }
    }
}
