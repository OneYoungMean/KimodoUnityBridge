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

        public bool IsValid
        {
            get
            {
                if (boneNames == null || localPositions == null || localRotations == null ||
                    boneNames.Length != localPositions.Length ||
                    boneNames.Length != localRotations.Length)
                {
                    return false;
                }

                for (int i = 0; i < localPositions.Length; i++)
                {
                    Vector3 position = localPositions[i];
                    Quaternion rotation = localRotations[i];
                    if (!IsFinite(position.x) || !IsFinite(position.y) ||
                        !IsFinite(position.z) || !IsFinite(rotation.x) ||
                        !IsFinite(rotation.y) || !IsFinite(rotation.z) ||
                        !IsFinite(rotation.w))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [Serializable]
    public sealed class MuscleSample
    {
        // Atomic muscle pose: 49 body muscles followed by rootTQ, leftFootTQ
        // and rightFootTQ. HumanPose is an API boundary, never stored here.
        public float[] data = KimodoSampleDataLayout.CreateBuffer();
        /// <summary>
        /// Unity's temporary HumanPose view of this 70D payload. The returned
        /// value is rebuilt from <see cref="data"/> on each access; MuscleSample
        /// remains the only stored animation representation.
        /// </summary>
        public HumanPose pose => KimodoMuscleSampleHumanPoseAdapter.ToHumanPose(this);

        public bool IsValid => KimodoSampleDataLayout.TryValidate(data, out _);

        public MuscleSample Clone()
        {
            return new MuscleSample
            {
                data = data != null ? (float[])data.Clone() : KimodoSampleDataLayout.CreateBuffer()
            };
        }

        public void GetRoot(out Vector3 position, out Quaternion rotation) =>
            KimodoSampleDataLayout.GetTransform(data, KimodoSampleDataLayout.RootTqOffset, out position, out rotation);

        public void GetLeftFoot(out Vector3 position, out Quaternion rotation) =>
            KimodoSampleDataLayout.GetTransform(data, KimodoSampleDataLayout.LeftFootTqOffset, out position, out rotation);

        public void GetRightFoot(out Vector3 position, out Quaternion rotation) =>
            KimodoSampleDataLayout.GetTransform(data, KimodoSampleDataLayout.RightFootTqOffset, out position, out rotation);

        public void SetRoot(Vector3 position, Quaternion rotation) =>
            KimodoSampleDataLayout.SetTransform(data, KimodoSampleDataLayout.RootTqOffset, position, rotation.normalized);

        public void SetLeftFoot(Vector3 position, Quaternion rotation) =>
            KimodoSampleDataLayout.SetTransform(data, KimodoSampleDataLayout.LeftFootTqOffset, position, rotation.normalized);

        public void SetRightFoot(Vector3 position, Quaternion rotation) =>
            KimodoSampleDataLayout.SetTransform(data, KimodoSampleDataLayout.RightFootTqOffset, position, rotation.normalized);
    }

    public sealed class KimodoSkeletonInstance : IDisposable
    {
        private readonly RetargetSkeleton cache;

        internal KimodoSkeletonInstance(RetargetSkeleton cache)
        {
            this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public RetargetSkeleton Cache => cache;
        public Avatar Avatar => cache.avatar;
        public Animator Animator => cache.animator;
        public Transform Root => cache.skeletonRoot;
        public bool IsReady => cache.IsReady;

        public void ResetToBindPose()
        {
            KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(cache);
        }

        public BoneSample CaptureBoneSample()
        {
            return KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
        }

        public bool TryApplyBoneSample(BoneSample sample, out string error)
        {
            return KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(sample, cache, out error);
        }

        public bool TryCaptureMuscleSample(out MuscleSample sample, out string error)
        {
            return KimodoRetargetSamplingUtility.TryCaptureMuscleSample(cache, out sample, out error);
        }

        public bool TryCaptureSampleData(
            out MuscleSample sampleData,
            out KimodoConstraintMask validMask,
            out string error)
        {
            return KimodoRetargetSamplingUtility.TryCaptureSampleData(
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

    public sealed class RetargetSkeleton : IDisposable
    {
        public Avatar avatar;
        /// <summary>Avatar-derived scale used only for HumanPose RootTQ/FootTQ transport conversion.</summary>
        public float humanScale = 1f;
        public GameObject root;
        public Transform skeletonRoot;
        public Vector3 rootLocalPosition;
        public Quaternion rootLocalRotation;
        public Vector3 rootLocalScale;
        public string canonicalRootBoneName;
        public Animator animator;
        public HumanPoseHandler poseHandler;
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
            humanScale = 1f;
            root = null;
            skeletonRoot = null;
            canonicalRootBoneName = null;
            animator = null;
            poseHandler = null;
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
