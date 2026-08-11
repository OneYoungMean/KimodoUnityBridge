using System;
using UnityEngine;

namespace CharacterAnimationCli.Unity
{
    [Serializable]
    public sealed class CharacterPoseTransform
    {
        public Vector3 t;
        public Quaternion q = Quaternion.identity;

        internal CharacterPoseTransform Clone()
        {
            return new CharacterPoseTransform { t = t, q = q };
        }
    }

    [Serializable]
    public sealed class CharacterPoseSides
    {
        public CharacterPoseTransform left = new CharacterPoseTransform();
        public CharacterPoseTransform right = new CharacterPoseTransform();

        internal CharacterPoseSides Clone()
        {
            return new CharacterPoseSides
            {
                left = left != null ? left.Clone() : new CharacterPoseTransform(),
                right = right != null ? right.Clone() : new CharacterPoseTransform()
            };
        }
    }

    /// <summary>
    /// Canonical Unity pose payload. Its channels have the same meaning as a
    /// humanoid MuscleClip: 49 body muscles plus Root, Hand and Foot T/Q.
    /// </summary>
    [Serializable]
    public sealed class CharacterPose
    {
        public const int MuscleCount = 49;

        public float[] muscles = new float[MuscleCount];
        public CharacterPoseTransform root = new CharacterPoseTransform();
        public CharacterPoseSides hands = new CharacterPoseSides();
        public CharacterPoseSides feet = new CharacterPoseSides();

        public CharacterPose Clone()
        {
            var copy = new CharacterPose
            {
                muscles = muscles != null ? (float[])muscles.Clone() : null,
                root = root != null ? root.Clone() : null,
                hands = hands != null ? hands.Clone() : null,
                feet = feet != null ? feet.Clone() : null
            };
            return copy;
        }

        public bool TryValidate(out string error)
        {
            if (muscles == null || muscles.Length != MuscleCount)
            {
                error = $"muscles must contain exactly {MuscleCount} values.";
                return false;
            }

            for (int i = 0; i < muscles.Length; i++)
            {
                if (!IsFinite(muscles[i]))
                {
                    error = $"muscles[{i}] must be finite.";
                    return false;
                }
            }

            if (!TryValidateTransform(root, "root", out error) ||
                hands == null ||
                !TryValidateTransform(hands.left, "hands.left", out error) ||
                !TryValidateTransform(hands.right, "hands.right", out error) ||
                feet == null ||
                !TryValidateTransform(feet.left, "feet.left", out error) ||
                !TryValidateTransform(feet.right, "feet.right", out error))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = hands == null ? "hands is required." : "feet is required.";
                }
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryValidateTransform(CharacterPoseTransform value, string name, out string error)
        {
            if (value == null)
            {
                error = $"{name} is required.";
                return false;
            }
            if (!IsFinite(value.t.x) || !IsFinite(value.t.y) || !IsFinite(value.t.z))
            {
                error = $"{name}.t must contain finite values.";
                return false;
            }
            if (!IsFinite(value.q.x) || !IsFinite(value.q.y) || !IsFinite(value.q.z) || !IsFinite(value.q.w) ||
                value.q.x * value.q.x + value.q.y * value.q.y +
                value.q.z * value.q.z + value.q.w * value.q.w <= 1e-8f)
            {
                error = $"{name}.q must be a finite, non-zero quaternion in [x,y,z,w] order.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
