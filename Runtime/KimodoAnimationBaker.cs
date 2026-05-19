using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools
{
    // Kept class name for compatibility with existing call sites.
    public static class KimodoAnimationBaker
    {
        [Serializable]
        private class MotionJsonData
        {
            public int num_frames;
            public int num_joints;
            public int fps;
            public string[] joint_names;
            public int[] joint_parents;
            public List<List<List<float>>> positions;
            public List<float> local_rot_quats;
        }

        public static bool BakeIntoClip(AnimationClip targetClip, string motionJson, UnityEngine.Timeline.KimodoBakeSkeletonType skeletonType, out string error)
        {
            error = string.Empty;

            if (targetClip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            MotionJsonData data;
            try
            {
                data = ParseMotionJsonFlexible(motionJson);
            }
            catch (Exception e)
            {
                error = $"Failed to parse motionJson: {e.Message}";
                return false;
            }

            if (!ValidateData(data, out error))
            {
                return false;
            }

            if (skeletonType != UnityEngine.Timeline.KimodoBakeSkeletonType.SOMA)
            {
                error = "Only SOMA bake mode is supported.";
                return false;
            }

            float fps = data.fps > 0 ? data.fps : 30f;
            int frameCount = Mathf.Min(data.num_frames > 0 ? data.num_frames : data.positions.Count, data.positions.Count);

            Undo.RecordObject(targetClip, "Bake Kimodo Animation");
            targetClip.ClearCurves();
            BakeSomaCurves(targetClip, data, fps, frameCount);
            targetClip.frameRate = fps;
            EditorUtility.SetDirty(targetClip);
            return true;
        }

        private static MotionJsonData ParseMotionJsonFlexible(string motionJson)
        {
            JToken token = JToken.Parse(motionJson);
            if (token.Type != JTokenType.Object)
            {
                throw new Exception("motionJson root is not an object.");
            }

            JObject obj = (JObject)token;
            MotionJsonData data = obj.ToObject<MotionJsonData>() ?? new MotionJsonData();

            if (data.positions != null && data.positions.Count > 0)
            {
                return data;
            }

            JToken posed = obj["posed_joints"];
            if (posed != null && posed.Type == JTokenType.Array)
            {
                data.positions = posed.ToObject<List<List<List<float>>>>();
                if (data.positions != null && data.positions.Count > 0)
                {
                    if (data.num_frames <= 0) data.num_frames = data.positions.Count;
                    if (data.num_joints <= 0 && data.positions[0] != null) data.num_joints = data.positions[0].Count;
                    return data;
                }
            }

            JToken flat = obj["joints"];
            if (flat != null && flat.Type == JTokenType.Array)
            {
                List<float> flatVals = flat.ToObject<List<float>>();
                int frames = data.num_frames;
                int joints = data.num_joints;
                if (frames > 0 && joints > 0 && flatVals != null && flatVals.Count >= frames * joints * 3)
                {
                    data.positions = new List<List<List<float>>>(frames);
                    int ptr = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        List<List<float>> frame = new List<List<float>>(joints);
                        for (int j = 0; j < joints; j++)
                        {
                            frame.Add(new List<float> { flatVals[ptr], flatVals[ptr + 1], flatVals[ptr + 2] });
                            ptr += 3;
                        }
                        data.positions.Add(frame);
                    }
                    return data;
                }
            }

            return data;
        }

        private static bool ValidateData(MotionJsonData data, out string error)
        {
            error = string.Empty;
            if (data == null)
            {
                error = "Parsed motion data is null.";
                return false;
            }
            if (data.positions == null || data.positions.Count == 0)
            {
                if (data.local_rot_quats == null || data.local_rot_quats.Count == 0)
                {
                    error = "No positions or local_rot_quats in motion data.";
                    return false;
                }
            }
            if (data.joint_names == null || data.joint_names.Length == 0)
            {
                error = "No joint_names in motion data.";
                return false;
            }
            if (data.positions.Count < 2)
            {
                error = "Need at least 2 frames for baking.";
                return false;
            }
            return true;
        }

        private static void BakeSomaCurves(AnimationClip targetClip, MotionJsonData data, float fps, int frameCount)
        {
            int jointCount = Mathf.Min(data.joint_names.Length, data.num_joints > 0 ? data.num_joints : data.joint_names.Length);
            bool hasPositions = data.positions != null && data.positions.Count > 0;
            int rotJointCount = jointCount;
            bool hasRotations = false;
            if (data.local_rot_quats != null && data.local_rot_quats.Count > 0 && frameCount > 0)
            {
                int availableJointCount = data.local_rot_quats.Count / (frameCount * 4);
                rotJointCount = Mathf.Min(jointCount, availableJointCount);
                hasRotations = rotJointCount > 0;
            }
            int rootJoint = FindRootJointIndex(data, jointCount);
            string[] jointPaths = BuildJointPaths(data, jointCount);
            Debug.Log($"[Kimodo] BakeSomaCurves frameCount={frameCount}, jointCount={jointCount}, rootJoint={rootJoint}, local_rot_quats={(data.local_rot_quats != null ? data.local_rot_quats.Count : 0)}, rotJointCount={rotJointCount}, hasRotations={hasRotations}");

            for (int joint = 0; joint < jointCount; joint++)
            {
                string path = jointPaths[joint];

                // Positions are world-space from Kimodo output. For hierarchy-consistent baking,
                // only bake root translation and drive child motion by local rotations.
                if (hasPositions && joint == rootJoint)
                {
                    AnimationCurve px = new AnimationCurve();
                    AnimationCurve py = new AnimationCurve();
                    AnimationCurve pz = new AnimationCurve();

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        Vector3 p = ReadPos(data, f, joint);
                        px.AddKey(t, p.x);
                        py.AddKey(t, p.y);
                        pz.AddKey(t, p.z);
                    }

                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", px);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", py);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", pz);
                }

                if (hasRotations && joint < rotJointCount)
                {
                    AnimationCurve qx = new AnimationCurve();
                    AnimationCurve qy = new AnimationCurve();
                    AnimationCurve qz = new AnimationCurve();
                    AnimationCurve qw = new AnimationCurve();

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        Quaternion q = ReadLocalQuat(data, f, joint, rotJointCount);
                        qx.AddKey(t, q.x);
                        qy.AddKey(t, q.y);
                        qz.AddKey(t, q.z);
                        qw.AddKey(t, q.w);
                    }

                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", qx);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", qy);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", qz);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", qw);
                }
            }
        }

        private static Vector3 ReadPos(MotionJsonData data, int frame, int joint)
        {
            List<float> p = data.positions[frame][joint];
            Vector3 kimodoPos = new Vector3(p[0], p[1], p[2]);
            return ConvertKimodoPosition(kimodoPos);
        }

        private static Quaternion ReadLocalQuat(MotionJsonData data, int frame, int joint, int jointCount)
        {
            int baseIdx = (frame * jointCount + joint) * 4;
            float w = data.local_rot_quats[baseIdx + 0];
            float x = data.local_rot_quats[baseIdx + 1];
            float y = data.local_rot_quats[baseIdx + 2];
            float z = data.local_rot_quats[baseIdx + 3];
            Quaternion q = new Quaternion(x, y, z, w);
            q = q.normalized;
            return ConvertKimodoRotation(q);
        }

        private static Vector3 ConvertKimodoPosition(Vector3 src)
        {
            // Keep consistent with KimodoBVHLoader's Kimodo -> Unity conversion.
            return new Vector3(-src.x, src.y, src.z);
        }

        private static Quaternion ConvertKimodoRotation(Quaternion src)
        {
            // Keep consistent with KimodoBVHLoader's Kimodo -> Unity conversion.
            return new Quaternion(src.x, -src.y, -src.z, src.w);
        }

        private static int FindRootJointIndex(MotionJsonData data, int jointCount)
        {
            if (jointCount <= 0)
            {
                return 0;
            }

            if (data.joint_parents != null && data.joint_parents.Length >= jointCount)
            {
                for (int i = 0; i < jointCount; i++)
                {
                    if (data.joint_parents[i] < 0)
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        private static string[] BuildJointPaths(MotionJsonData data, int jointCount)
        {
            string[] paths = new string[jointCount];
            var visiting = new bool[jointCount];

            for (int i = 0; i < jointCount; i++)
            {
                paths[i] = BuildJointPathRecursive(data, i, jointCount, paths, visiting);
            }

            return paths;
        }

        private static string BuildJointPathRecursive(MotionJsonData data, int joint, int jointCount, string[] cache, bool[] visiting)
        {
            if (joint < 0 || joint >= jointCount)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(cache[joint]))
            {
                return cache[joint];
            }

            if (visiting[joint])
            {
                // Break malformed parent cycles safely.
                string cycName = SanitizeName(data.joint_names[joint]);
                cache[joint] = cycName;
                return cache[joint];
            }

            visiting[joint] = true;
            string safeName = SanitizeName(data.joint_names[joint]);
            int parent = (data.joint_parents != null && joint < data.joint_parents.Length) ? data.joint_parents[joint] : -1;

            if (parent >= 0 && parent < jointCount && parent != joint)
            {
                string parentPath = BuildJointPathRecursive(data, parent, jointCount, cache, visiting);
                cache[joint] = string.IsNullOrWhiteSpace(parentPath)
                    ? safeName
                    : $"{parentPath}/{safeName}";
            }
            else
            {
                cache[joint] = safeName;
            }

            visiting[joint] = false;
            return cache[joint];
        }


        private static string SanitizeName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "joint";
            }
            return input.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
        }
    }
}
