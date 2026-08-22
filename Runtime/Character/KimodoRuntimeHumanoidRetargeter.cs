using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeHumanoidRetargeter : IDisposable
    {
        private readonly List<TargetState> targets = new List<TargetState>();

        private sealed class TargetState
        {
            internal Animator Animator;
            internal HumanPoseHandler PoseHandler;
            internal Transform HipsBone;
            internal Transform LeftUpperLegBone;
            internal Transform LeftLowerLegBone;
            internal Transform LeftFootBone;
            internal Transform RightUpperLegBone;
            internal Transform RightLowerLegBone;
            internal Transform RightFootBone;
            internal bool AnimatorWasEnabled;
            internal Quaternion SourceToTargetRotation = Quaternion.identity;
            internal Vector3 SourceHipsAnchorPosition;
            internal Vector3 TargetHipsAnchorPosition;
            internal bool RetargetAnchorInitialized;

            internal void RestoreAnimator()
            {
                if (Animator != null)
                {
                    Animator.enabled = AnimatorWasEnabled;
                }
            }
        }

        internal bool BindTargets(
            IReadOnlyList<Animator> animators,
            out bool hasTarget,
            out string error)
        {
            DisposeTargets();
            error = string.Empty;
            hasTarget = animators != null && animators.Count > 0;
            if (!hasTarget)
            {
                return true;
            }

            var seen = new HashSet<Animator>();
            for (int i = 0; i < animators.Count; i++)
            {
                Animator animator = animators[i];
                if (animator == null || !seen.Add(animator))
                {
                    continue;
                }

                Avatar avatar = animator.avatar;
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
                {
                    error = $"Humanoid retarget animator '{animator.name}' avatar is null, invalid, or not humanoid.";
                    DisposeTargets();
                    return false;
                }

                var state = new TargetState
                {
                    Animator = animator,
                    PoseHandler = new HumanPoseHandler(avatar, animator.transform),
                    HipsBone = animator.GetBoneTransform(HumanBodyBones.Hips),
                    LeftUpperLegBone = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg),
                    LeftLowerLegBone = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),
                    LeftFootBone = animator.GetBoneTransform(HumanBodyBones.LeftFoot),
                    RightUpperLegBone = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg),
                    RightLowerLegBone = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg),
                    RightFootBone = animator.GetBoneTransform(HumanBodyBones.RightFoot),
                    AnimatorWasEnabled = animator.enabled
                };
                animator.enabled = false;
                targets.Add(state);
            }

            hasTarget = targets.Count > 0;
            return hasTarget;
        }

        internal void ResetAnchors()
        {
            for (int i = 0; i < targets.Count; i++)
            {
                TargetState state = targets[i];
                state.RetargetAnchorInitialized = false;
            }
        }

        internal bool TryApplyPose(
            RetargetSkeleton sourceCache,
            Transform sourceHipsBone,
            out string error)
        {
            error = string.Empty;
            if (sourceCache == null || targets.Count == 0)
            {
                return true;
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(sourceCache, out MuscleSample sample, out error))
            {
                return false;
            }

            HumanPose pose = CharacterPoseMuscleAdapter.ToHumanPose(sample);
            for (int i = 0; i < targets.Count; i++)
            {
                TargetState state = targets[i];
                if (state?.PoseHandler == null)
                {
                    error = $"Target pose handler {i} is not initialized.";
                    return false;
                }

                if (!state.RetargetAnchorInitialized)
                {
                    state.SourceHipsAnchorPosition = sourceHipsBone != null
                        ? sourceHipsBone.position
                        : pose.bodyPosition;
                    state.TargetHipsAnchorPosition = state.HipsBone != null
                        ? state.HipsBone.position
                        : state.Animator.transform.position;
                    Quaternion sourceRotation = sourceCache.skeletonRoot != null
                        ? ResolvePlanarRotation(sourceCache.skeletonRoot.rotation)
                        : Quaternion.identity;
                    state.SourceToTargetRotation =
                        ResolvePlanarRotation(state.Animator.transform.rotation) *
                        Quaternion.Inverse(sourceRotation);
                    state.RetargetAnchorInitialized = true;
                }

                HumanPose targetPose = pose;
                state.PoseHandler.SetHumanPose(ref targetPose);
            }
            return true;
        }

        internal void ApplyLateCorrection(Transform sourceHipsBone)
        {
            if (sourceHipsBone == null)
            {
                return;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                TargetState state = targets[i];
                if (state?.Animator == null || state.HipsBone == null || !state.RetargetAnchorInitialized)
                {
                    continue;
                }

                Vector3 sourceDelta = sourceHipsBone.position - state.SourceHipsAnchorPosition;
                Vector3 desiredHipsPosition = state.TargetHipsAnchorPosition +
                    state.SourceToTargetRotation * sourceDelta;
                Vector3 hipsOffset = desiredHipsPosition - state.HipsBone.position;
                state.Animator.transform.position += new Vector3(hipsOffset.x, 0f, hipsOffset.z);

            }
        }

        public void Dispose()
        {
            DisposeTargets();
        }

        private void DisposeTargets()
        {
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i]?.RestoreAnimator();
            }
            targets.Clear();
        }

        private static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

    }
}
