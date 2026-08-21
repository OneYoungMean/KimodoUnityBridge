using System;
using CharacterAnimationCli.Unity;
using UnityEngine;

namespace TimelineInject
{
    /// <summary>
    /// Canonical 70-float sample layout. The payload is deliberately fixed;
    /// channel validity is carried separately by KimodoSampleChannelMask.
    /// Each transform is translation (x,y,z) followed by quaternion (x,y,z,w).
    /// </summary>
    public static class KimodoSampleDataLayout
    {
        public const int BodyMuscleOffset = 0;
        public const int BodyMuscleCount = 49;
        public const int RootTqOffset = BodyMuscleOffset + BodyMuscleCount;
        public const int RootTqCount = 7;
        public const int LeftFootTqOffset = RootTqOffset + RootTqCount;
        public const int FootTqCount = 7;
        public const int RightFootTqOffset = LeftFootTqOffset + FootTqCount;
        public const int SampleDataLength = RightFootTqOffset + FootTqCount;

        public static float[] CreateBuffer() => new float[SampleDataLength];

        public static bool IsValidLength(float[] data) =>
            data != null && data.Length == SampleDataLength;

        public static void SetTransform(float[] data, int offset, Vector3 position, Quaternion rotation)
        {
            RequireBuffer(data, offset);
            data[offset] = position.x;
            data[offset + 1] = position.y;
            data[offset + 2] = position.z;
            data[offset + 3] = rotation.x;
            data[offset + 4] = rotation.y;
            data[offset + 5] = rotation.z;
            data[offset + 6] = rotation.w;
        }

        public static void GetTransform(
            float[] data,
            int offset,
            out Vector3 position,
            out Quaternion rotation)
        {
            RequireBuffer(data, offset);
            position = new Vector3(data[offset], data[offset + 1], data[offset + 2]);
            rotation = new Quaternion(
                data[offset + 3],
                data[offset + 4],
                data[offset + 5],
                data[offset + 6]);
        }

        public static bool TryValidate(float[] data, out string error)
        {
            if (!IsValidLength(data))
            {
                error = $"sampleData must contain exactly {SampleDataLength} values.";
                return false;
            }

            for (int i = 0; i < data.Length; i++)
            {
                if (float.IsNaN(data[i]) || float.IsInfinity(data[i]))
                {
                    error = $"sampleData[{i}] must be finite.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static bool TryDecodeCharacterPose(
            float[] data,
            out CharacterPose pose,
            out string error)
        {
            pose = null;
            if (!TryValidate(data, out error)) return false;
            pose = new CharacterPose();
            Array.Copy(data, BodyMuscleOffset, pose.muscles, 0, BodyMuscleCount);
            GetTransform(data, RootTqOffset, out pose.root.t, out pose.root.q);
            GetTransform(data, LeftFootTqOffset, out pose.feet.left.t, out pose.feet.left.q);
            GetTransform(data, RightFootTqOffset, out pose.feet.right.t, out pose.feet.right.q);
            if (!pose.TryValidate(out error))
            {
                pose = null;
                return false;
            }
            return true;
        }

        public static bool TryEncodeCharacterPose(
            CharacterPose pose,
            out float[] data,
            out string error)
        {
            data = CreateBuffer();
            error = string.Empty;
            if (pose == null)
            {
                error = "Character pose is null.";
                return false;
            }
            if (!pose.TryValidate(out error)) return false;
            Array.Copy(pose.muscles, 0, data, BodyMuscleOffset, BodyMuscleCount);
            SetTransform(data, RootTqOffset, pose.root.t, pose.root.q.normalized);
            SetTransform(data, LeftFootTqOffset, pose.feet.left.t, pose.feet.left.q.normalized);
            SetTransform(data, RightFootTqOffset, pose.feet.right.t, pose.feet.right.q.normalized);
            return TryValidate(data, out error);
        }

        private static void RequireBuffer(float[] data, int offset)
        {
            if (!IsValidLength(data) || offset < 0 || offset + 6 >= data.Length)
            {
                throw new ArgumentException("sampleData must be a valid 70-value buffer.", nameof(data));
            }
        }
    }

    [Serializable]
    public sealed class KimodoSampleChannelMask
    {
        public bool muscle49;
        public bool rootTQ;
        public bool leftFootTQ;
        public bool rightFootTQ;
        public bool root2DPosition;
        public bool root2DHeading;
        public bool leftHandEffector;
        public bool rightHandEffector;
        public bool leftFootEffector;
        public bool rightFootEffector;

        public KimodoSampleChannelMask Clone() => (KimodoSampleChannelMask)MemberwiseClone();

        public void NormalizeDependencies()
        {
            if (!root2DPosition)
            {
                root2DHeading = false;
            }
        }

        public bool IsValidForEffector(int index) => index switch
        {
            0 => leftHandEffector,
            1 => rightHandEffector,
            2 => leftFootEffector,
            3 => rightFootEffector,
            _ => false
        };

        public bool Any => muscle49 || rootTQ || leftFootTQ || rightFootTQ ||
            root2DPosition || root2DHeading || leftHandEffector ||
            rightHandEffector || leftFootEffector || rightFootEffector;

    }
}
