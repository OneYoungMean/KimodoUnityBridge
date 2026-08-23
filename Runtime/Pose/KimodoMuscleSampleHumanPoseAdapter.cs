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
        public static readonly int[] UnityBodyMuscleIndices =
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
            // Old/default samples can contain an all-zero root quaternion.
            // Unity accepts the float payload but produces NaN transforms
            // while evaluating the Humanoid clip. Use the neutral rotation at
            // this API boundary instead of letting NaN reach a Transform.
            float rotationMagnitude = pose.bodyRotation.x * pose.bodyRotation.x +
                pose.bodyRotation.y * pose.bodyRotation.y +
                pose.bodyRotation.z * pose.bodyRotation.z +
                pose.bodyRotation.w * pose.bodyRotation.w;
            if (!IsFinite(pose.bodyRotation) || rotationMagnitude < 1e-8f)
            {
                pose.bodyRotation = Quaternion.identity;
            }
            else
            {
                pose.bodyRotation = pose.bodyRotation.normalized;
            }
            return pose;
        }

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) &&
            IsFinite(value.z) && IsFinite(value.w);

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        internal static void EnsureMuscles(ref HumanPose pose)
        {
            if (pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount)
            {
                pose.muscles = new float[HumanTrait.MuscleCount];
            }
        }
    }
}
