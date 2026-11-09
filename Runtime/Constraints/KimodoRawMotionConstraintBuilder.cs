using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>Builds protocol constraints directly from Kimodo raw motion.</summary>
    internal static class KimodoRawMotionConstraintBuilder
    {
        internal static bool TryBuildFullBodyFrame(
            KimodoRawMotionData motion,
            string modelName,
            int frameIndex,
            out KimodoConstraintInternalData frame,
            out string error)
        {
            frame = null;
            error = string.Empty;
            if (motion == null)
            {
                error = "Motion data is null.";
                return false;
            }

            KimodoRigProfileDatabase.ResolveProfile(modelName, out _, out string[] profileJointNames, out _);
            if (!KimodoRawMotionUtility.TryResolveMotionJointIndices(
                    motion,
                    profileJointNames,
                    allowPartialJoints: false,
                    out int[] motionJointIndices,
                    out error,
                    allowPositionalFallback: false))
            {
                return false;
            }

            int sourceFrame = Mathf.Clamp(frameIndex, 0, Mathf.Max(0, motion.FrameCount - 1));
            if (!motion.TryReadUnityRootPosition(sourceFrame, out Vector3 rootPosition))
            {
                error = $"Raw motion root position is invalid at frame {sourceFrame}.";
                return false;
            }

            int rotationJointCount = KimodoRawMotionUtility.ResolveRotationJointCount(motion);
            if (rotationJointCount <= 0)
            {
                error = "Raw motion local rotations are empty.";
                return false;
            }

            var localJointAxisAngles = new List<Vector3>(motionJointIndices.Length);
            for (int i = 0; i < motionJointIndices.Length; i++)
            {
                int motionJointIndex = motionJointIndices[i];
                if (!motion.TryReadUnityLocalRotation(
                        sourceFrame,
                        motionJointIndex,
                        rotationJointCount,
                        out Quaternion localRotation))
                {
                    error = $"Raw motion local rotation is invalid at frame {sourceFrame}, " +
                        $"profile joint '{profileJointNames[i]}'.";
                    return false;
                }

                localJointAxisAngles.Add(
                    KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(localRotation));
            }

            frame = new KimodoConstraintInternalData
            {
                rootPosition = rootPosition,
                localJointAxisAngles = localJointAxisAngles
            };
            return true;
        }

        internal static string BuildFullBodyConstraintsJson(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<int> frames,
            double sampleTimeOffsetSeconds = 0.0,
            double? clipDurationSeconds = null)
        {
            if (motion == null || frames == null || frames.Count == 0)
            {
                throw new InvalidOperationException("FullBody bake requires motion keyframes.");
            }

            var fullBodyFrames = new List<KimodoConstraintInternalData>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                int frame = Mathf.Clamp(frames[i], 0, Mathf.Max(0, motion.FrameCount - 1));
                if (!TryBuildFullBodyFrame(motion, modelName, frame, out KimodoConstraintInternalData data, out string error))
                {
                    throw new InvalidOperationException($"FullBody bake sample failed at frame {frame}: {error}");
                }

                data.sampleTime = sampleTimeOffsetSeconds + frame / (double)motion.FrameRate;
                fullBodyFrames.Add(data);
            }

            return BuildFullBodyConstraints(
                fullBodyFrames,
                motion.FrameRate,
                clipDurationSeconds ?? 0.0,
                includeSmoothRoot: true).ToString(Formatting.None);
        }

        internal static JArray BuildFullBodyConstraints(
            IReadOnlyList<KimodoConstraintInternalData> frames,
            float frameRate,
            double clipDurationSeconds,
            bool includeSmoothRoot = false)
        {
            var result = new JArray();
            if (frames == null || frames.Count == 0)
            {
                return result;
            }

            var frameIndices = new JArray();
            var rootPositions = new JArray();
            var localRotations = new JArray();
            var smoothRoot2D = includeSmoothRoot ? new JArray() : null;
            float fps = frameRate > 0f ? frameRate : KimodoMotionModelProfiles.DefaultFrameRate;
            int maxFrame = clipDurationSeconds > 0.0
                ? Mathf.Max(0, KimodoFrameTimeUtility.SecondsToFrameCount(clipDurationSeconds, fps) - 1)
                : int.MaxValue;

            for (int i = 0; i < frames.Count; i++)
            {
                KimodoConstraintInternalData frame = frames[i];
                if (frame == null)
                {
                    continue;
                }

                int index = Mathf.Clamp(KimodoFrameTimeUtility.SecondsToFrameIndex(frame.sampleTime, fps), 0, maxFrame);
                Vector3 root = ToProtocolPosition(frame.rootPosition);
                frameIndices.Add(index);
                rootPositions.Add(new JArray(root.x, root.y, root.z));
                smoothRoot2D?.Add(new JArray(root.x, root.z));
                localRotations.Add(BuildProtocolJoints(frame.localJointAxisAngles));
            }

            if (frameIndices.Count == 0)
            {
                return result;
            }

            var constraint = new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = frameIndices,
                ["root_positions"] = rootPositions,
                ["local_joints_rot"] = localRotations
            };
            if (smoothRoot2D != null)
            {
                constraint["smooth_root_2d"] = smoothRoot2D;
            }
            result.Add(constraint);
            return result;
        }

        internal static string BuildRoot2DConstraintsJson(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<int> frames,
            double sampleTimeOffsetSeconds = 0.0,
            double? clipDurationSeconds = null)
        {
            if (motion == null || frames == null || frames.Count == 0)
            {
                throw new InvalidOperationException("Root2D bake requires motion keyframes.");
            }

            float frameRate = motion.FrameRate;
            int maxFrame = clipDurationSeconds.HasValue
                ? Mathf.Max(0, KimodoFrameTimeUtility.SecondsToFrameCount(clipDurationSeconds.Value, frameRate) - 1)
                : int.MaxValue;
            var frameIndices = new JArray();
            var roots = new JArray();
            var headings = new JArray();
            for (int i = 0; i < frames.Count; i++)
            {
                int sourceFrame = Mathf.Clamp(frames[i], 0, Mathf.Max(0, motion.FrameCount - 1));
                if (!TryBuildFullBodyFrame(motion, modelName, sourceFrame, out KimodoConstraintInternalData data, out string error))
                {
                    throw new InvalidOperationException($"Root2D bake sample failed at frame {sourceFrame}: {error}");
                }

                int frame = KimodoFrameTimeUtility.SecondsToFrameIndex(
                    sampleTimeOffsetSeconds + sourceFrame / (double)frameRate,
                    frameRate);
                frameIndices.Add(Mathf.Clamp(frame, 0, maxFrame));
                Vector3 root = ToProtocolPosition(data.rootPosition);
                roots.Add(new JArray(root.x, root.z));
                Quaternion heading = ResolveRootRotation(data);
                Vector3 forward = KimodoMotionMath.ResolvePlanarHeading(heading) * Vector3.forward;
                if (forward.sqrMagnitude <= 1e-8f) forward = Vector3.forward;
                forward.Normalize();
                headings.Add(new JArray(forward.z, -forward.x));
            }

            return new JArray(new JObject
            {
                ["type"] = "root2d",
                ["frame_indices"] = frameIndices,
                ["smooth_root_2d"] = roots,
                ["global_root_heading"] = headings
            }).ToString(Formatting.None);
        }

        private static Quaternion ResolveRootRotation(KimodoConstraintInternalData frame)
        {
            if (frame?.localJointAxisAngles == null || frame.localJointAxisAngles.Count == 0)
            {
                throw new InvalidOperationException("Raw motion FullBody frame has no root rotation.");
            }
            return KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(frame.localJointAxisAngles[0]);
        }

        private static JArray BuildProtocolJoints(IReadOnlyList<Vector3> jointAxisAngles)
        {
            var joints = new JArray();
            if (jointAxisAngles == null) return joints;
            for (int i = 0; i < jointAxisAngles.Count; i++)
            {
                Vector3 axisAngle = ToProtocolAxisAngle(jointAxisAngles[i]);
                joints.Add(new JArray(axisAngle.x, axisAngle.y, axisAngle.z));
            }
            return joints;
        }

        private static Vector3 ToProtocolPosition(Vector3 position) => new Vector3(-position.x, position.y, position.z);

        private static Vector3 ToProtocolAxisAngle(Vector3 unityAxisAngle)
        {
            Quaternion unity = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(unityAxisAngle);
            Quaternion protocol = new Quaternion(unity.x, -unity.y, -unity.z, unity.w);
            return KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(protocol);
        }
    }
}