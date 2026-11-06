using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintPoseUtility
    {
        internal static bool TryCreateRestPoseSample(
            KimodoTimelineInOutConstraintContext context,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            if (context?.Animator == null)
            {
                error = "Constraint rest pose requires the bound Character Animator.";
                return false;
            }
            var avatar = KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(context.Track, context.Animator);
            return KimodoInOutConstraintTools.TryCaptureOriginalScenePose(
                context.Animator.transform.root.gameObject, context.Animator, avatar.Avatar,
                restPose: true, characterName: context.Animator.name, out sample, out error);
        }

        internal static KimodoMarkerSampleResult BuildCapturedSampleResult(
            MuscleSample sample,
            RetargetSkeleton cache,
            double sampleTime)
        {
            bool hasSampleData = sample?.IsValid == true;
            var result = new KimodoMarkerSampleResult
            {
                sampleData = sample?.Clone() ?? new MuscleSample(),
                sampleValid = hasSampleData,
                enableMask = hasSampleData ? KimodoConstraintMask.ForType("fullbody") : new KimodoConstraintMask(),
                constraintMode = "fullbody",
                sampleTime = sampleTime,
                enabled = true
            };
            KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(cache, result);
            return result;
        }

        internal static JObject BuildPoseJson(KimodoMarkerSampleResult sample)
        {
            ValidateSample(sample);
            return new JObject
            {
                ["muscles"] = new JArray(sample.sampleData.data.Take(KimodoSampleDataLayout.BodyMuscleCount)),
                ["root"] = FullTransformJson(GetRootTransform(sample)),
                ["hands"] = new JObject
                {
                    ["left"] = FullTransformJson(GetEndEffector(sample, "left_hand")),
                    ["right"] = FullTransformJson(GetEndEffector(sample, "right_hand"))
                },
                ["feet"] = new JObject
                {
                    ["left"] = FullTransformJson(GetEndEffector(sample, "left_foot")),
                    ["right"] = FullTransformJson(GetEndEffector(sample, "right_foot"))
                }
            };
        }

        internal static bool TryAppendSmplxConstraints(JObject analysis, out string error)
        {
            error = string.Empty;
            if (analysis?["poses"] is not JArray poses || poses.Count == 0)
            {
                error = "Mocap result contains no poses to convert.";
                return false;
            }
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName("smplx", out Avatar avatar, out error))
            {
                return false;
            }
            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar, "KimodoMocapConstraintSampler", out RetargetSkeleton cache, out error))
            {
                return false;
            }

            try
            {
                if (!TryResolveJointTransforms(cache, analysis["joint_names"] as JArray,
                        out string[] jointNames, out Transform[] joints, out error))
                {
                    return false;
                }
                Transform hips = cache.animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips == null || !TryGetBindLocalRotation(cache, hips, out Quaternion hipsBindRotation))
                {
                    error = "SMPL-X Avatar does not expose a bind rotation for Humanoid Hips.";
                    return false;
                }

                var bindRotations = new Quaternion[joints.Length];
                for (int index = 0; index < joints.Length; index++)
                {
                    if (!TryGetBindLocalRotation(cache, joints[index], out bindRotations[index]))
                    {
                        error = $"SMPL-X Avatar has no bind rotation for joint '{jointNames[index]}'.";
                        return false;
                    }
                }

                var samples = new System.Collections.Generic.List<KimodoMarkerSampleResult>(poses.Count);
                for (int frameIndex = 0; frameIndex < poses.Count; frameIndex++)
                {
                    if (poses[frameIndex] is not JObject pose ||
                        pose["global_orient_axis_angle"] is not JArray globalOrient ||
                        pose["body_pose_axis_angle"] is not JArray bodyPose ||
                        pose["translation_m"] is not JArray translation ||
                        !TryReadVector(globalOrient, out Vector3 globalAxisAngle) ||
                        !TryReadVector(translation, out Vector3 rootPosition) ||
                        bodyPose.Count != jointNames.Length * 3)
                    {
                        error = $"Mocap pose {frameIndex} has invalid SMPL-X frame data.";
                        return false;
                    }

                    hips.localRotation = hipsBindRotation * ToUnityRotation(globalAxisAngle);
                    for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
                    {
                        var axisAngle = new Vector3(
                            bodyPose[jointIndex * 3].Value<float>(),
                            bodyPose[jointIndex * 3 + 1].Value<float>(),
                            bodyPose[jointIndex * 3 + 2].Value<float>());
                        if (!IsFinite(axisAngle))
                        {
                            error = $"Mocap pose {frameIndex} has a non-finite rotation for '{jointNames[jointIndex]}'.";
                            return false;
                        }
                        joints[jointIndex].localRotation = bindRotations[jointIndex] * ToUnityRotation(axisAngle);
                    }
                    cache.root.transform.position = new Vector3(-rootPosition.x, rootPosition.y, rootPosition.z);
                    if (!KimodoRetargetSamplingUtility.TryCaptureSampleData(cache, out MuscleSample sample, out error) ||
                        sample == null || !sample.IsValid)
                    {
                        error = $"Mocap pose {frameIndex} could not be captured as a Humanoid sample: {error}";
                        return false;
                    }

                    double sampleTime = pose.Value<double?>("timestamp_seconds") ??
                        KimodoFrameTimeUtility.CommandFrameIndexToSeconds(frameIndex);
                    samples.Add(BuildCapturedSampleResult(sample, cache, sampleTime));
                }
                analysis["constraints"] = JArray.Parse(KimodoConstraintJsonExporter.ToConstraintsJson(
                    samples,
                    new KimodoConstraintExportContext
                    {
                        projectedPoseProjector = KimodoRuntimeConstraintExportProjector.Create("smplx")
                    },
                    exportFps: KimodoFrameTimeUtility.CommandFrameRate));
                return true;
            }
            finally
            {
                cache.Dispose();
            }
        }

        private static bool TryResolveJointTransforms(
            RetargetSkeleton cache,
            JArray names,
            out string[] jointNames,
            out Transform[] joints,
            out string error)
        {
            error = string.Empty;
            jointNames = names?.ToObject<string[]>() ?? Array.Empty<string>();
            joints = new Transform[jointNames.Length];
            if (jointNames.Length != 21)
            {
                error = $"Expected 21 SMPL-X body joints, received {jointNames.Length}.";
                return false;
            }
            for (int index = 0; index < jointNames.Length; index++)
            {
                if (!KimodoRetargetAvatarUtility.TryGetUniqueCachedTransformByName(
                        cache, jointNames[index], out joints[index], out bool ambiguous) || joints[index] == null)
                {
                    error = ambiguous
                        ? $"SMPL-X Avatar joint name '{jointNames[index]}' is ambiguous."
                        : $"SMPL-X Avatar has no joint named '{jointNames[index]}'.";
                    return false;
                }
            }
            return true;
        }

        private static bool TryGetBindLocalRotation(RetargetSkeleton cache, Transform target, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (cache?.boneTransforms == null || cache.bindLocalRotations == null) return false;
            for (int index = 0; index < cache.boneTransforms.Length && index < cache.bindLocalRotations.Length; index++)
            {
                if (cache.boneTransforms[index] == target)
                {
                    rotation = cache.bindLocalRotations[index];
                    return true;
                }
            }
            return false;
        }

        private static Quaternion ToUnityRotation(Vector3 axisAngle)
        {
            Quaternion source = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(axisAngle);
            return new Quaternion(source.x, -source.y, -source.z, source.w).normalized;
        }

        private static bool TryReadVector(JArray values, out Vector3 result)
        {
            result = Vector3.zero;
            if (values == null || values.Count != 3) return false;
            result = new Vector3(values[0].Value<float>(), values[1].Value<float>(), values[2].Value<float>());
            return IsFinite(result);
        }

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void ValidateSample(KimodoMarkerSampleResult sample)
        {
            if (sample?.sampleData == null || !sample.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose source has no valid 70-value sampleData payload.");
            }
        }

        private static JObject FullTransformJson(KimodoRigidTransform transform) => new JObject
        {
            ["t"] = new JArray(transform.t.x, transform.t.y, transform.t.z),
            ["q"] = new JArray(transform.q.x, transform.q.y, transform.q.z, transform.q.w),
            ["enable_rotation"] = transform.enableRotation
        };

        private static KimodoRigidTransform GetEndEffector(KimodoMarkerSampleResult sample, string name)
        {
            if (sample?.effectors == null) throw new InvalidOperationException("SampleResult has no effector payload.");
            return name switch
            {
                "left_hand" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftHand, sample.rootOverride),
                "right_hand" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightHand, sample.rootOverride),
                "left_foot" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftFoot, sample.rootOverride),
                "right_foot" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightFoot, sample.rootOverride),
                _ => throw new InvalidOperationException($"Unsupported end effector '{name}'.")
            };
        }

        private static KimodoRigidTransform GetRootTransform(KimodoMarkerSampleResult sample) => sample?.rootOverride != null
            ? new KimodoRigidTransform { t = sample.rootOverride.t, q = sample.rootOverride.q }
            : new KimodoRigidTransform { t = Vector3.zero, q = Quaternion.identity };
    }
}
