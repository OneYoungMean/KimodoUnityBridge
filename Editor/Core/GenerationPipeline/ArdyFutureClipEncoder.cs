using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace KimodoBridge.Editor
{
    public static class ArdyFutureClipEncoder
    {
        public static byte[] Encode(
            AnimationClip sourceClip,
            Avatar sourceAvatar,
            Avatar targetArdyAvatar,
            string modelName,
            CancellationToken token = default)
        {
            if (sourceClip == null) throw new ArgumentNullException(nameof(sourceClip));
            if (!KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile))
            {
                throw new InvalidOperationException($"Model '{modelName}' is not a registered ARDY profile.");
            }
            if (!KimodoRetargetMarkerSamplingUtility.TryResolveTargetAvatar(
                    targetArdyAvatar, null, profile.ModelName, out Avatar targetAvatar, out string error))
            {
                throw new InvalidOperationException(error);
            }

            SkeletonCache sourceCache = null;
            SkeletonCache targetCache = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext context = null;
            AnimationClip humanoidClip = sourceClip;
            try
            {
                if (!KimodoRetargetSamplingUtility.TryResolveSourceHumanoidClip(
                        sourceClip,
                        sourceAvatar,
                        "KimodoArdyFuture_Source",
                        null,
                        ref sourceCache,
                        out humanoidClip,
                        out error))
                {
                    throw new InvalidOperationException(error);
                }
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        targetAvatar, "KimodoArdyFuture_Target", out targetCache, out error))
                {
                    throw new InvalidOperationException(error);
                }
                if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                        humanoidClip,
                        targetCache,
                        "KimodoArdyFuture_Sampler",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out context,
                        out error))
                {
                    throw new InvalidOperationException(error);
                }
                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        profile.ModelName,
                        targetCache,
                        out string[] jointNames,
                        out int[] jointParents,
                        out Transform[] joints,
                        out error))
                {
                    throw new InvalidOperationException(error);
                }

                int frameCount = Math.Max(1, (int)Math.Floor(sourceClip.length * profile.SourceFps + 1e-9));
                var roots = new Vector3[frameCount];
                var rotations = new List<float>(frameCount * jointNames.Length * 4);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    float sampleTime = frame / profile.SourceFps;
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(context, sampleTime, out error))
                    {
                        throw new InvalidOperationException(error);
                    }
                    roots[frame] = joints[0].position;
                    for (int joint = 0; joint < joints.Length; joint++)
                    {
                        Quaternion value = joints[joint] != null ? joints[joint].localRotation.normalized : Quaternion.identity;
                        rotations.Add(value.w);
                        rotations.Add(value.x);
                        rotations.Add(-value.y);
                        rotations.Add(-value.z);
                    }
                }
                var motion = new KimodoRawMotionData(
                    frameCount,
                    jointNames.Length,
                    profile.SourceFps,
                    jointNames,
                    jointParents,
                    roots,
                    rotations,
                    rootJointIndex: 0);
                return KimodoRawMotionUtility.ToFlatBuffer(motion, profile.ModelName);
            }
            finally
            {
                KimodoRetargetClipSamplingUtility.DestroyClipSamplingContext(context);
                targetCache?.Dispose();
                sourceCache?.Dispose();
                if (!ReferenceEquals(humanoidClip, sourceClip) && humanoidClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(humanoidClip);
                }
            }
        }
    }
}
