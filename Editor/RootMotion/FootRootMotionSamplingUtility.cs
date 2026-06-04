using KimodoBridge;
using System;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class FootRootMotionSamplingUtility
    {
        public static bool TrySampleClip(
            AnimationClip clip,
            Avatar avatar,
            out FootRootMotionFrame[] frames,
            out string error)
        {
            frames = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "AnimationClip is null.";
                return false;
            }

            if (clip.length <= 0f)
            {
                error = "AnimationClip length must be greater than zero.";
                return false;
            }

            if (!KimodoRetargetTools.IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            if (!KimodoRetargetTools.TryCreateTemporaryHumanoidRoot(
                avatar,
                "FootRootMotionSampler",
                animatorEnabled: true,
                applyRootMotion: true,
                out GameObject instance,
                out Animator animator,
                out error))
            {
                return false;
            }

            try
            {
                Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (leftFoot == null || rightFoot == null || hips == null)
                {
                    error = "Humanoid avatar is missing LeftFoot, RightFoot, or Hips bone mapping.";
                    return false;
                }

                float sampleStepSeconds = FootRootMotionSolverSettings.FixedSamplingStepSeconds;
                int frameCount = Mathf.Max(2, Mathf.CeilToInt(clip.length / sampleStepSeconds) + 1);
                frames = new FootRootMotionFrame[frameCount];
                for (int i = 0; i < frameCount; i++)
                {
                    float t = Mathf.Min(clip.length, i * sampleStepSeconds);
                    clip.SampleAnimation(instance, t);

                    frames[i] = new FootRootMotionFrame
                    {
                        time = t,
                        leftFootWorld = leftFoot.position,
                        rightFootWorld = rightFoot.position,
                        sampledRootWorld = instance.transform.position,
                        sampledRootRotation = instance.transform.rotation
                    };
                }

                return true;
            }
            catch (Exception ex)
            {
                frames = null;
                error = "Sampling failed: " + ex.Message;
                return false;
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }
    }
}
