using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeHumanoidRetargeter : IDisposable
    {
        private readonly List<TargetState> targets = new List<TargetState>();
        private bool driveFootIk;
        private string leftFootIkName = string.Empty;
        private string rightFootIkName = string.Empty;

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
            internal Transform LeftFootIkTarget;
            internal Transform RightFootIkTarget;
            internal Vector3 LeftFootTargetBaselinePosition;
            internal Quaternion LeftFootTargetBaselineRotation;
            internal Vector3 RightFootTargetBaselinePosition;
            internal Quaternion RightFootTargetBaselineRotation;
            internal Vector3 SourceLeftFootBaselineWorldPosition;
            internal Quaternion SourceLeftFootBaselineWorldRotation;
            internal Vector3 SourceRightFootBaselineWorldPosition;
            internal Quaternion SourceRightFootBaselineWorldRotation;
            internal bool LeftFootIkInitialized;
            internal bool RightFootIkInitialized;
            internal Vector3 LeftKneePoleLocalDirection;
            internal Vector3 RightKneePoleLocalDirection;
            internal bool LeftKneePoleInitialized;
            internal bool RightKneePoleInitialized;
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
            bool enableFootIk,
            string leftTargetName,
            string rightTargetName,
            out bool hasTarget,
            out string error)
        {
            DisposeTargets();
            driveFootIk = enableFootIk;
            leftFootIkName = leftTargetName ?? string.Empty;
            rightFootIkName = rightTargetName ?? string.Empty;
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

                ResolveFootIkTargets(
                    animator.transform,
                    driveFootIk,
                    leftFootIkName,
                    rightFootIkName,
                    out Transform leftFootIkTarget,
                    out Transform rightFootIkTarget);
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
                    LeftFootIkTarget = leftFootIkTarget,
                    RightFootIkTarget = rightFootIkTarget,
                    AnimatorWasEnabled = animator.enabled
                };
                animator.enabled = false;
                targets.Add(state);
            }

            hasTarget = targets.Count > 0;
            return hasTarget;
        }

        internal void SyncFootIkSetting(
            bool enableFootIk,
            string leftTargetName,
            string rightTargetName)
        {
            string resolvedLeftName = leftTargetName ?? string.Empty;
            string resolvedRightName = rightTargetName ?? string.Empty;
            if (driveFootIk == enableFootIk &&
                string.Equals(leftFootIkName, resolvedLeftName, StringComparison.Ordinal) &&
                string.Equals(rightFootIkName, resolvedRightName, StringComparison.Ordinal))
            {
                return;
            }

            driveFootIk = enableFootIk;
            leftFootIkName = resolvedLeftName;
            rightFootIkName = resolvedRightName;
            for (int i = 0; i < targets.Count; i++)
            {
                TargetState state = targets[i];
                if (state?.Animator == null)
                {
                    continue;
                }

                ResolveFootIkTargets(
                    state.Animator.transform,
                    driveFootIk,
                    leftFootIkName,
                    rightFootIkName,
                    out state.LeftFootIkTarget,
                    out state.RightFootIkTarget);
            }
            ResetAnchors();
        }

        internal void ResetAnchors()
        {
            for (int i = 0; i < targets.Count; i++)
            {
                TargetState state = targets[i];
                state.LeftFootIkInitialized = false;
                state.RightFootIkInitialized = false;
                state.LeftKneePoleInitialized = false;
                state.RightKneePoleInitialized = false;
                state.RetargetAnchorInitialized = false;
            }
        }

        internal bool TryApplyPose(
            SkeletonCache sourceCache,
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

            HumanPose pose = sample.pose;
            KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref pose);
            BuildFootWorldPose(
                sample,
                out Vector3 leftFootWorldPosition,
                out Quaternion leftFootWorldRotation,
                out Vector3 rightFootWorldPosition,
                out Quaternion rightFootWorldRotation);

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
                if (driveFootIk)
                {
                    ApplyFootIkTargets(
                        state,
                        leftFootWorldPosition,
                        leftFootWorldRotation,
                        rightFootWorldPosition,
                        rightFootWorldRotation);
                }
            }
            return true;
        }

        internal void ApplyLateCorrection(
            bool enableFootIk,
            Transform sourceHipsBone,
            Transform sourceLeftUpperLegBone,
            Transform sourceLeftLowerLegBone,
            Transform sourceLeftFootBone,
            Transform sourceRightUpperLegBone,
            Transform sourceRightLowerLegBone,
            Transform sourceRightFootBone)
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

                if (ShouldSolveFootIk(enableFootIk, state.LeftFootIkTarget))
                {
                    SolveTwoBoneLeg(
                        state.HipsBone,
                        state.LeftUpperLegBone,
                        state.LeftLowerLegBone,
                        state.LeftFootBone,
                        sourceHipsBone,
                        sourceLeftUpperLegBone,
                        sourceLeftLowerLegBone,
                        sourceLeftFootBone,
                        TransformSourcePositionForTarget(state, sourceLeftFootBone),
                        ref state.LeftKneePoleLocalDirection,
                        ref state.LeftKneePoleInitialized);
                }
                if (ShouldSolveFootIk(enableFootIk, state.RightFootIkTarget))
                {
                    SolveTwoBoneLeg(
                        state.HipsBone,
                        state.RightUpperLegBone,
                        state.RightLowerLegBone,
                        state.RightFootBone,
                        sourceHipsBone,
                        sourceRightUpperLegBone,
                        sourceRightLowerLegBone,
                        sourceRightFootBone,
                        TransformSourcePositionForTarget(state, sourceRightFootBone),
                        ref state.RightKneePoleLocalDirection,
                        ref state.RightKneePoleInitialized);
                }
            }
        }

        internal static bool ShouldSolveFootIk(bool enabled, Transform ikTarget) =>
            enabled && ikTarget != null;

        public void Dispose()
        {
            DisposeTargets();
            driveFootIk = false;
            leftFootIkName = string.Empty;
            rightFootIkName = string.Empty;
        }

        private void DisposeTargets()
        {
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i]?.RestoreAnimator();
            }
            targets.Clear();
        }

        private static void ResolveFootIkTargets(
            Transform root,
            bool enabled,
            string leftName,
            string rightName,
            out Transform leftTarget,
            out Transform rightTarget)
        {
            leftTarget = null;
            rightTarget = null;
            if (!enabled || root == null)
            {
                return;
            }

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (leftTarget == null &&
                    !string.IsNullOrWhiteSpace(leftName) &&
                    string.Equals(candidate.name, leftName, StringComparison.Ordinal))
                {
                    leftTarget = candidate;
                }
                if (rightTarget == null &&
                    !string.IsNullOrWhiteSpace(rightName) &&
                    string.Equals(candidate.name, rightName, StringComparison.Ordinal))
                {
                    rightTarget = candidate;
                }
                if (leftTarget != null && rightTarget != null)
                {
                    return;
                }
            }
        }

        private static void BuildFootWorldPose(
            MuscleSample sample,
            out Vector3 leftFootWorldPosition,
            out Quaternion leftFootWorldRotation,
            out Vector3 rightFootWorldPosition,
            out Quaternion rightFootWorldRotation)
        {
            HumanPose pose = sample != null ? sample.pose : default;
            Vector3 rootPosition = pose.bodyPosition;
            Quaternion rootRotation = pose.bodyRotation;
            leftFootWorldPosition = rootPosition + rootRotation * (sample != null ? sample.leftFootPosition : Vector3.zero);
            leftFootWorldRotation = rootRotation * (sample != null ? sample.leftFootRotation : Quaternion.identity);
            rightFootWorldPosition = rootPosition + rootRotation * (sample != null ? sample.rightFootPosition : Vector3.zero);
            rightFootWorldRotation = rootRotation * (sample != null ? sample.rightFootRotation : Quaternion.identity);
        }

        private static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private static Vector3 TransformSourcePositionForTarget(
            TargetState state,
            Transform sourceTransform)
        {
            if (state == null || sourceTransform == null)
            {
                return Vector3.zero;
            }

            return state.TargetHipsAnchorPosition + state.SourceToTargetRotation *
                (sourceTransform.position - state.SourceHipsAnchorPosition);
        }

        private static void ApplyFootIkTargets(
            TargetState state,
            Vector3 leftFootWorldPosition,
            Quaternion leftFootWorldRotation,
            Vector3 rightFootWorldPosition,
            Quaternion rightFootWorldRotation)
        {
            ApplyFootIkTarget(
                state.LeftFootBone,
                state.LeftFootIkTarget,
                ref state.LeftFootIkInitialized,
                ref state.LeftFootTargetBaselinePosition,
                ref state.LeftFootTargetBaselineRotation,
                ref state.SourceLeftFootBaselineWorldPosition,
                ref state.SourceLeftFootBaselineWorldRotation,
                leftFootWorldPosition,
                leftFootWorldRotation,
                state.SourceToTargetRotation);
            ApplyFootIkTarget(
                state.RightFootBone,
                state.RightFootIkTarget,
                ref state.RightFootIkInitialized,
                ref state.RightFootTargetBaselinePosition,
                ref state.RightFootTargetBaselineRotation,
                ref state.SourceRightFootBaselineWorldPosition,
                ref state.SourceRightFootBaselineWorldRotation,
                rightFootWorldPosition,
                rightFootWorldRotation,
                state.SourceToTargetRotation);
        }

        private static void ApplyFootIkTarget(
            Transform footBone,
            Transform ikTarget,
            ref bool initialized,
            ref Vector3 targetBaselinePosition,
            ref Quaternion targetBaselineRotation,
            ref Vector3 sourceBaselineWorldPosition,
            ref Quaternion sourceBaselineWorldRotation,
            Vector3 sourceCurrentWorldPosition,
            Quaternion sourceCurrentWorldRotation,
            Quaternion sourceToTargetRotation)
        {
            if (ikTarget == null)
            {
                return;
            }

            if (!initialized)
            {
                Vector3 alignedPosition = footBone != null ? footBone.position : ikTarget.position;
                Quaternion alignedRotation = footBone != null ? footBone.rotation : ikTarget.rotation;
                ikTarget.SetPositionAndRotation(alignedPosition, alignedRotation);
                targetBaselinePosition = alignedPosition;
                targetBaselineRotation = alignedRotation;
                sourceBaselineWorldPosition = sourceCurrentWorldPosition;
                sourceBaselineWorldRotation = sourceCurrentWorldRotation;
                initialized = true;
                return;
            }

            Vector3 deltaPosition = sourceToTargetRotation *
                (sourceCurrentWorldPosition - sourceBaselineWorldPosition);
            Quaternion deltaRotation = sourceCurrentWorldRotation * Quaternion.Inverse(sourceBaselineWorldRotation);
            deltaRotation = sourceToTargetRotation * deltaRotation * Quaternion.Inverse(sourceToTargetRotation);
            ikTarget.SetPositionAndRotation(
                targetBaselinePosition + deltaPosition,
                deltaRotation * targetBaselineRotation);
        }

        internal static void SolveTwoBoneLeg(
            Transform targetHips,
            Transform upperLeg,
            Transform lowerLeg,
            Transform foot,
            Transform sourceHips,
            Transform sourceUpperLeg,
            Transform sourceLowerLeg,
            Transform sourceFoot,
            ref Vector3 previousPoleLocalDirection,
            ref bool poleInitialized)
        {
            SolveTwoBoneLeg(
                targetHips,
                upperLeg,
                lowerLeg,
                foot,
                sourceHips,
                sourceUpperLeg,
                sourceLowerLeg,
                sourceFoot,
                sourceFoot != null ? sourceFoot.position : Vector3.zero,
                ref previousPoleLocalDirection,
                ref poleInitialized);
        }

        private static void SolveTwoBoneLeg(
            Transform targetHips,
            Transform upperLeg,
            Transform lowerLeg,
            Transform foot,
            Transform sourceHips,
            Transform sourceUpperLeg,
            Transform sourceLowerLeg,
            Transform sourceFoot,
            Vector3 targetFootPosition,
            ref Vector3 previousPoleLocalDirection,
            ref bool poleInitialized)
        {
            if (targetHips == null || upperLeg == null || lowerLeg == null || foot == null || sourceFoot == null)
            {
                return;
            }

            Vector3 upperPosition = upperLeg.position;
            Vector3 upperToLower = lowerLeg.position - upperPosition;
            Vector3 lowerToFoot = foot.position - lowerLeg.position;
            float upperLength = upperToLower.magnitude;
            float lowerLength = lowerToFoot.magnitude;
            if (upperLength <= 1e-5f || lowerLength <= 1e-5f)
            {
                return;
            }

            Vector3 upperToTarget = targetFootPosition - upperPosition;
            float targetDistance = upperToTarget.magnitude;
            Vector3 targetDirection = targetDistance > 1e-5f
                ? upperToTarget / targetDistance
                : (foot.position - upperPosition).normalized;
            if (targetDirection.sqrMagnitude <= 1e-8f)
            {
                return;
            }

            float totalLength = upperLength + lowerLength;
            float minimumReach = Mathf.Abs(upperLength - lowerLength) + 1e-4f;
            float maximumReach = Mathf.Min(totalLength - 1e-4f, totalLength * 0.995f);
            if (maximumReach <= minimumReach)
            {
                return;
            }

            float reachableDistance = Mathf.Clamp(targetDistance, minimumReach, maximumReach);
            Vector3 reachableTarget = upperPosition + targetDirection * reachableDistance;
            Vector3 previousBendDirection = poleInitialized
                ? Vector3.ProjectOnPlane(
                    targetHips.TransformDirection(previousPoleLocalDirection),
                    targetDirection)
                : Vector3.zero;
            Vector3 bendDirection = Vector3.zero;
            if (sourceHips != null && sourceUpperLeg != null && sourceLowerLeg != null)
            {
                Vector3 sourceTargetDirection = sourceFoot.position - sourceUpperLeg.position;
                Vector3 sourceBendDirection = Vector3.ProjectOnPlane(
                    sourceLowerLeg.position - sourceUpperLeg.position,
                    sourceTargetDirection);
                if (sourceBendDirection.sqrMagnitude > 1e-8f)
                {
                    Vector3 sourcePoleLocalDirection =
                        sourceHips.InverseTransformDirection(sourceBendDirection.normalized);
                    bendDirection = Vector3.ProjectOnPlane(
                        targetHips.TransformDirection(sourcePoleLocalDirection),
                        targetDirection);
                }
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = previousBendDirection;
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = Vector3.ProjectOnPlane(upperToLower, targetDirection);
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = Vector3.ProjectOnPlane(upperLeg.forward, targetDirection);
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                bendDirection = Vector3.ProjectOnPlane(upperLeg.right, targetDirection);
            }
            if (bendDirection.sqrMagnitude <= 1e-8f)
            {
                return;
            }
            bendDirection.Normalize();
            if (previousBendDirection.sqrMagnitude > 1e-8f &&
                Vector3.Dot(bendDirection, previousBendDirection) < 0f)
            {
                bendDirection = -bendDirection;
            }
            previousPoleLocalDirection =
                targetHips.InverseTransformDirection(bendDirection).normalized;
            poleInitialized = true;

            float alongTarget =
                (upperLength * upperLength + reachableDistance * reachableDistance - lowerLength * lowerLength) /
                (2f * reachableDistance);
            float awayFromTarget = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - alongTarget * alongTarget));
            Vector3 desiredLowerPosition =
                upperPosition + targetDirection * alongTarget + bendDirection * awayFromTarget;
            Quaternion footWorldRotation = foot.rotation;

            upperLeg.rotation =
                Quaternion.FromToRotation(upperToLower, desiredLowerPosition - upperPosition) * upperLeg.rotation;
            Vector3 adjustedLowerToFoot = foot.position - lowerLeg.position;
            Vector3 adjustedLowerToTarget = reachableTarget - lowerLeg.position;
            if (adjustedLowerToFoot.sqrMagnitude > 1e-8f && adjustedLowerToTarget.sqrMagnitude > 1e-8f)
            {
                lowerLeg.rotation =
                    Quaternion.FromToRotation(adjustedLowerToFoot, adjustedLowerToTarget) * lowerLeg.rotation;
            }
            foot.rotation = footWorldRotation;
        }
    }
}
