using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine.Timeline
{
    public static class KimodoVectorExtensions
    {
        public static float[] ToArray(this Vector2 value)
        {
            return new[] { value.x, value.y };
        }

        public static float[] ToArray(this Vector3 value)
        {
            return new[] { value.x, value.y, value.z };
        }
    }

    [Serializable]
    public class KimodoConstraintJson
    {
        public string type;
        public List<int> frame_indices = new List<int>();
        public List<float[]> smooth_root_2d;
        public List<float[]> global_root_heading;
        public List<float[][]> local_joints_rot;
        public List<float[]> root_positions;
        public List<string> joint_names;
    }

    public interface IKimodoConstraintMarker
    {
        KimodoConstraintJson ToJson();
    }

    [Serializable]
    public abstract class KimodoConstraintMarkerBase : Marker, IKimodoConstraintMarker
    {
        [Tooltip("If enabled, use manually edited marker values. If disabled, values are sampled from timeline pose at this marker time.")]
        public bool useOverride;

        public abstract string ConstraintType { get; }

        protected KimodoConstraintJson CreateBase()
        {
            return new KimodoConstraintJson
            {
                type = ConstraintType
            };
        }

        public abstract KimodoConstraintJson ToJson();
    }

    [Serializable]
    public sealed class KimodoRoot2DConstraintMarker : KimodoConstraintMarkerBase
    {
        public override string ConstraintType => "root2d";

        public List<int> frameIndices = new List<int> { 0 };
        public List<Vector2> smoothRoot2D = new List<Vector2> { Vector2.zero };
        public bool includeGlobalHeading;
        public List<Vector2> globalRootHeading = new List<Vector2> { Vector2.right };

        public override KimodoConstraintJson ToJson()
        {
            KimodoConstraintJson json = CreateBase();
            json.frame_indices = new List<int>(frameIndices ?? new List<int>());

            var root2d = new List<float[]>();
            if (smoothRoot2D != null)
            {
                for (int i = 0; i < smoothRoot2D.Count; i++)
                {
                    root2d.Add(smoothRoot2D[i].ToArray());
                }
            }
            json.smooth_root_2d = root2d;

            if (includeGlobalHeading && globalRootHeading != null && globalRootHeading.Count > 0)
            {
                var heading = new List<float[]>();
                for (int i = 0; i < globalRootHeading.Count; i++)
                {
                    heading.Add(globalRootHeading[i].ToArray());
                }
                json.global_root_heading = heading;
            }

            return json;
        }
    }

    [Serializable]
    public sealed class KimodoFullBodyConstraintMarker : KimodoConstraintMarkerBase
    {
        public override string ConstraintType => "fullbody";

        public List<int> frameIndices = new List<int> { 0 };
        public List<Vector2> smoothRoot2D = new List<Vector2> { Vector2.zero };
        public List<Vector3> rootPositions = new List<Vector3> { new Vector3(0f, 1f, 0f) };
        [Tooltip("Per frame: local_joints_rot[frame][joint] = axis-angle xyz (radians).")]
        public List<KimodoAxisAngleFrame> localJointRots = new List<KimodoAxisAngleFrame>();

        public override KimodoConstraintJson ToJson()
        {
            KimodoConstraintJson json = CreateBase();
            json.frame_indices = new List<int>(frameIndices ?? new List<int>());

            var root2d = new List<float[]>();
            if (smoothRoot2D != null)
            {
                for (int i = 0; i < smoothRoot2D.Count; i++)
                {
                    root2d.Add(smoothRoot2D[i].ToArray());
                }
            }
            json.smooth_root_2d = root2d;

            var roots = new List<float[]>();
            if (rootPositions != null)
            {
                for (int i = 0; i < rootPositions.Count; i++)
                {
                    roots.Add(rootPositions[i].ToArray());
                }
            }
            json.root_positions = roots;

            json.local_joints_rot = BuildLocalJointRotJson(localJointRots);
            return json;
        }

        internal static List<float[][]> BuildLocalJointRotJson(List<KimodoAxisAngleFrame> frames)
        {
            var result = new List<float[][]>();
            if (frames == null)
            {
                return result;
            }

            for (int i = 0; i < frames.Count; i++)
            {
                KimodoAxisAngleFrame frame = frames[i];
                if (frame == null || frame.joints == null)
                {
                    result.Add(Array.Empty<float[]>());
                    continue;
                }

                float[][] joints = new float[frame.joints.Count][];
                for (int j = 0; j < frame.joints.Count; j++)
                {
                    joints[j] = frame.joints[j].ToArray();
                }
                result.Add(joints);
            }

            return result;
        }
    }

    [Serializable]
    public class KimodoEndEffectorConstraintMarker : KimodoConstraintMarkerBase
    {
        public override string ConstraintType => "end-effector";

        public List<int> frameIndices = new List<int> { 0 };
        [Tooltip("Allowed values follow Kimodo convention, e.g. LeftHand/RightHand/LeftFoot/RightFoot/Hips.")]
        public List<string> jointNames = new List<string> { "LeftHand" };
        public List<Vector2> smoothRoot2D = new List<Vector2> { Vector2.zero };
        public List<Vector3> rootPositions = new List<Vector3> { new Vector3(0f, 1f, 0f) };
        [Tooltip("Per frame: local_joints_rot[frame][joint] = axis-angle xyz (radians).")]
        public List<KimodoAxisAngleFrame> localJointRots = new List<KimodoAxisAngleFrame>();

        public override KimodoConstraintJson ToJson()
        {
            KimodoConstraintJson json = CreateBase();
            json.frame_indices = new List<int>(frameIndices ?? new List<int>());
            json.joint_names = new List<string>(jointNames ?? new List<string>());

            var root2d = new List<float[]>();
            if (smoothRoot2D != null)
            {
                for (int i = 0; i < smoothRoot2D.Count; i++)
                {
                    root2d.Add(smoothRoot2D[i].ToArray());
                }
            }
            json.smooth_root_2d = root2d;

            var roots = new List<float[]>();
            if (rootPositions != null)
            {
                for (int i = 0; i < rootPositions.Count; i++)
                {
                    roots.Add(rootPositions[i].ToArray());
                }
            }
            json.root_positions = roots;
            json.local_joints_rot = KimodoFullBodyConstraintMarker.BuildLocalJointRotJson(localJointRots);
            return json;
        }
    }

    [Serializable]
    public sealed class KimodoLeftHandConstraintMarker : KimodoEndEffectorConstraintMarker
    {
        public override string ConstraintType => "left-hand";
    }

    [Serializable]
    public sealed class KimodoRightHandConstraintMarker : KimodoEndEffectorConstraintMarker
    {
        public override string ConstraintType => "right-hand";
    }

    [Serializable]
    public sealed class KimodoLeftFootConstraintMarker : KimodoEndEffectorConstraintMarker
    {
        public override string ConstraintType => "left-foot";
    }

    [Serializable]
    public sealed class KimodoRightFootConstraintMarker : KimodoEndEffectorConstraintMarker
    {
        public override string ConstraintType => "right-foot";
    }

    [Serializable]
    public sealed class KimodoAxisAngleFrame
    {
        public List<Vector3> joints = new List<Vector3>();
    }
}
