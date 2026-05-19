using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoUnityMotionTools
{
    public class SOMABone
    {
        public string Name { get; set; }
        public int Index { get; set; }
        public int ParentIndex { get; set; }
        public Vector3 LocalPosition { get; set; }
        public Quaternion LocalRotation { get; set; }
        public Vector3 WorldPosition { get; set; }
        public Quaternion WorldRotation { get; set; }

        public SOMABone(string name, int index, int parentIndex)
        {
            Name = name;
            Index = index;
            ParentIndex = parentIndex;
        }
    }

    public class SOMASkeleton : MonoBehaviour
    {
        [Header("Skeleton Configuration")]
        public bool Is77Joints = true;
        public float BoneScale = 1.0f;
        public Color BoneColor = Color.cyan;
        public Color JointColor = Color.yellow;
        public float JointRadius = 0.05f;

        [Header("Joint Names (SOMA77)")]
        public List<string> JointNames = new List<string>();

        private List<SOMABone> bones = new List<SOMABone>();
        private Vector3[] currentPositions;
        private Quaternion[] currentRotations;

        private static readonly string[] SOMA77_NAMES = new string[]
        {
            "Pelvis", "L_Hip", "L_Knee", "L_Foot", "L_Toes",
            "R_Hip", "R_Knee", "R_Foot", "R_Toes",
            "Spine1", "Spine2", "Spine3", "Neck", "Head",
            "L_Clavicle", "L_Shoulder", "L_Elbow", "L_Wrist", "L_Hand",
            "L_Thumb1", "L_Thumb2", "L_Thumb3",
            "L_Index1", "L_Index2", "L_Index3",
            "L_Middle1", "L_Middle2", "L_Middle3",
            "L_Ring1", "L_Ring2", "L_Ring3",
            "L_Pinky1", "L_Pinky2", "L_Pinky3",
            "R_Clavicle", "R_Shoulder", "R_Elbow", "R_Wrist", "R_Hand",
            "R_Thumb1", "R_Thumb2", "R_Thumb3",
            "R_Index1", "R_Index2", "R_Index3",
            "R_Middle1", "R_Middle2", "R_Middle3",
            "R_Ring1", "R_Ring2", "R_Ring3",
            "R_Pinky1", "R_Pinky2", "R_Pinky3",
            "Jaw", "L_Eye", "R_Eye", "L_Ear", "R_Ear",
            "L_Thumb4", "R_Thumb4",
            "L_Index4", "R_Index4",
            "L_Middle4", "R_Middle4",
            "L_Ring4", "R_Ring4",
            "L_Pinky4", "R_Pinky4",
            "UpperLip", "LowerLip",
            "L_Breast", "R_Breast",
            "L_Nipple", "R_Nipple",
            "L_HandThumb", "R_HandThumb"
        };

        private static readonly int[] SOMA77_PARENTS = new int[]
        {
            -1, 0, 1, 2, 3,
            0, 5, 6, 7,
            0, 9, 10, 11, 12,
            12, 14, 15, 16, 17,
            18, 19, 20, 21,
            22, 23, 24,
            25, 26, 27,
            28, 29, 30,
            31, 32, 33,
            12, 35, 36, 37, 38,
            39, 40, 41, 42,
            43, 44, 45,
            46, 47, 48,
            49, 50, 51,
            13, 53, 54, 55, 56,
            21, 45,
            24, 48,
            27, 51,
            30, 54,
            33, 57,
            13, 13,
            10, 10,
            10, 10,
            18, 38
        };

        private static readonly string[] SOMA30_NAMES = new string[]
        {
            "Pelvis", "L_Hip", "L_Knee", "L_Foot", "L_Toes",
            "R_Hip", "R_Knee", "R_Foot", "R_Toes",
            "Spine1", "Spine2", "Spine3", "Neck", "Head",
            "L_Clavicle", "L_Shoulder", "L_Elbow", "L_Wrist", "L_Hand",
            "R_Clavicle", "R_Shoulder", "R_Elbow", "R_Wrist", "R_Hand",
            "L_Eye", "R_Eye", "Jaw",
            "L_Thumb", "L_Index", "L_Middle", "L_Ring", "L_Pinky",
            "R_Thumb", "R_Index", "R_Middle", "R_Ring", "R_Pinky"
        };

        private static readonly int[] SOMA30_PARENTS = new int[]
        {
            -1, 0, 1, 2, 3,
            0, 5, 6, 7,
            0, 9, 10, 11, 12,
            12, 14, 15, 16, 17,
            12, 19, 20, 21, 22,
            13, 24, 25,
            18, 18, 18, 18, 18,
            22, 22, 22, 22, 22
        };

        void Awake()
        {
            InitializeSkeleton();
        }

        public void InitializeSkeleton()
        {
            bones.Clear();

            string[] names = Is77Joints ? SOMA77_NAMES : SOMA30_NAMES;
            int[] parents = Is77Joints ? SOMA77_PARENTS : SOMA30_PARENTS;

            for (int i = 0; i < names.Length; i++)
            {
                var bone = new SOMABone(names[i], i, parents[i]);
                bones.Add(bone);
            }

            JointNames = new List<string>(names);
            currentPositions = new Vector3[bones.Count];
            currentRotations = new Quaternion[bones.Count];

            for (int i = 0; i < bones.Count; i++)
            {
                currentPositions[i] = Vector3.zero;
                currentRotations[i] = Quaternion.identity;
            }

            Debug.Log($"[SOMASKeleton] Initialized {bones.Count} joints skeleton");
        }

        public int JointCount => bones.Count;

        public void UpdatePositions(Vector3[] positions)
        {
            if (positions == null || positions.Length != bones.Count)
            {
                Debug.LogWarning($"[SOMASKeleton] Position array size mismatch: expected {bones.Count}, got {positions?.Length ?? 0}");
                return;
            }

            for (int i = 0; i < positions.Length; i++)
            {
                currentPositions[i] = transform.position + positions[i] * BoneScale;
            }
        }

        public void UpdateFromMotionData(List<List<List<float>>> motionData, int frameIndex)
        {
            if (motionData == null || motionData.Count == 0)
            {
                Debug.LogWarning("[SOMASKeleton] No motion data available");
                return;
            }

            int targetFrame = Mathf.Clamp(frameIndex, 0, motionData.Count - 1);
            var frameData = motionData[targetFrame];

            if (frameData == null || frameData.Count != bones.Count)
            {
                Debug.LogWarning($"[SOMASKeleton] Frame data mismatch: expected {bones.Count}, got {frameData?.Count ?? 0}");
                return;
            }

            for (int i = 0; i < frameData.Count && i < bones.Count; i++)
            {
                var jointData = frameData[i];
                if (jointData != null && jointData.Count >= 3)
                {
                    currentPositions[i] = transform.position + new Vector3(jointData[0], jointData[1], jointData[2]) * BoneScale;
                }
            }
        }

        public void SetJointPosition(int index, Vector3 position)
        {
            if (index >= 0 && index < currentPositions.Length)
            {
                currentPositions[index] = transform.position + position * BoneScale;
            }
        }

        public Vector3 GetJointPosition(int index)
        {
            if (index >= 0 && index < currentPositions.Length)
            {
                return currentPositions[index] - transform.position;
            }
            return Vector3.zero;
        }

        public string GetJointName(int index)
        {
            if (index >= 0 && index < bones.Count)
            {
                return bones[index].Name;
            }
            return "";
        }

        public int GetJointIndex(string name)
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].Name == name)
                    return i;
            }
            return -1;
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
            {
                DrawDefaultSkeleton();
                return;
            }

            Gizmos.color = JointColor;
            for (int i = 0; i < currentPositions.Length; i++)
            {
                Gizmos.DrawWireSphere(currentPositions[i], JointRadius);
            }

            Gizmos.color = BoneColor;
            for (int i = 0; i < bones.Count; i++)
            {
                int parentIndex = bones[i].ParentIndex;
                if (parentIndex >= 0 && parentIndex < currentPositions.Length)
                {
                    Gizmos.DrawLine(currentPositions[parentIndex], currentPositions[i]);
                }
            }
        }

        private void DrawDefaultSkeleton()
        {
            string[] names = Is77Joints ? SOMA77_NAMES : SOMA30_NAMES;
            int[] parents = Is77Joints ? SOMA77_PARENTS : SOMA30_PARENTS;

            Gizmos.color = JointColor;
            for (int i = 0; i < names.Length; i++)
            {
                Vector3 pos = transform.position + Vector3.up * i * 0.1f * BoneScale;
                Gizmos.DrawWireSphere(pos, JointRadius);
            }

            Gizmos.color = BoneColor;
            for (int i = 0; i < names.Length; i++)
            {
                int parentIndex = parents[i];
                if (parentIndex >= 0)
                {
                    Vector3 parentPos = transform.position + Vector3.up * parentIndex * 0.1f * BoneScale;
                    Vector3 childPos = transform.position + Vector3.up * i * 0.1f * BoneScale;
                    Gizmos.DrawLine(parentPos, childPos);
                }
            }
        }
    }
}
