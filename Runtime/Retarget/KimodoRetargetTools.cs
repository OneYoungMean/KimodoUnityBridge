using System;
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
            out string error,
            Action<string> debugLog = null)
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
                int frameCount = KimodoRetargetSamplingUtility.ResolveInclusiveSampleCount(duration, frameRate);
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

                    debugLog?.Invoke(
                        $"[Kimodo][RetargetAvatar] runtime target cache ready: " +
                        $"avatar={DescribeAvatarForDebug(targetCache.avatar)}, " +
                        $"animatorAvatar={DescribeAvatarForDebug(targetCache.animator != null ? targetCache.animator.avatar : null)}, " +
                        $"root='{targetCache.skeletonRoot?.name}', bones={targetCache.boneTransforms?.Length ?? 0}, " +
                        $"humanBones={targetCache.humanBoneTransforms?.Count ?? 0}.");
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
                    debugLog?.Invoke(
                        $"[Kimodo][RetargetAvatar] runtime Humanoid->Bone input: " +
                        $"clip='{sourceHumanoidClip.name}', isHumanMotion={sourceHumanoidClip.isHumanMotion}, " +
                        $"provided={ReferenceEquals(sourceHumanoidClip, providedSourceHumanoidClip)}, " +
                        $"samplingMode={KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid}, " +
                        $"applyMotionXToDelta=false.");
                    if (!KimodoRetargetSamplingUtility.TryCollectBoneSamplesFromClip(
                            sourceHumanoidClip,
                            targetCache,
                            frameCount,
                            KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                            out BoneSample[] targetBoneSamples,
                            out error,
                            applyMotionXToDelta: false))
                    {
                        return false;
                    }

                    debugLog?.Invoke(
                        $"[Kimodo][RetargetAvatar] runtime Humanoid->Bone output: " +
                        $"{DescribeBoneSampleMotionForDebug(targetBoneSamples)}, " +
                        $"animatorAvatar={DescribeAvatarForDebug(targetCache.animator != null ? targetCache.animator.avatar : null)}.");

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

        private static string DescribeAvatarForDebug(Avatar avatar)
        {
            if (avatar == null)
            {
                return "<null>";
            }

            HumanDescription description = avatar.humanDescription;
            int humanCount = description.human != null ? description.human.Length : 0;
            int skeletonCount = description.skeleton != null ? description.skeleton.Length : 0;
            return $"name='{avatar.name}',id={avatar.GetInstanceID()},isValid={avatar.isValid}," +
                $"isHuman={avatar.isHuman},human={humanCount},skeleton={skeletonCount}";
        }

        private static string DescribeBoneSampleMotionForDebug(IReadOnlyList<BoneSample> samples)
        {
            if (samples == null || samples.Count < 2 || samples[0] == null || samples[samples.Count - 1] == null)
            {
                return "dynamicBones=unknown";
            }

            BoneSample first = samples[0];
            BoneSample last = samples[samples.Count - 1];
            int count = Mathf.Min(first.localRotations?.Length ?? 0, last.localRotations?.Length ?? 0);
            int dynamic = 0;
            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                float rotationDelta = Quaternion.Angle(first.localRotations[i], last.localRotations[i]);
                Vector3 positionDelta = last.localPositions[i] - first.localPositions[i];
                if (rotationDelta > 0.01f || positionDelta.sqrMagnitude > 1e-8f)
                {
                    dynamic++;
                    if (names.Count < 8)
                    {
                        names.Add(first.boneNames != null && i < first.boneNames.Length
                            ? (string.IsNullOrWhiteSpace(first.boneNames[i]) ? "<root>" : first.boneNames[i])
                            : $"#{i}");
                    }
                }
            }

            return $"dynamicBones={dynamic}/{count},names=[{string.Join(",", names)}]";
        }
    }

}
