#if UNITY_EDITOR
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    public static class KimodoNeutralPoseImporter
    {
        private const string DefaultExportFolder = @"C:\nvlab\NvlabKimodoQuickServer\neutral_pose_exports";
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

        //[MenuItem("Kimodo/Import Neutral Pose JSON/Import Single JSON...")]
        private static void ImportSingleJson()
        {
            string startDir = Directory.Exists(DefaultExportFolder)
                ? DefaultExportFolder
                : Application.dataPath;

            string path = EditorUtility.OpenFilePanel("Select neutral pose JSON", startDir, "json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!TryLoad(path, out NeutralJointsJson data, out string error))
            {
                EditorUtility.DisplayDialog("Kimodo", error, "OK");
                return;
            }

            GameObject root = BuildSkeleton(data, Path.GetFileNameWithoutExtension(path));
            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            Debug.Log($"[Kimodo] Imported neutral pose: {path}");
        }

        //[MenuItem("Kimodo/Import Neutral Pose JSON/Import All From Default Folder")]
        private static void ImportAllFromDefaultFolder()
        {
            if (!Directory.Exists(DefaultExportFolder))
            {
                EditorUtility.DisplayDialog(
                    "Kimodo",
                    $"Default export folder does not exist:\n{DefaultExportFolder}\n\nRun export_kimodo_neutral_poses.bat first.",
                    "OK");
                return;
            }

            string[] files = Directory.GetFiles(DefaultExportFolder, "*_neutral.json", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                EditorUtility.DisplayDialog("Kimodo", "No *_neutral.json files found in default export folder.", "OK");
                return;
            }

            int ok = 0;
            int fail = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (!TryLoad(path, out NeutralJointsJson data, out string error))
                {
                    fail++;
                    Debug.LogError($"[Kimodo] Import failed for {path}: {error}");
                    continue;
                }

                BuildSkeleton(data, Path.GetFileNameWithoutExtension(path));
                ok++;
            }

            Debug.Log($"[Kimodo] Neutral pose import finished. success={ok}, failed={fail}, folder={DefaultExportFolder}");
        }

        private static bool TryLoad(string path, out NeutralJointsJson data, out string error)
        {
            data = null;
            error = string.Empty;

            try
            {
                string jsonText = File.ReadAllText(path);
                data = JsonConvert.DeserializeObject<NeutralJointsJson>(jsonText);
                if (data == null)
                {
                    error = "JSON parse returned null.";
                    return false;
                }
            }
            catch (Exception e)
            {
                error = $"Failed to read or parse JSON: {e.Message}";
                return false;
            }

            return Validate(data, out error);
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
                    error = $"joints[{i}].position must have at least 3 numbers.";
                    return false;
                }
            }

            return true;
        }

        private static GameObject BuildSkeleton(NeutralJointsJson data, string fallbackName)
        {
            int count = data.joints.Count;
            var nodes = new GameObject[count];
            var worldPos = new Vector3[count];
            var tracks = new List<BoneSegmentTrack>(count);

            string skelName = string.IsNullOrWhiteSpace(data.skeleton_name) ? fallbackName : data.skeleton_name;
            string rootName = $"SOMA_{skelName}_neutral";
            GameObject root = new GameObject(rootName);

            for (int i = 0; i < count; i++)
            {
                JointEntry src = data.joints[i];
                nodes[i] = new GameObject(SanitizeName(src.name));
                worldPos[i] = ReadKimodoPosition(src);
            }

            for (int i = 0; i < count; i++)
            {
                JointEntry src = data.joints[i];
                Transform t = nodes[i].transform;
                int p = src.parent_index;

                if (p >= 0 && p < count)
                {
                    t.SetParent(nodes[p].transform, false);
                    t.localPosition = worldPos[i] - worldPos[p];
                }
                else
                {
                    t.SetParent(root.transform, false);
                    t.localPosition = worldPos[i];
                }

                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;

                if (BuildJointSpheres)
                {
                    AddJointSphere(t);
                }

                if (BuildBoneCylinders && p >= 0 && p < count)
                {
                    AddBoneCylinder(nodes[p].transform, t, tracks);
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
                UnityEngine.Object.DestroyImmediate(col);
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
                UnityEngine.Object.DestroyImmediate(col);
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
    }
}
#endif
