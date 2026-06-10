using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintPoseRigFootUtility
    {
        internal static bool TryResolveFootWorldPositions(
            SkeletonCache cache,
            out Vector3 leftFootPosition,
            out Vector3 rightFootPosition,
            out string error)
        {
            leftFootPosition = Vector3.zero;
            rightFootPosition = Vector3.zero;
            error = string.Empty;

            if (cache == null || cache.animator == null)
            {
                error = "invalid skeleton cache";
                return false;
            }

            Transform leftFoot = cache.animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = cache.animator.GetBoneTransform(HumanBodyBones.RightFoot);

            if (leftFoot == null)
            {
                leftFoot = cache.animator.GetBoneTransform(HumanBodyBones.LeftToes);
            }

            if (rightFoot == null)
            {
                rightFoot = cache.animator.GetBoneTransform(HumanBodyBones.RightToes);
            }

            if (leftFoot == null)
            {
                error = "left foot transform not found on skeleton cache";
                return false;
            }

            if (rightFoot == null)
            {
                error = "right foot transform not found on skeleton cache";
                return false;
            }

            leftFootPosition = leftFoot.position;
            rightFootPosition = rightFoot.position;
            return true;
        }

        internal static bool TryResolveFootWorldPositions(
            KimodoConstraintPoseRigFactory.PoseRigInstance instance,
            string modelName,
            out Vector3 leftFootPosition,
            out Vector3 rightFootPosition,
            out string error)
        {
            leftFootPosition = Vector3.zero;
            rightFootPosition = Vector3.zero;
            error = string.Empty;

            if (instance == null || instance.Root == null || instance.NameMap == null)
            {
                error = "invalid pose rig instance";
                return false;
            }

            if (!TryResolveFootTransform(instance, modelName, left: true, out Transform leftFoot, out error))
            {
                return false;
            }

            if (!TryResolveFootTransform(instance, modelName, left: false, out Transform rightFoot, out error))
            {
                return false;
            }

            leftFootPosition = leftFoot != null ? leftFoot.position : instance.Root.transform.position;
            rightFootPosition = rightFoot != null ? rightFoot.position : instance.Root.transform.position;
            return true;
        }

        private static bool TryResolveFootTransform(
            KimodoConstraintPoseRigFactory.PoseRigInstance instance,
            string modelName,
            bool left,
            out Transform foot,
            out string error)
        {
            foot = null;
            error = string.Empty;

            string[] candidates = ResolveFootCandidates(modelName, left);
            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    instance.NameMap.TryGetValue(candidate, out foot) &&
                    foot != null)
                {
                    return true;
                }
            }

            error = left
                ? $"left foot transform not found for model '{modelName}'"
                : $"right foot transform not found for model '{modelName}'";
            return false;
        }

        private static string[] ResolveFootCandidates(string modelName, bool left)
        {
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            switch (rigType)
            {
                case KimodoConstraintRigType.G1:
                    return left
                        ? new[] { "left_ankle_roll_skel", "left_toe_base", "left_ankle_pitch_skel" }
                        : new[] { "right_ankle_roll_skel", "right_toe_base", "right_ankle_pitch_skel" };
                case KimodoConstraintRigType.Smplx:
                    return left
                        ? new[] { "left_foot", "left_ankle" }
                        : new[] { "right_foot", "right_ankle" };
                case KimodoConstraintRigType.Soma77:
                default:
                    return left
                        ? new[] { "LeftFoot", "LeftToeBase", "LeftShin" }
                        : new[] { "RightFoot", "RightToeBase", "RightShin" };
            }
        }
    }
}
