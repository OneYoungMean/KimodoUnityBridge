using System;
using KimodoBridge;
using UnityEngine;

namespace KimodoUnityBridge
{
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

        public float[] muscles = new float[MuscleCount];
        public KimodoRigidTransform root = KimodoRigidTransform.Identity;
        public KimodoRigidTransform leftHand = KimodoRigidTransform.Identity;
        public KimodoRigidTransform rightHand = KimodoRigidTransform.Identity;
        public KimodoRigidTransform leftFoot = KimodoRigidTransform.Identity;
        public KimodoRigidTransform rightFoot = KimodoRigidTransform.Identity;

        public CharacterPose Clone()
        {
            var copy = new CharacterPose
            {
                muscles = muscles != null ? (float[])muscles.Clone() : null,
                root = root.Clone(),
                leftHand = leftHand?.Clone() ?? KimodoRigidTransform.Identity,
                rightHand = rightHand?.Clone() ?? KimodoRigidTransform.Identity,
                leftFoot = leftFoot?.Clone() ?? KimodoRigidTransform.Identity,
                rightFoot = rightFoot?.Clone() ?? KimodoRigidTransform.Identity,
                sampledTrack = sampledTrack,
                sampledTime = sampledTime
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
                !TryValidateTransform(leftHand, "leftHand", out error) ||
                !TryValidateTransform(rightHand, "rightHand", out error) ||
                !TryValidateTransform(leftFoot, "leftFoot", out error) ||
                !TryValidateTransform(rightFoot, "rightFoot", out error))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "Effector transforms are required.";
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
