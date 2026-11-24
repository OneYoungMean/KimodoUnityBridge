using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoFlowHmrMuscleClipImporter
    {
        internal static bool TryCreateMuscleClip(
            JObject analysis,
            Avatar targetAvatar,
            string clipName,
            out AnimationClip clip,
            out string assetPath,
            out string error)
        {
            clip = null;
            assetPath = string.Empty;
            error = string.Empty;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(targetAvatar))
            {
                error = "The Timeline character must have its own valid Humanoid Avatar.";
                return false;
            }
            if (!string.Equals(analysis?.Value<string>("model_name"), "smplh", StringComparison.OrdinalIgnoreCase))
            {
                error = "FlowHMR response is not an SMPL-H motion.";
                return false;
            }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(analysis.Value<string>("kmb_payload_base64") ?? string.Empty); }
            catch (Exception ex) { error = $"FlowHMR KMB payload is invalid: {ex.Message}"; return false; }
            if (!KimodoRawMotionUtility.TryParseFlatBuffer(bytes, out KimodoRawMotionData motion, out error)) return false;

            const int bodyJointCount = 22;
            // shortcut: the current source Avatar profile is SMPL-X22; SMPL-H fingers
            // remain at the target Avatar's neutral pose until finger muscle mapping exists.
            if (motion.JointCount < bodyJointCount || motion.FrameCount <= 0)
            {
                error = $"SMPL-H motion needs at least {bodyJointCount} body joints; received {motion.JointCount}.";
                return false;
            }
            for (int i = 0; i < bodyJointCount; i++)
            {
                if (!string.Equals(motion.jointNames[i], KimodoRigProfileDatabase.GetJointNamesForModel("smplx")[i], StringComparison.OrdinalIgnoreCase))
                {
                    error = $"SMPL-H body joint {i} is '{motion.jointNames[i]}', expected the SMPL-X22 order.";
                    return false;
                }
            }
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName("smplx", out Avatar sourceAvatar, out error) ||
                !KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(sourceAvatar, "KimodoFlowHmrSource", out RetargetSkeleton source, out error))
                return false;
            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(targetAvatar, "KimodoFlowHmrTarget", out RetargetSkeleton target, out error))
            {
                source.Dispose();
                return false;
            }

            try
            {
                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton("smplx", source,
                        out _, out _, out Transform[] joints, out error)) return false;
                var bindRotations = new Quaternion[joints.Length];
                for (int i = 0; i < joints.Length; i++)
                {
                    if (!TryGetBindLocalRotation(source, joints[i], out bindRotations[i]))
                    {
                        error = $"SMPL-X source Avatar has no bind rotation for '{motion.jointNames[i]}'.";
                        return false;
                    }
                }

                int frames = motion.FrameCount;
                var sourceSamples = new List<MuscleSample>(frames);
                if (!KimodoFlowHmrTiming.TryRead(
                    motion, analysis,
                    out float[] frameTimes, out float durationSeconds, out error))
                    return false;
                if (!motion.HasHumanDetectionTrack)
                {
                    error = "FlowHMR KMB is missing the required human_detection_01 track.";
                    return false;
                }
                if (motion.HasHumanDetectionTrack && !motion.HasAnyHumanDetection)
                {
                    error = "FlowHMR human_detection_01 contains no valid detector frames.";
                    return false;
                }
                int rotationJoints = KimodoRawMotionUtility.ResolveRotationJointCount(motion);
                for (int frame = 0; frame < frames; frame++)
                {
                    if (!TryResolvePoseFrame(motion, frame, frames, out int poseFrame, out error))
                    {
                        return false;
                    }
                    for (int joint = 0; joint < bodyJointCount; joint++)
                    {
                        if (!motion.TryReadUnityLocalRotation(poseFrame, joint, rotationJoints, out Quaternion rotation))
                        {
                            error = $"SMPL-H rotation is invalid at frame {poseFrame}, joint '{motion.jointNames[joint]}'.";
                            return false;
                        }
                        joints[joint].localRotation = bindRotations[joint] * rotation;
                    }
                    if (!motion.TryReadUnityRootPosition(poseFrame, out Vector3 rootPosition))
                    {
                        error = $"SMPL-H root position is invalid at frame {poseFrame}.";
                        return false;
                    }
                    // Match KimodoRawMotionUtility playback semantics: root translation is
                    // carried by the profile root joint, while the virtual skeleton stays at origin.
                    joints[0].localPosition = rootPosition;
                    if (!KimodoRetargetSamplingUtility.TryCaptureSampleData(source, out MuscleSample sourceSample, out error))
                    {
                        error = $"Could not capture FlowHMR frame {frame}: {error}";
                        return false;
                    }
                    sourceSamples.Add(sourceSample);
                }

                if (!KimodoRetargetSamplingUtility.TryRetargetMuscleSamplesToBoneSamples(
                        sourceSamples, motion.FrameRate, target, out BoneSample[] targetBones, out error))
                    return false;
                var samples = new List<MuscleSample>(frames);
                for (int frame = 0; frame < targetBones.Length; frame++)
                {
                    if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(targetBones[frame], target, out error) ||
                        !KimodoRetargetSamplingUtility.TryCaptureSampleData(target, out MuscleSample targetSample, out error))
                    {
                        error = $"Could not capture retargeted FlowHMR frame {frame} on the Timeline character Avatar: {error}";
                        return false;
                    }
                    samples.Add(targetSample);
                }

                clip = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(clipName);
                clip.frameRate = motion.FrameRate;
                if (!KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(
                        samples, frameTimes, durationSeconds, clip, out error))
                {
                    clip = null;
                    return false;
                }
                assetPath = UnityEditor.AssetDatabase.GetAssetPath(clip);
                UnityEditor.EditorUtility.SetDirty(clip);
                KimodoEditorClipWritebackService.FlushWritebackAssets();
                return true;
            }
            finally
            {
                source.Dispose();
                target.Dispose();
            }
        }

        internal static bool TryResolvePoseFrame(
            KimodoRawMotionData motion,
            int sampleFrame,
            int frameCount,
            out int poseFrame,
            out string error)
        {
            poseFrame = 0;
            error = string.Empty;
            if (motion == null || sampleFrame < 0 || sampleFrame >= frameCount || frameCount != motion.FrameCount)
            {
                error = "FlowHMR pose frame is outside the motion sample range.";
                return false;
            }

            if (!motion.HasHumanDetectionTrack)
            {
                error = "FlowHMR KMB is missing the required human_detection_01 track.";
                return false;
            }
            if (!motion.TryResolveHumanDetectionFrame(sampleFrame, out poseFrame))
            {
                error = $"FlowHMR human_detection_01 has no valid pose near frame {sampleFrame}.";
                return false;
            }
            return true;
        }

        private static bool TryGetBindLocalRotation(RetargetSkeleton cache, Transform transform, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (cache?.boneTransforms == null || cache.bindLocalRotations == null) return false;
            for (int i = 0; i < cache.boneTransforms.Length && i < cache.bindLocalRotations.Length; i++)
            {
                if (cache.boneTransforms[i] != transform) continue;
                rotation = cache.bindLocalRotations[i];
                return true;
            }
            return false;
        }
    }
}
