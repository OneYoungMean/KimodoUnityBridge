using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public static class KimodoNeutralJointsJsonBuilder
    {
        private const bool BuildJointSpheres = true;
        private const bool BuildBoneCylinders = true;
        private const float JointSphereDiameter = 0.03f;
        private const float BoneCylinderXZ = 0.015f;

        private static readonly Color JointColor = new Color(0.1f, 0.85f, 0.55f, 1f);
        private static readonly Color BoneColor = new Color(0.45f, 0.62f, 0.95f, 1f);

        [Serializable]
        private class NeutralJointsJson
        {
            public int schema_version;
            public string source;
            public string skeleton_name;
            public int joint_count;
            public string units;
            public string space;
            public int root_index;
            public List<JointEntry> joints;
        }

        [Serializable]
        private class JointEntry
        {
            public int index;
            public string name;
            public int parent_index;
            public string parent_name;
            public List<float> position;
        }

        private sealed class BoneSegmentTrack
        {
            public Transform Parent;
            public Transform Child;
            public Transform Segment;
        }

        [MenuItem("Kimodo/Build Skeleton From Neutral Joints JSON")]
        private static void BuildFromSelectedJson()
        {
            TextAsset textAsset = Selection.activeObject as TextAsset;
            if (textAsset == null)
            {
                EditorUtility.DisplayDialog("Kimodo", "Select a neutral-joints JSON TextAsset first.", "OK");
                return;
            }

            if (!TryParse(textAsset.text, out NeutralJointsJson data, out string error))
            {
                EditorUtility.DisplayDialog("Kimodo", $"Invalid neutral-joints JSON:\n{error}", "OK");
                return;
            }

            if (!Validate(data, out error))
            {
                EditorUtility.DisplayDialog("Kimodo", $"JSON missing required fields:\n{error}", "OK");
                return;
            }

            GameObject root = BuildSkeleton(data);
            Selection.activeGameObject = root;
            Debug.Log($"[Kimodo] Built neutral-joints skeleton '{root.name}' ({data.joints.Count} joints).");
        }

        private static bool TryParse(string json, out NeutralJointsJson data, out string error)
        {
            data = null;
            error = string.Empty;
            try
            {
                data = JsonConvert.DeserializeObject<NeutralJointsJson>(json);
                return data != null;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static bool Validate(NeutralJointsJson data, out string error)
        {
            error = string.Empty;
            if (data.joints == null || data.joints.Count == 0)
            {
                error = "joints is empty.";
                return false;
            }

            for (int i = 0; i < data.joints.Count; i++)
            {
                JointEntry j = data.joints[i];
                if (string.IsNullOrWhiteSpace(j.name))
                {
                    error = $"joints[{i}].name is empty.";
                    return false;
                }
                if (j.position == null || j.position.Count < 3)
                {
                    error = $"joints[{i}].position must have 3 floats.";
                    return false;
                }
            }
            return true;
        }

        private static GameObject BuildSkeleton(NeutralJointsJson data)
        {
            int jointCount = data.joints.Count;
            var joints = new GameObject[jointCount];
            var worldPos = new Vector3[jointCount];
            var tracks = new List<BoneSegmentTrack>(jointCount);

            // Root container.
            GameObject root = new GameObject($"SOMA_{(string.IsNullOrWhiteSpace(data.skeleton_name) ? "neutral" : data.skeleton_name)}");

            for (int i = 0; i < jointCount; i++)
            {
                JointEntry src = data.joints[i];
                joints[i] = new GameObject(SanitizeName(src.name));
                worldPos[i] = ReadKimodoPosition(src);
            }

            // Build hierarchy and local positions from world positions.
            for (int i = 0; i < jointCount; i++)
            {
                JointEntry src = data.joints[i];
                int p = src.parent_index;
                if (p >= 0 && p < jointCount)
                {
                    joints[i].transform.SetParent(joints[p].transform, false);
                    joints[i].transform.localPosition = worldPos[i] - worldPos[p];
                }
                else
                {
                    joints[i].transform.SetParent(root.transform, false);
                    joints[i].transform.localPosition = worldPos[i];
                }
                joints[i].transform.localRotation = Quaternion.identity;
                joints[i].transform.localScale = Vector3.one;

                if (BuildJointSpheres)
                {
                    AddJointSphere(joints[i].transform);
                }

                if (BuildBoneCylinders && p >= 0 && p < jointCount)
                {
                    AddBoneCylinder(joints[p].transform, joints[i].transform, tracks);
                }
            }

            if (BuildBoneCylinders)
            {
                UpdateBoneSegments(tracks);
            }

            return root;
        }

        private static Vector3 ReadKimodoPosition(JointEntry src)
        {
            // Kimodo -> Unity conversion:
            // existing project convention mirrors X, keeps Y/Z.
            float x = src.position[0];
            float y = src.position[1];
            float z = src.position[2];
            return new Vector3(-x, y, z);
        }

        private static string SanitizeName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "joint";
            }
            return input.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
        }

        private static void AddJointSphere(Transform joint)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "JointViz";
            sphere.transform.SetParent(joint, false);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localRotation = Quaternion.identity;
            sphere.transform.localScale = Vector3.one * Mathf.Max(0.001f, JointSphereDiameter);

            Collider col = sphere.GetComponent<Collider>();
            if (col != null)
            {
                DestroySafe(col);
            }

            MeshRenderer mr = sphere.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                mr.sharedMaterial.color = JointColor;
            }
        }

        private static void AddBoneCylinder(Transform parent, Transform child, List<BoneSegmentTrack> tracks)
        {
            GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cyl.name = "BoneSegment";
            cyl.transform.SetParent(parent, true);

            Collider col = cyl.GetComponent<Collider>();
            if (col != null)
            {
                DestroySafe(col);
            }

            MeshRenderer mr = cyl.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                mr.sharedMaterial.color = BoneColor;
            }

            tracks.Add(new BoneSegmentTrack
            {
                Parent = parent,
                Child = child,
                Segment = cyl.transform,
            });
        }

        private static void UpdateBoneSegments(List<BoneSegmentTrack> tracks)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return;
            }

            float xz = Mathf.Max(0.0001f, BoneCylinderXZ);
            for (int i = 0; i < tracks.Count; i++)
            {
                BoneSegmentTrack t = tracks[i];
                if (t == null || t.Segment == null || t.Parent == null || t.Child == null)
                {
                    continue;
                }

                Vector3 a = t.Parent.position;
                Vector3 b = t.Child.position;
                Vector3 d = b - a;
                float len = d.magnitude * 0.5f;

                t.Segment.position = (a + b) * 0.5f;
                if (len > 1e-6f)
                {
                    t.Segment.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
                }
                t.Segment.localScale = new Vector3(xz, Mathf.Max(0.0001f, len), xz);
            }
        }

        private static void DestroySafe(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }
            UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
