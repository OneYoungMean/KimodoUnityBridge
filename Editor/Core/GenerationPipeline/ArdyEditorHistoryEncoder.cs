using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class ArdyEditorHistoryEncoder
    {
        internal static bool TryEncode(
            ArdyEditorHistorySource source,
            KimodoMotionModelProfile profile,
            out string cachePath,
            out string error)
        {
            cachePath = string.Empty;
            error = string.Empty;
            if (source?.Clip == null || source.SourceAvatar == null)
            {
                error = "ARDY history source clip or source avatar is missing.";
                return false;
            }
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    profile.ModelName,
                    out Avatar targetAvatar,
                    out error))
            {
                return false;
            }
            if (!KimodoRetargetToolsEditor.TryGetOrCreateEditorBoneClip(
                    source.Clip,
                    source.SourceAvatar,
                    targetAvatar,
                    forceRefresh: false,
                    out AnimationClip targetClip,
                    out _,
                    out error))
            {
                return false;
            }

            SkeletonCache cache = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        targetAvatar,
                        "ArdyHistoryEncoder",
                        out cache,
                        out error))
                {
                    return false;
                }
                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        profile.ModelName,
                        cache.skeletonRoot,
                        out string[] jointNames,
                        out int[] jointParents,
                        out Transform[] joints,
                        out error))
                {
                    return false;
                }
                if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                        targetClip,
                        cache,
                        "ArdyHistoryEncoder",
                        KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(targetClip),
                        out context,
                        out error))
                {
                    return false;
                }

                int maxFrames = profile.MaxHistoryHandles * profile.HorizonFrames;
                double timelineDuration = Math.Max(0.0, source.TimelineDurationSeconds);
                int requestedFrames = Math.Max(
                    profile.FramesPerToken,
                    (int)Math.Floor(timelineDuration * profile.SourceFps + 1e-9));
                int frameCount = Math.Min(maxFrames, requestedFrames);
                frameCount -= frameCount % profile.FramesPerToken;
                if (frameCount <= 0)
                {
                    error = "ARDY history source is shorter than one model token.";
                    return false;
                }

                double sampledTimelineDuration = frameCount / profile.SourceFps;
                double timelineStart = Math.Max(0.0, timelineDuration - sampledTimelineDuration);
                double timeScale = Math.Max(1e-6, source.TimeScale);
                var rootPositions = new Vector3[frameCount];
                var rotations = new List<float>(frameCount * jointNames.Length * 4);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    double timelineTime = timelineStart + frame / profile.SourceFps;
                    float sampleTime = (float)Math.Min(
                        targetClip.length,
                        Math.Max(0.0, source.ClipInSeconds + timelineTime * timeScale));
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(context, sampleTime, out error))
                    {
                        return false;
                    }

                    rootPositions[frame] = cache.skeletonRoot.InverseTransformPoint(joints[0].position);
                    for (int joint = 0; joint < joints.Length; joint++)
                    {
                        Quaternion unity = joints[joint] != null ? joints[joint].localRotation.normalized : Quaternion.identity;
                        rotations.Add(unity.w);
                        rotations.Add(unity.x);
                        rotations.Add(-unity.y);
                        rotations.Add(-unity.z);
                    }
                }

                var motion = new KimodoRawMotionData(
                    frameCount,
                    jointNames.Length,
                    profile.SourceFps,
                    jointNames,
                    jointParents,
                    rootPositions,
                    rotations,
                    rootJointIndex: 0);
                byte[] payload = KimodoRawMotionUtility.ToFlatBuffer(motion, profile.ModelName);
                cachePath = ArdyUnityMotionCache.Write(payload, "redirected-history");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                KimodoRetargetClipSamplingUtility.DestroyClipSamplingContext(context);
                cache?.Dispose();
            }
        }
    }
}
