using System;
using System.Collections.Generic;
using System.Threading;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    // Converts sparse marker poses to the selected ARDY profile before they cross the protocol boundary.
    internal static class ArdyMarkerClipConstraintEncoder
    {
        internal static bool TryConvert(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            KimodoMotionModelProfile profile,
            List<KimodoClipConstraint> clips,
            out List<KimodoMarkerSampleResult> jsonSamples,
            out string error,
            CancellationToken token = default)
        {
            jsonSamples = new List<KimodoMarkerSampleResult>();
            error = string.Empty;
            if (samples == null) return true;

            for (int index = 0; index < samples.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                KimodoMarkerSampleResult sample = samples[index];
                if (sample == null) continue;
                if (string.Equals(sample.constraintType, "root2d", StringComparison.OrdinalIgnoreCase))
                {
                    jsonSamples.Add(sample);
                    continue;
                }

                if (!TryEncode(sample, profile, out KimodoClipConstraint clip, out error))
                {
                    error = $"Convert ARDY constraint '{sample.constraintType}' at {sample.sampleTime:F3}s failed: {error}";
                    return false;
                }
                clips.Add(clip);
            }
            return true;
        }

        private static bool TryEncode(
            KimodoMarkerSampleResult sample,
            KimodoMotionModelProfile profile,
            out KimodoClipConstraint clip,
            out string error)
        {
            clip = null;
            error = string.Empty;
            if (!IsSupportedConstraintType(sample.constraintType))
            {
                error = $"ARDY supports fullbody, left-hand, right-hand, left-foot, and right-foot marker constraints; received '{sample.constraintType ?? string.Empty}'.";
                return false;
            }
            string sourceModel = ResolveModelName(sample.rigType);
            SkeletonCache source = null;
            SkeletonCache target = null;
            try
            {
                if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(sourceModel, out Avatar sourceAvatar, out error) ||
                    !KimodoRetargetAvatarUtility.TryBuildSkeletonCache(sourceAvatar, "KimodoArdyMarkerSource", out source, out error) ||
                    !KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(profile.ModelName, out Avatar targetAvatar, out error) ||
                    !KimodoRetargetAvatarUtility.TryBuildSkeletonCache(targetAvatar, "KimodoArdyMarkerTarget", out target, out error))
                {
                    return false;
                }

                KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(source);
                if (!KimodoRetargetAvatarUtility.TryApplyMarkerSampleToTransformMap(
                        sample, sourceModel, source.skeletonRoot, source.uniqueNameMap, out error) ||
                    !KimodoRetargetSamplingUtility.TryCaptureMuscleSample(source, out MuscleSample muscles, out error) ||
                    !KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        muscles, profile.SourceFps, target, out BoneSample targetSample, out _, out error) ||
                    !KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(targetSample, target, out error) ||
                    !KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        profile.ModelName, target.skeletonRoot, out string[] names, out int[] parents, out Transform[] joints, out error))
                {
                    return false;
                }

                Transform root = joints[0];
                if (root == null)
                {
                    error = "ARDY profile root joint is missing after muscle retargeting.";
                    return false;
                }
                var rotations = new List<float>(names.Length * 4);
                Quaternion rootRotation = root.rotation.normalized;
                for (int joint = 0; joint < joints.Length; joint++)
                {
                    Quaternion value = joint == 0 ? rootRotation : joints[joint] != null ? joints[joint].localRotation.normalized : Quaternion.identity;
                    rotations.Add(value.w);
                    rotations.Add(value.x);
                    rotations.Add(-value.y);
                    rotations.Add(-value.z);
                }

                byte[] bytes = KimodoRawMotionUtility.ToFlatBuffer(
                    new KimodoRawMotionData(1, names.Length, profile.SourceFps, names, parents, new[] { root.position }, rotations, 0),
                    profile.ModelName);
                int frame = KimodoFrameTimeUtility.SecondsToFrameIndex(sample.sampleTime, profile.SourceFps);
                clip = new KimodoClipConstraint
                {
                    motionBytes = bytes,
                    startTime = frame / profile.SourceFps,
                    duration = 1f / profile.SourceFps,
                    mask = BuildMask(profile.ModelName, sample)
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                source?.Dispose();
                target?.Dispose();
            }
        }

        private static KimodoClipConstraintMask BuildMask(string modelName, KimodoMarkerSampleResult sample)
        {
            if (string.Equals(sample.constraintType, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                return KimodoClipConstraintMask.FullBody(modelName, includeRoot: true);
            }

            string jointName = ResolveEndEffectorJoint(sample.constraintType);
            var mask = new KimodoClipConstraintMask
            {
                rootPosition = new KimodoClipConstraintPositionMask { x = true, y = true, z = true },
                rootHeading = true,
                rootRotation = true
            };
            if (!string.IsNullOrWhiteSpace(jointName))
            {
                mask.joints.Add(new KimodoClipConstraintJointMask
                {
                    jointName = jointName,
                    position = new KimodoClipConstraintPositionMask { x = true, y = true, z = true },
                    rotation = true
                });
            }
            return mask;
        }

        private static bool IsSupportedConstraintType(string type)
        {
            return string.Equals(type, "fullbody", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(ResolveEndEffectorJoint(type));
        }

        private static string ResolveModelName(KimodoConstraintRigType rigType)
        {
            return rigType == KimodoConstraintRigType.Core27 ? KimodoMotionModelProfiles.ArdyCoreModelName :
                rigType == KimodoConstraintRigType.G1 ? "G1" :
                rigType == KimodoConstraintRigType.Smplx ? "SMPLX" :
                KimodoPlayableClip.DefaultBridgeModelName;
        }

        private static string ResolveEndEffectorJoint(string type)
        {
            switch ((type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "left-hand": return "LeftHand";
                case "right-hand": return "RightHand";
                case "left-foot": return "LeftFoot";
                case "right-foot": return "RightFoot";
                default: return string.Empty;
            }
        }
    }
}
