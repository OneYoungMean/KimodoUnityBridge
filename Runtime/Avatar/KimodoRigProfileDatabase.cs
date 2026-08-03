using System;
using TimelineInject;

namespace KimodoBridge
{
    public static class KimodoRigProfileDatabase
    {
        private static readonly string[] Soma77Names =
        {
            "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "HeadEnd", "Jaw", "LeftEye", "RightEye",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandThumb1", "LeftHandThumb2", "LeftHandThumb3", "LeftHandThumbEnd",
            "LeftHandIndex1", "LeftHandIndex2", "LeftHandIndex3", "LeftHandIndex4", "LeftHandIndexEnd",
            "LeftHandMiddle1", "LeftHandMiddle2", "LeftHandMiddle3", "LeftHandMiddle4", "LeftHandMiddleEnd",
            "LeftHandRing1", "LeftHandRing2", "LeftHandRing3", "LeftHandRing4", "LeftHandRingEnd",
            "LeftHandPinky1", "LeftHandPinky2", "LeftHandPinky3", "LeftHandPinky4", "LeftHandPinkyEnd",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandThumb1", "RightHandThumb2", "RightHandThumb3", "RightHandThumbEnd",
            "RightHandIndex1", "RightHandIndex2", "RightHandIndex3", "RightHandIndex4", "RightHandIndexEnd",
            "RightHandMiddle1", "RightHandMiddle2", "RightHandMiddle3", "RightHandMiddle4", "RightHandMiddleEnd",
            "RightHandRing1", "RightHandRing2", "RightHandRing3", "RightHandRing4", "RightHandRingEnd",
            "RightHandPinky1", "RightHandPinky2", "RightHandPinky3", "RightHandPinky4", "RightHandPinkyEnd",
            "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase", "LeftToeEnd", "RightLeg", "RightShin", "RightFoot", "RightToeBase", "RightToeEnd"
        };

        private static readonly int[] Soma77Parents =
        {
            -1, 0, 1, 2, 3, 4, 5, 6, 6, 6, 6,
            3, 11, 12, 13, 14, 15, 16, 17,
            14, 19, 20, 21, 22,
            14, 24, 25, 26, 27,
            14, 29, 30, 31, 32,
            14, 34, 35, 36, 37,
            3, 39, 40, 41, 42, 43, 44, 45,
            42, 47, 48, 49, 50,
            42, 52, 53, 54, 55,
            42, 57, 58, 59, 60,
            42, 62, 63, 64, 65,
            0, 67, 68, 69, 70, 0, 72, 73, 74, 75
        };

        private static readonly string[] G1Skel34Names =
        {
            "pelvis_skel",
            "left_hip_pitch_skel", "left_hip_roll_skel", "left_hip_yaw_skel", "left_knee_skel", "left_ankle_pitch_skel", "left_ankle_roll_skel", "left_toe_base",
            "right_hip_pitch_skel", "right_hip_roll_skel", "right_hip_yaw_skel", "right_knee_skel", "right_ankle_pitch_skel", "right_ankle_roll_skel", "right_toe_base",
            "waist_yaw_skel", "waist_roll_skel", "waist_pitch_skel",
            "left_shoulder_pitch_skel", "left_shoulder_roll_skel", "left_shoulder_yaw_skel", "left_elbow_skel", "left_wrist_roll_skel", "left_wrist_pitch_skel", "left_wrist_yaw_skel", "left_hand_roll_skel",
            "right_shoulder_pitch_skel", "right_shoulder_roll_skel", "right_shoulder_yaw_skel", "right_elbow_skel", "right_wrist_roll_skel", "right_wrist_pitch_skel", "right_wrist_yaw_skel", "right_hand_roll_skel"
        };

        private static readonly int[] G1Skel34Parents =
        {
            -1,
            0, 1, 2, 3, 4, 5, 6,
            0, 8, 9, 10, 11, 12, 13,
            0, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24,
            17, 26, 27, 28, 29, 30, 31, 32
        };

        private static readonly string[] Core27Names =
        {
            "Hips", "Spine", "Spine1", "Spine2", "Spine3", "Neck", "Head",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandEnd", "RightHandThumb1",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandEnd", "LeftHandThumb1",
            "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase",
            "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase"
        };

        private static readonly int[] Core27Parents =
        {
            -1, 0, 1, 2, 3, 4, 5,
            4, 7, 8, 9, 10, 10,
            4, 13, 14, 15, 16, 16,
            0, 19, 20, 21,
            0, 23, 24, 25
        };

        private static readonly string[] Smplx22Names =
        {
            "pelvis",
            "left_hip", "right_hip", "spine1",
            "left_knee", "right_knee", "spine2",
            "left_ankle", "right_ankle", "spine3",
            "left_foot", "right_foot",
            "neck", "left_collar", "right_collar",
            "head", "left_shoulder", "right_shoulder",
            "left_elbow", "right_elbow",
            "left_wrist", "right_wrist"
        };

        private static readonly int[] Smplx22Parents =
        {
            -1,
            0, 0, 0,
            1, 2, 3,
            4, 5, 6,
            7, 8,
            9, 9, 9,
            12, 13, 14,
            16, 17,
            18, 19
        };

        public static KimodoConstraintRigType ResolveRigTypeFromModelName(string modelName)
        {
            string normalized = string.IsNullOrWhiteSpace(modelName)
                ? string.Empty
                : modelName.Trim().ToLowerInvariant();
            if (normalized.Contains("ardy-core") || normalized.Contains("cskel27"))
            {
                return KimodoConstraintRigType.Core27;
            }

            if (normalized.Contains("g1"))
            {
                return KimodoConstraintRigType.G1;
            }

            if (normalized.Contains("smplx"))
            {
                return KimodoConstraintRigType.Smplx;
            }

            return KimodoConstraintRigType.Soma77;
        }

        public static string[] GetJointNamesForModel(string modelName)
        {
            ResolveProfile(modelName, out _, out string[] jointNames, out _);
            return CloneStrings(jointNames);
        }

        public static int[] GetParentIndicesForModel(string modelName)
        {
            ResolveProfile(modelName, out _, out _, out int[] parentIndices);
            return CloneInts(parentIndices);
        }

        public static string GetProfileRootJointNameForModel(string modelName)
        {
            ResolveProfile(modelName, out _, out string[] jointNames, out _);
            return jointNames != null && jointNames.Length > 0 ? jointNames[0] : string.Empty;
        }

        public static void ResolveProfile(
            string modelName,
            out KimodoConstraintRigType rigType,
            out string[] jointNames,
            out int[] parentIndices)
        {
            rigType = ResolveRigTypeFromModelName(modelName);
            switch (rigType)
            {
                case KimodoConstraintRigType.Core27:
                    jointNames = Core27Names;
                    parentIndices = Core27Parents;
                    return;
                case KimodoConstraintRigType.G1:
                    jointNames = G1Skel34Names;
                    parentIndices = G1Skel34Parents;
                    return;
                case KimodoConstraintRigType.Smplx:
                    jointNames = Smplx22Names;
                    parentIndices = Smplx22Parents;
                    return;
                case KimodoConstraintRigType.Soma77:
                default:
                    jointNames = Soma77Names;
                    parentIndices = Soma77Parents;
                    return;
            }
        }

        private static string[] CloneStrings(string[] source)
        {
            if (source == null || source.Length == 0)
            {
                return System.Array.Empty<string>();
            }

            return (string[])source.Clone();
        }

        private static int[] CloneInts(int[] source)
        {
            if (source == null || source.Length == 0)
            {
                return System.Array.Empty<int>();
            }

            return (int[])source.Clone();
        }
    }
}
