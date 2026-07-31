using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    public sealed class BoneSample
    {
        public string[] boneNames;
        public Vector3[] localPositions;
        public Quaternion[] localRotations;

        public bool IsValid =>
            boneNames != null &&
            localPositions != null &&
            localRotations != null &&
            boneNames.Length == localPositions.Length &&
            boneNames.Length == localRotations.Length;
    }

    public sealed class MuscleSample
    {
        public HumanPose pose;
        public Vector3 leftFootPosition;
        public Quaternion leftFootRotation;
        public Vector3 rightFootPosition;
        public Quaternion rightFootRotation;
        public Vector3 leftHandPosition;
        public Quaternion leftHandRotation;
        public Vector3 rightHandPosition;
        public Quaternion rightHandRotation;
    }

    public sealed class SkeletonCache : IDisposable
    {
        public Avatar avatar;
        public GameObject root;
        public Transform skeletonRoot;
        public Vector3 rootLocalPosition;
        public Quaternion rootLocalRotation;
        public Vector3 rootLocalScale;
        public string canonicalRootBoneName;
        public Animator animator;
        public HumanPoseHandler poseHandler;
        public float humanScale;
        public string[] bonePaths;
        public Transform[] boneTransforms;
        public Dictionary<string, Transform> bonePathMap;
        public Dictionary<string, Transform> uniqueNameMap;
        public HashSet<string> ambiguousNames;
        public Dictionary<HumanBodyBones, Transform> humanBoneTransforms;
        public Vector3[] bindLocalPositions;
        public Quaternion[] bindLocalRotations;
        public int boneCount;
        private bool disposed;

        public bool IsReady =>
            !disposed &&
            root != null &&
            skeletonRoot != null &&
            animator != null &&
            poseHandler != null &&
            bonePaths != null &&
            boneTransforms != null &&
            bonePaths.Length == boneTransforms.Length;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            poseHandler?.Dispose();
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            avatar = null;
            root = null;
            skeletonRoot = null;
            canonicalRootBoneName = null;
            animator = null;
            poseHandler = null;
            humanScale = 0f;
            bonePaths = null;
            boneTransforms = null;
            bonePathMap = null;
            uniqueNameMap = null;
            ambiguousNames = null;
            humanBoneTransforms = null;
            bindLocalPositions = null;
            bindLocalRotations = null;
            boneCount = 0;
        }
    }
}
