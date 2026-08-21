using System;
using System.Collections.Generic;
using TimelineInject;
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
        // Hand IK targets are scene data and are carried by
        // KimodoConstraintEffectors/HumanoidEffectors. These legacy
        // fields remain source-compatible for old editor assets only; the
        // sampling and clip pipelines no longer read or write them.
        [Obsolete("Hand IK targets are scene data; use HumanoidEffectors.")]
        public Vector3 leftHandPosition;
        [Obsolete("Hand IK targets are scene data; use HumanoidEffectors.")]
        public Quaternion leftHandRotation;
        [Obsolete("Hand IK targets are scene data; use HumanoidEffectors.")]
        public Vector3 rightHandPosition;
        [Obsolete("Hand IK targets are scene data; use HumanoidEffectors.")]
        public Quaternion rightHandRotation;
    }

    public sealed class KimodoSkeletonInstance : IDisposable
    {
        private readonly SkeletonCache cache;

        internal KimodoSkeletonInstance(SkeletonCache cache)
        {
            this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public SkeletonCache Cache => cache;
        public Avatar Avatar => cache.avatar;
        public Animator Animator => cache.animator;
        public Transform Root => cache.skeletonRoot;
        public float HumanScale => cache.humanScale;
        public bool IsReady => cache.IsReady;

        public void ResetToBindPose()
        {
            KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(cache);
        }

        public BoneSample CaptureBoneSample()
        {
            return KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
        }

        public bool TryApplyBoneSample(BoneSample sample, out string error)
        {
            return KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(sample, cache, out error);
        }

        public bool TryCaptureMuscleSample(out MuscleSample sample, out string error)
        {
            return KimodoRetargetSamplingUtility.TryCaptureMuscleSample(cache, out sample, out error);
        }

        public bool TryCaptureSampleData(
            out float[] sampleData,
            out KimodoSampleChannelMask validMask,
            out string error)
        {
            return KimodoRetargetClipSamplingUtility.TryCaptureSampleData(
                cache,
                out sampleData,
                out validMask,
                out error);
        }

        public bool TryGetHumanBone(HumanBodyBones bone, out Transform transform)
        {
            transform = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(cache, bone);
            return transform != null;
        }

        public void Dispose()
        {
            cache.Dispose();
        }
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
        public Quaternion bindSkeletonRootWorldRotation;
        public Quaternion[] bindWorldRotations;
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

        public bool GetBonePose(
            HumanBodyBones bone,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!IsReady || humanBoneTransforms == null ||
                !humanBoneTransforms.TryGetValue(bone, out Transform transform) ||
                transform == null)
            {
                return false;
            }

            position = transform.position;
            rotation = transform.rotation;
            return true;
        }

        public bool GetBoneBindLocalRotation(
            HumanBodyBones bone,
            out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (!IsReady || humanBoneTransforms == null || bindLocalRotations == null ||
                !humanBoneTransforms.TryGetValue(bone, out Transform transform) ||
                transform == null || boneTransforms == null)
            {
                return false;
            }

            for (int i = 0; i < boneTransforms.Length && i < bindLocalRotations.Length; i++)
            {
                if (boneTransforms[i] == transform)
                {
                    rotation = bindLocalRotations[i];
                    return true;
                }
            }
            return false;
        }

        public bool GetBoneBindWorldRotation(
            HumanBodyBones bone,
            out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (!IsReady || humanBoneTransforms == null || bindWorldRotations == null ||
                !humanBoneTransforms.TryGetValue(bone, out Transform transform) ||
                transform == null || boneTransforms == null)
            {
                return false;
            }

            for (int i = 0; i < boneTransforms.Length && i < bindWorldRotations.Length; i++)
            {
                if (boneTransforms[i] == transform)
                {
                    rotation = bindWorldRotations[i];
                    return true;
                }
            }
            return false;
        }

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
            bindWorldRotations = null;
            boneCount = 0;
        }
    }
}
