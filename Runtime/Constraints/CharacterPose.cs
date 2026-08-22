using System;
using KimodoBridge;
using UnityEngine;

namespace CharacterAnimationCli.Unity
{
    /// <summary>Minimal position/rotation value with no hierarchy or IK semantics.</summary>
    [Serializable]
    public sealed class KimodoRigidTransform
    {
        public Vector3 position;
        public Quaternion rotation;

        public Vector3 t
        {
            get => position;
            set => position = value;
        }

        public Quaternion q
        {
            get => rotation;
            set => rotation = value;
        }

        public static KimodoRigidTransform Identity => new KimodoRigidTransform
        {
            position = Vector3.zero,
            rotation = Quaternion.identity
        };

        public KimodoRigidTransform Clone() => new KimodoRigidTransform
        {
            position = position,
            rotation = rotation
        };
    }

    [Serializable]
    public sealed class CharacterPoseSides
    {
        public KimodoRigidTransform left = KimodoRigidTransform.Identity;
        public KimodoRigidTransform right = KimodoRigidTransform.Identity;

        internal CharacterPoseSides Clone()
        {
            return new CharacterPoseSides
            {
                left = left.Clone(),
                right = right.Clone()
            };
        }
    }

    /// <summary>
    /// Canonical Unity pose payload. Its channels have the same meaning as a
    /// canonical pose: body muscles plus Root; hand/foot values are effector
    /// transport channels retained for protocol compatibility.
    /// </summary>
    [Serializable]
    public sealed class CharacterPose
    {
        public const int MuscleCount = 49;

        // Command-layer sampling metadata. These fields identify where the
        // pose came from; the 70D MuscleSample remains the atomic animation
        // payload used by Runtime/Retarget.
        [NonSerialized] public string sampledTrack;
        [NonSerialized] public double sampledTime;
        [NonSerialized] public MuscleSample muscleSample;

        public float[] muscles = new float[MuscleCount];
        public KimodoRigidTransform root = KimodoRigidTransform.Identity;
        public CharacterPoseSides hands = new CharacterPoseSides();
        public CharacterPoseSides feet = new CharacterPoseSides();

        public CharacterPose Clone()
        {
            var copy = new CharacterPose
            {
                muscles = muscles != null ? (float[])muscles.Clone() : null,
                root = root.Clone(),
                hands = hands != null ? hands.Clone() : null,
                feet = feet != null ? feet.Clone() : null,
                sampledTrack = sampledTrack,
                sampledTime = sampledTime,
                muscleSample = muscleSample?.Clone()
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

        private static bool TryValidateTransform(KimodoRigidTransform value, string name, out string error)
        {
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
