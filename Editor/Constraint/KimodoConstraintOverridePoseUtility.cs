using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoConstraintOverridePoseUtility
    {
        internal static bool TryBuildUnityPoseFromMarker(
            KimodoConstraintMarkerBase marker,
            out KimodoMarkerSampleResult pose,
            out string error)
        {
            pose = null;
            error = string.Empty;

            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            return TryBuildUnityPoseFromJson(marker, marker.ToJson(), out pose, out error);
        }

        internal static bool TryBuildUnityPoseFromJson(
            KimodoConstraintMarkerBase marker,
            KimodoConstraintJson json,
            out KimodoMarkerSampleResult pose,
            out string error)
        {
            pose = null;
            error = string.Empty;

            if (json == null)
            {
                error = "marker json is null";
                return false;
            }

            string type = !string.IsNullOrWhiteSpace(json.type)
                ? json.type
                : marker != null ? marker.ConstraintType : string.Empty;
            bool isRoot2D = string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase);

            pose = new KimodoMarkerSampleResult
            {
                rootPosition = Vector3.zero,
                rootHeading = Vector2.right,
                localAxisAngles = new List<Vector3>()
            };

            if (json.root_positions != null && json.root_positions.Count > 0)
            {
                float[] rp = json.root_positions[0];
                if (rp != null && rp.Length >= 3)
                {
                    pose.rootPosition = KimodoSpaceConversionUtility.ToUnityRootPosition(new Vector3(rp[0], rp[1], rp[2]));
                }
            }
            else if (json.smooth_root_2d != null && json.smooth_root_2d.Count > 0)
            {
                float[] r2 = json.smooth_root_2d[0];
                if (r2 != null && r2.Length >= 2)
                {
                    Vector2 heading2D = KimodoSpaceConversionUtility.ToUnityHeading(new Vector2(r2[0], r2[1]));
                    pose.rootPosition = new Vector3(heading2D.x, 0f, heading2D.y);
                }
            }

            if (json.global_root_heading != null && json.global_root_heading.Count > 0)
            {
                float[] h = json.global_root_heading[0];
                if (h != null && h.Length >= 2)
                {
                    pose.rootHeading = KimodoSpaceConversionUtility.ToUnityHeading(new Vector2(h[0], h[1]));
                }
            }

            float[][] local = null;
            if (json.local_joints_rot != null && json.local_joints_rot.Count > 0)
            {
                local = json.local_joints_rot[0];
            }

            if (!isRoot2D && (local == null || local.Length == 0))
            {
                error = "marker override has no joint rotations";
                return false;
            }

            if (local != null)
            {
                for (int i = 0; i < local.Length; i++)
                {
                    float[] v = local[i];
                    if (v == null || v.Length < 3)
                    {
                        pose.localAxisAngles.Add(Vector3.zero);
                    }
                    else
                    {
                        pose.localAxisAngles.Add(KimodoSpaceConversionUtility.ToUnityAxisAngle(new Vector3(v[0], v[1], v[2])));
                    }
                }
            }

            return true;
        }

        internal static bool TryCapturePoseFromRig(
            Transform[] transforms,
            bool captureRootHeading,
            out KimodoMarkerSampleResult pose,
            out string error)
        {
            pose = null;
            error = string.Empty;

            if (transforms == null || transforms.Length == 0 || transforms[0] == null)
            {
                error = "preview rig is unavailable";
                return false;
            }

            pose = new KimodoMarkerSampleResult
            {
                rootPosition = transforms[0].position,
                rootHeading = Vector2.right,
                localAxisAngles = new List<Vector3>(transforms.Length)
            };

            if (captureRootHeading)
            {
                Vector3 heading = transforms[0].forward;
                heading.y = 0f;
                if (heading.sqrMagnitude > 1e-8f)
                {
                    heading.Normalize();
                    pose.rootHeading = new Vector2(heading.x, heading.z);
                }
            }

            for (int i = 0; i < transforms.Length; i++)
            {
                pose.localAxisAngles.Add(ToAxisAngleVector(transforms[i].localRotation));
            }

            return true;
        }

        internal static bool TryWriteUnityPoseToMarker(
            KimodoConstraintMarkerBase marker,
            KimodoMarkerSampleResult pose,
            out string error)
        {
            error = string.Empty;

            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (pose == null)
            {
                error = "pose is null";
                return false;
            }

            KimodoConstraintJson json = marker.ToJson();
            if (json == null)
            {
                error = "marker json is null";
                return false;
            }

            KimodoMarkerSampleResult kimodoPose = KimodoSpaceConversionUtility.ToKimodoSample(pose);
            if (kimodoPose == null)
            {
                error = "pose conversion failed";
                return false;
            }

            string type = !string.IsNullOrWhiteSpace(json.type) ? json.type : marker.ConstraintType;
            if (string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                json.smooth_root_2d = new List<float[]>
                {
                    new[] { kimodoPose.rootPosition.x, kimodoPose.rootPosition.z }
                };

                if (json.global_root_heading != null)
                {
                    json.global_root_heading = new List<float[]>
                    {
                        new[] { kimodoPose.rootHeading.x, kimodoPose.rootHeading.y }
                    };
                }
            }
            else
            {
                json.smooth_root_2d = new List<float[]>
                {
                    new[] { kimodoPose.rootPosition.x, kimodoPose.rootPosition.z }
                };

                json.root_positions = new List<float[]>
                {
                    new[] { kimodoPose.rootPosition.x, kimodoPose.rootPosition.y, kimodoPose.rootPosition.z }
                };

                json.local_joints_rot = new List<float[][]>
                {
                    ToAxisAngleArray(kimodoPose.localAxisAngles)
                };
            }

            ApplyJsonToMarker(marker, json, useOverride: true);
            return true;
        }

        internal static void ApplyJsonToMarker(
            KimodoConstraintMarkerBase marker,
            KimodoConstraintJson json,
            bool useOverride)
        {
            if (marker == null || json == null)
            {
                return;
            }

            SerializedObject so = new SerializedObject(marker);
            so.Update();

            SetIntList(so.FindProperty("frameIndices"), json.frame_indices);
            SetVector2List(so.FindProperty("smoothRoot2D"), json.smooth_root_2d);
            SetVector3List(so.FindProperty("rootPositions"), json.root_positions);
            SetAxisAngleFrames(so.FindProperty("localJointRots"), json.local_joints_rot);
            SetStringList(so.FindProperty("jointNames"), json.joint_names);

            SerializedProperty includeHeadingProp = so.FindProperty("includeGlobalHeading");
            if (includeHeadingProp != null)
            {
                bool hasHeading = json.global_root_heading != null && json.global_root_heading.Count > 0;
                includeHeadingProp.boolValue = hasHeading;
                if (hasHeading)
                {
                    SetVector2List(so.FindProperty("globalRootHeading"), json.global_root_heading);
                }
            }

            SerializedProperty overrideProp = so.FindProperty("useOverride");
            if (overrideProp != null)
            {
                overrideProp.boolValue = useOverride;
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(marker);
        }

        private static float[][] ToAxisAngleArray(List<Vector3> axisAngles)
        {
            int count = axisAngles != null ? axisAngles.Count : 0;
            float[][] data = new float[count][];
            for (int i = 0; i < count; i++)
            {
                Vector3 v = axisAngles[i];
                data[i] = new[] { v.x, v.y, v.z };
            }

            return data;
        }

        private static Vector3 ToAxisAngleVector(Quaternion q)
        {
            q.Normalize();
            q.ToAngleAxis(out float degrees, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis == Vector3.zero)
            {
                return Vector3.zero;
            }

            if (degrees > 180f)
            {
                degrees -= 360f;
            }

            return axis.normalized * (degrees * Mathf.Deg2Rad);
        }

        private static void SetIntList(SerializedProperty prop, List<int> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            prop.arraySize = values != null ? values.Count : 0;
            for (int i = 0; i < prop.arraySize; i++)
            {
                prop.GetArrayElementAtIndex(i).intValue = values[i];
            }
        }

        private static void SetStringList(SerializedProperty prop, List<string> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            prop.arraySize = values != null ? values.Count : 0;
            for (int i = 0; i < prop.arraySize; i++)
            {
                prop.GetArrayElementAtIndex(i).stringValue = values[i] ?? string.Empty;
            }
        }

        private static void SetVector2List(SerializedProperty prop, List<float[]> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            prop.arraySize = values != null ? values.Count : 0;
            for (int i = 0; i < prop.arraySize; i++)
            {
                float[] v = values[i];
                prop.GetArrayElementAtIndex(i).vector2Value = (v != null && v.Length >= 2) ? new Vector2(v[0], v[1]) : Vector2.zero;
            }
        }

        private static void SetVector3List(SerializedProperty prop, List<float[]> values)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            prop.arraySize = values != null ? values.Count : 0;
            for (int i = 0; i < prop.arraySize; i++)
            {
                float[] v = values[i];
                prop.GetArrayElementAtIndex(i).vector3Value = (v != null && v.Length >= 3) ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;
            }
        }

        private static void SetAxisAngleFrames(SerializedProperty prop, List<float[][]> frames)
        {
            if (prop == null || !prop.isArray)
            {
                return;
            }

            prop.arraySize = frames != null ? frames.Count : 0;
            for (int i = 0; i < prop.arraySize; i++)
            {
                SerializedProperty frameProp = prop.GetArrayElementAtIndex(i);
                SerializedProperty jointsProp = frameProp.FindPropertyRelative("joints");
                if (jointsProp == null || !jointsProp.isArray)
                {
                    continue;
                }

                float[][] joints = frames[i];
                jointsProp.arraySize = joints != null ? joints.Length : 0;
                for (int j = 0; j < jointsProp.arraySize; j++)
                {
                    float[] axis = joints[j];
                    jointsProp.GetArrayElementAtIndex(j).vector3Value = (axis != null && axis.Length >= 3)
                        ? new Vector3(axis[0], axis[1], axis[2])
                        : Vector3.zero;
                }
            }
        }
    }
}
