using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Converts raw-motion FullBody frames directly at the protocol boundary.
    /// It deliberately accepts only the internal representation; no
    /// SampleResult object is created on this path.
    /// </summary>
    internal static class KimodoConstraintInternalJsonExporter
    {
        internal static JArray ToJsonArray(
            IReadOnlyList<KimodoConstraintInternalData> frames,
            float frameRate,
            double clipDurationSeconds)
        {
            var result = new JArray();
            if (frames == null || frames.Count == 0)
            {
                return result;
            }

            var frameIndices = new JArray();
            var rootPositions = new JArray();
            var localRotations = new JArray();
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

                int index = KimodoFrameTimeUtility.SecondsToFrameIndex(frame.sampleTime, fps);
                index = Mathf.Clamp(index, 0, maxFrame);
                Vector3 root = new Vector3(-frame.rootPosition.x, frame.rootPosition.y, frame.rootPosition.z);
                frameIndices.Add(index);
                rootPositions.Add(new JArray(root.x, root.y, root.z));

                var joints = new JArray();
                if (frame.localJointAxisAngles != null)
                {
                    for (int joint = 0; joint < frame.localJointAxisAngles.Count; joint++)
                    {
                        Vector3 axisAngle = ToProtocolAxisAngle(frame.localJointAxisAngles[joint]);
                        joints.Add(new JArray(axisAngle.x, axisAngle.y, axisAngle.z));
                    }
                }
                localRotations.Add(joints);
            }

            if (frameIndices.Count == 0)
            {
                return result;
            }

            result.Add(new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = frameIndices,
                ["root_positions"] = rootPositions,
                ["local_joints_rot"] = localRotations
            });
            return result;
        }

        private static Vector3 ToProtocolAxisAngle(Vector3 unityAxisAngle)
        {
            Quaternion unity = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(unityAxisAngle);
            Quaternion protocol = new Quaternion(unity.x, -unity.y, -unity.z, unity.w);
            return KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(protocol);
        }
    }
}
