using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetCoreUtility
    {
        internal static bool IsValidHumanoid(Avatar avatar)
        {
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        internal static bool WriteMuscleSampleToMuscleClip(
            IReadOnlyList<MuscleSample> samples,
            AnimationClip clip,
            out string error)
        {
            error = string.Empty;
            if (clip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            if (samples == null || samples.Count == 0)
            {
                error = "Muscle samples are empty.";
                return false;
            }

            clip.ClearCurves();
            if (!KimodoRetargetClipWriter.WriteMuscleCurves(samples, clip, out error))
            {
                return false;
            }

            clip.EnsureQuaternionContinuity();
            return true;
        }

        internal static bool WriteBoneSampleToBoneClip(
            IReadOnlyList<BoneSample> samples,
            AnimationClip clip,
            out string error)
        {
            error = string.Empty;
            if (clip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            if (samples == null || samples.Count == 0)
            {
                error = "Bone samples are empty.";
                return false;
            }

            clip.ClearCurves();
            if (!KimodoRetargetClipWriter.WriteBoneCurves(samples, clip, out error))
            {
                return false;
            }

            clip.EnsureQuaternionContinuity();
            return true;
        }

        internal static bool TryRetargetClip(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            Avatar targetAvatar,
            bool exportMuscleClip,
            AnimationClip providedSourceHumanoidClip,
            out AnimationClip targetClip,
            out string error)
        {
            SkeletonCache sourceCache = null;
            SkeletonCache targetCache = null;
            try
            {
                targetClip = sourceClip;
                error = string.Empty;

                if (sourceClip == null)
                {
                    error = "Source clip is null.";
                    return false;
                }

                if (exportMuscleClip && sourceClip.isHumanMotion)
                {
                    return true;
                }

                if (!IsValidHumanoid(sourceAvatar))
                {
                    error = "Source avatar is null/invalid/non-humanoid.";
                    return false;
                }

                if (!IsValidHumanoid(targetAvatar))
                {
                    error = "Target avatar is null/invalid/non-humanoid.";
                    return false;
                }

                float frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
                float duration = Mathf.Max(0f, sourceClip.length);
                int frameCount = KimodoRetargetSamplingUtility.ResolveFrameCount(duration, frameRate);
                bool needsSourceCache = exportMuscleClip && !sourceClip.isHumanMotion;
                bool needsTargetCache = !exportMuscleClip;

                if (needsSourceCache && !KimodoRetargetAvatarUtility.ValidateRetargetCache(sourceCache, out _))
                {
                    sourceCache = null;
                    if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(sourceAvatar, "KimodoRetargetTools_SourceClipBatch", out sourceCache, out error))
                    {
                        return false;
                    }
                }

                if (needsTargetCache && !KimodoRetargetAvatarUtility.ValidateRetargetCache(targetCache, out _))
                {
                    targetCache = null;
                    if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(targetAvatar, "KimodoRetargetTools_TargetClipBatch", out targetCache, out error))
                    {
                        return false;
                    }
                }

                if (targetClip != null)
                {
                    targetClip.frameRate = frameRate;
                }

                if (exportMuscleClip)
                {
                    if (!KimodoRetargetSamplingUtility.TryCollectMuscleSamplesFromClip(
                            sourceClip,
                            sourceCache,
                            frameCount,
                            duration,
                            KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(sourceClip),
                            out MuscleSample[] targetMuscleSamples,
                            out error))
                    {
                        return false;
                    }

                    return WriteMuscleSampleToMuscleClip(targetMuscleSamples, targetClip, out error);
                }

                if (!KimodoRetargetSamplingUtility.TryResolveSourceHumanoidClip(
                        sourceClip,
                        sourceAvatar,
                        "KimodoRetargetTools_SourceClipBatch",
                        providedSourceHumanoidClip,
                        ref sourceCache,
                        out AnimationClip sourceHumanoidClip,
                        out error))
                {
                    return false;
                }

                try
                {
                    if (!KimodoRetargetSamplingUtility.TryCollectBoneSamplesFromClip(
                            sourceHumanoidClip,
                            targetCache,
                            frameCount,
                            duration,
                            KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                            out BoneSample[] targetBoneSamples,
                            out error))
                    {
                        return false;
                    }

                    return WriteBoneSampleToBoneClip(targetBoneSamples, targetClip, out error);
                }
                finally
                {
                    if (!ReferenceEquals(sourceHumanoidClip, sourceClip) &&
                        !ReferenceEquals(sourceHumanoidClip, providedSourceHumanoidClip))
                    {
                        UnityEngine.Object.DestroyImmediate(sourceHumanoidClip);
                    }
                }
            }
            finally
            {
                targetCache?.Dispose();
                sourceCache?.Dispose();
            }
        }
    }

}
