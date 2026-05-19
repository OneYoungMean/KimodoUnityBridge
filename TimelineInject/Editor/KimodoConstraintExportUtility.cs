using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public static class KimodoConstraintExportUtility
    {
        private const string LogPrefix = "[Kimodo][ConstraintExport]";
        private static readonly string[] Soma30Names =
        {
            "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "Jaw", "LeftEye", "RightEye",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandThumbEnd", "LeftHandMiddleEnd",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandThumbEnd", "RightHandMiddleEnd",
            "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase", "RightLeg", "RightShin", "RightFoot", "RightToeBase"
        };
        private static readonly int[] Soma30Parents =
        {
            -1, 0, 1, 2, 3, 4, 5, 6, 6, 6, 3, 10, 11, 12, 13, 13, 3, 16, 17, 18, 19, 19, 0, 22, 23, 24, 0, 26, 27, 28
        };

        public static bool TryBuildAndWriteConstraintsFile(
            TimelineClip sourceClip,
            out string absolutePath,
            out string error)
        {
            absolutePath = string.Empty;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "No selected timeline clip for constraint export.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            var markers = GatherKimodoMarkers(track, sourceClip);
            if (markers.Count == 0)
            {
                // No constraints is valid: caller can clear sampler constraints input.
                return true;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            Transform skeletonRoot = animator.transform;
            if (skeletonRoot == null)
            {
                error = "Animator transform is null.";
                return false;
            }

            List<KimodoConstraintJson> constraints = new List<KimodoConstraintJson>();
            double originalTime = director.time;
            DirectorWrapMode originalWrap = director.extrapolationMode;

            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;

                for (int i = 0; i < markers.Count; i++)
                {
                    if (!TryBuildMarkerConstraint(markers[i], sourceClip, skeletonRoot, animator, director, out KimodoConstraintJson constraint, out error))
                    {
                        Debug.LogError($"{LogPrefix} Build marker constraint failed at index={i}: {error}");
                        return false;
                    }
                    Debug.Log($"{LogPrefix} Marker[{i}] {DescribeConstraint(constraint)}");
                    constraints.Add(constraint);
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrap;
            }

            List<KimodoConstraintJson> merged = MergeConstraintsByType(constraints);
            for (int i = 0; i < merged.Count; i++)
            {
                Debug.Log($"{LogPrefix} Merged[{i}] {DescribeConstraint(merged[i])}");
            }

            string json = JsonConvert.SerializeObject(
                merged,
                Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            string tempDir = Path.Combine(Application.dataPath, "KimodoTemp");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            string fileName = $"constraints_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json";
            absolutePath = Path.Combine(tempDir, fileName);
            File.WriteAllText(absolutePath, json);
            Debug.Log($"{LogPrefix} Exported {merged.Count} merged constraint(s) to: {absolutePath}");
            return true;
        }

        internal static bool TryBuildAutoConstraintPreview(
            KimodoConstraintMarkerBase marker,
            out KimodoConstraintJson output,
            out string error)
        {
            output = null;
            error = string.Empty;

            if (marker == null)
            {
                error = "Marker is null.";
                return false;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip clipRange) || clipRange == null)
            {
                error = "Cannot resolve KimodoPlayableClip range for marker.";
                return false;
            }

            TrackAsset track = clipRange.GetParentTrack();
            if (track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            Transform skeletonRoot = animator.transform;
            if (skeletonRoot == null)
            {
                error = "Animator transform is null.";
                return false;
            }

            double originalTime = director.time;
            DirectorWrapMode originalWrap = director.extrapolationMode;

            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;
                director.time = marker.time;
                director.Evaluate();

                SkeletonPose pose = CapturePose(skeletonRoot, animator);
                if (pose.LocalAxisAngles.Count == 0)
                {
                    error = $"Failed to sample skeleton pose at marker {marker.time:F3}s.";
                    return false;
                }

                int frameIndex = KimodoConstraintMarkerEditorUtility.TimeToKimodoFrameIndex(clipRange, marker.time);
                output = BuildAutoConstraint(marker, frameIndex, pose);
                return ValidateConstraint(output, out error);
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrap;
            }
        }

        private static List<KimodoConstraintMarkerBase> GatherKimodoMarkers(TrackAsset track, TimelineClip clipRange)
        {
            var markers = new List<KimodoConstraintMarkerBase>();
            double minTime = clipRange != null ? clipRange.start : double.MinValue;
            double maxTime = clipRange != null ? clipRange.end : double.MaxValue;
            foreach (IMarker marker in track.GetMarkers())
            {
                if (marker is KimodoConstraintMarkerBase kimodoMarker)
                {
                    if (kimodoMarker.time < minTime || kimodoMarker.time > maxTime)
                    {
                        continue;
                    }
                    markers.Add(kimodoMarker);
                }
            }

            markers.Sort((a, b) => a.time.CompareTo(b.time));
            return markers;
        }

        private static bool TryBuildMarkerConstraint(
            KimodoConstraintMarkerBase marker,
            TimelineClip sourceClip,
            Transform skeletonRoot,
            Animator animator,
            PlayableDirector director,
            out KimodoConstraintJson output,
            out string error)
        {
            output = null;
            error = string.Empty;

            if (marker == null)
            {
                error = "Marker is null.";
                return false;
            }

            bool allowOverride = marker is not KimodoEndEffectorConstraintMarker ee || !string.Equals(ee.ConstraintType, "end-effector", StringComparison.OrdinalIgnoreCase);
            if (allowOverride && marker.useOverride)
            {
                output = marker.ToJson();
                if (ValidateConstraint(output, out error))
                {
                    return true;
                }

                Debug.LogWarning($"{LogPrefix} Override data invalid for marker(type={marker.ConstraintType}, time={marker.time:F3}s): {error}. Fallback to sampled timeline pose.");
                // Continue with sampled pose fallback below.
            }

            double evalTime = marker.time;
            director.time = evalTime;
            director.Evaluate();

            SkeletonPose pose = CapturePose(skeletonRoot, animator);
            if (pose.LocalAxisAngles.Count == 0)
            {
                error = $"Failed to sample skeleton pose at marker {marker.time:F3}s.";
                return false;
            }

            int frameIndex = KimodoConstraintMarkerEditorUtility.TimeToKimodoFrameIndex(sourceClip, marker.time);
            output = BuildAutoConstraint(marker, frameIndex, pose);
            return ValidateConstraint(output, out error);
        }

        private static bool ValidateConstraint(KimodoConstraintJson json, out string error)
        {
            error = string.Empty;
            if (json == null)
            {
                error = "Constraint json is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(json.type))
            {
                error = "Constraint type is empty.";
                return false;
            }

            if (json.frame_indices == null || json.frame_indices.Count == 0)
            {
                error = $"Constraint {json.type} has no frame_indices.";
                return false;
            }

            if (string.Equals(json.type, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                if (json.local_joints_rot == null || json.local_joints_rot.Count == 0)
                {
                    error = "fullbody.local_joints_rot is empty.";
                    return false;
                }
                if (json.root_positions == null || json.root_positions.Count == 0)
                {
                    error = "fullbody.root_positions is empty.";
                    return false;
                }

                if (json.frame_indices.Count != json.local_joints_rot.Count)
                {
                    error = $"fullbody frame_indices count ({json.frame_indices.Count}) != local_joints_rot frame count ({json.local_joints_rot.Count}).";
                    return false;
                }
                if (json.frame_indices.Count != json.root_positions.Count)
                {
                    error = $"fullbody frame_indices count ({json.frame_indices.Count}) != root_positions count ({json.root_positions.Count}).";
                    return false;
                }

                for (int i = 0; i < json.local_joints_rot.Count; i++)
                {
                    float[][] joints = json.local_joints_rot[i];
                    if (joints == null || joints.Length == 0)
                    {
                        error = $"fullbody.local_joints_rot[{i}] is empty.";
                        return false;
                    }

                    for (int j = 0; j < joints.Length; j++)
                    {
                        float[] axis = joints[j];
                        if (axis == null || axis.Length < 3)
                        {
                            error = $"fullbody.local_joints_rot[{i}][{j}] invalid axis-angle vector.";
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static string DescribeConstraint(KimodoConstraintJson c)
        {
            if (c == null)
            {
                return "null";
            }

            int frameCount = c.frame_indices != null ? c.frame_indices.Count : 0;
            int root2dCount = c.smooth_root_2d != null ? c.smooth_root_2d.Count : 0;
            int rootPosCount = c.root_positions != null ? c.root_positions.Count : 0;
            int rotFrames = c.local_joints_rot != null ? c.local_joints_rot.Count : 0;
            int rotJoints = 0;
            if (rotFrames > 0 && c.local_joints_rot[0] != null)
            {
                rotJoints = c.local_joints_rot[0].Length;
            }
            return $"type={c.type}, frames={frameCount}, smooth_root_2d={root2dCount}, root_positions={rootPosCount}, local_joints_rot={rotFrames}x{rotJoints}x3";
        }

        private static KimodoConstraintJson BuildAutoConstraint(KimodoConstraintMarkerBase marker, int frameIndex, SkeletonPose pose)
        {
            if (marker is KimodoRoot2DConstraintMarker root2DMarker)
            {
                var json = root2DMarker.ToJson();
                json.frame_indices = new List<int> { frameIndex };
                json.smooth_root_2d = new List<float[]> { new[] { pose.RootPosition.x, pose.RootPosition.z } };
                if (root2DMarker.includeGlobalHeading)
                {
                    json.global_root_heading = new List<float[]> { new[] { pose.RootHeading.x, pose.RootHeading.y } };
                }
                return json;
            }

            if (marker is KimodoFullBodyConstraintMarker)
            {
                return BuildFullBodyConstraint(frameIndex, pose);
            }

            if (marker is KimodoEndEffectorConstraintMarker eeMarker)
            {
                return BuildEndEffectorConstraint(eeMarker, frameIndex, pose);
            }

            return marker.ToJson();
        }

        private static KimodoConstraintJson BuildFullBodyConstraint(int frameIndex, SkeletonPose pose)
        {
            var full = new KimodoConstraintJson
            {
                type = "fullbody",
                frame_indices = new List<int> { frameIndex },
                smooth_root_2d = new List<float[]> { new[] { pose.RootPosition.x, pose.RootPosition.z } },
                root_positions = new List<float[]> { new[] { pose.RootPosition.x, pose.RootPosition.y, pose.RootPosition.z } },
                local_joints_rot = new List<float[][]> { ToAxisAngleArray(pose.LocalAxisAngles) }
            };
            return full;
        }

        private static KimodoConstraintJson BuildEndEffectorConstraint(KimodoEndEffectorConstraintMarker eeMarker, int frameIndex, SkeletonPose pose)
        {
            var json = new KimodoConstraintJson
            {
                type = eeMarker.ConstraintType,
                frame_indices = new List<int> { frameIndex },
                joint_names = ResolveJointNamesForType(eeMarker.ConstraintType, eeMarker.jointNames),
                smooth_root_2d = new List<float[]> { new[] { pose.RootPosition.x, pose.RootPosition.z } },
                root_positions = new List<float[]> { new[] { pose.RootPosition.x, pose.RootPosition.y, pose.RootPosition.z } },
                local_joints_rot = new List<float[][]> { ToAxisAngleArray(pose.LocalAxisAngles) }
            };
            return json;
        }

        private static List<string> ResolveJointNamesForType(string type, List<string> fallback)
        {
            switch (type)
            {
                case "left-hand":
                    return new List<string> { "LeftHand" };
                case "right-hand":
                    return new List<string> { "RightHand" };
                case "left-foot":
                    return new List<string> { "LeftFoot" };
                case "right-foot":
                    return new List<string> { "RightFoot" };
                case "end-effector":
                    return (fallback == null || fallback.Count == 0)
                        ? new List<string> { "LeftHand" }
                        : new List<string>(fallback);
                default:
                    return new List<string>();
            }
        }

        private static float[][] ToAxisAngleArray(List<Vector3> axisAngles)
        {
            float[][] data = new float[axisAngles.Count][];
            for (int i = 0; i < axisAngles.Count; i++)
            {
                Vector3 v = axisAngles[i];
                data[i] = new[] { v.x, v.y, v.z };
            }
            return data;
        }

        private static List<KimodoConstraintJson> MergeConstraintsByType(List<KimodoConstraintJson> constraints)
        {
            var output = new List<KimodoConstraintJson>();
            if (constraints == null || constraints.Count == 0)
            {
                return output;
            }

            var buckets = new Dictionary<string, List<KimodoConstraintJson>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (KimodoConstraintJson c in constraints)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.type))
                {
                    continue;
                }

                if (!buckets.TryGetValue(c.type, out List<KimodoConstraintJson> list))
                {
                    list = new List<KimodoConstraintJson>();
                    buckets[c.type] = list;
                    order.Add(c.type);
                }
                list.Add(c);
            }

            foreach (string type in order)
            {
                List<KimodoConstraintJson> group = buckets[type];
                if (group == null || group.Count == 0)
                {
                    continue;
                }

                group.Sort((a, b) =>
                {
                    int af = (a.frame_indices != null && a.frame_indices.Count > 0) ? a.frame_indices[0] : int.MaxValue;
                    int bf = (b.frame_indices != null && b.frame_indices.Count > 0) ? b.frame_indices[0] : int.MaxValue;
                    return af.CompareTo(bf);
                });

                KimodoConstraintJson merged = BuildMergedConstraint(type, group);
                output.Add(merged);
            }

            return output;
        }

        private static KimodoConstraintJson BuildMergedConstraint(string type, List<KimodoConstraintJson> group)
        {
            var merged = new KimodoConstraintJson
            {
                type = type,
                frame_indices = new List<int>()
            };

            bool isRoot2D = string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase);
            bool isFullBody = string.Equals(type, "fullbody", StringComparison.OrdinalIgnoreCase);
            bool isEndEffectorFamily = string.Equals(type, "end-effector", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(type, "left-hand", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(type, "right-hand", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(type, "left-foot", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(type, "right-foot", StringComparison.OrdinalIgnoreCase);

            if (isRoot2D || isFullBody || isEndEffectorFamily)
            {
                merged.smooth_root_2d = new List<float[]>();
            }
            if (isFullBody || isEndEffectorFamily)
            {
                merged.root_positions = new List<float[]>();
                merged.local_joints_rot = new List<float[][]>();
            }
            if (isRoot2D)
            {
                merged.global_root_heading = new List<float[]>();
            }

            if (isEndEffectorFamily && group[0].joint_names != null && group[0].joint_names.Count > 0)
            {
                merged.joint_names = new List<string>(group[0].joint_names);
            }

            foreach (KimodoConstraintJson c in group)
            {
                if (c.frame_indices == null || c.frame_indices.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < c.frame_indices.Count; i++)
                {
                    int frame = c.frame_indices[i];
                    merged.frame_indices.Add(frame);

                    if (merged.smooth_root_2d != null && c.smooth_root_2d != null)
                    {
                        merged.smooth_root_2d.Add(GetListItemOrFallback(c.smooth_root_2d, i, 2));
                    }

                    if (merged.root_positions != null && c.root_positions != null)
                    {
                        merged.root_positions.Add(GetListItemOrFallback(c.root_positions, i, 3));
                    }

                    if (merged.local_joints_rot != null && c.local_joints_rot != null)
                    {
                        merged.local_joints_rot.Add(GetListItemOrFallback(c.local_joints_rot, i));
                    }

                    if (merged.global_root_heading != null && c.global_root_heading != null)
                    {
                        merged.global_root_heading.Add(GetListItemOrFallback(c.global_root_heading, i, 2));
                    }
                }
            }

            // Remove optional fields if no data.
            if (merged.smooth_root_2d != null && merged.smooth_root_2d.Count == 0)
            {
                merged.smooth_root_2d = null;
            }
            if (merged.root_positions != null && merged.root_positions.Count == 0)
            {
                merged.root_positions = null;
            }
            if (merged.local_joints_rot != null && merged.local_joints_rot.Count == 0)
            {
                merged.local_joints_rot = null;
            }
            if (merged.global_root_heading != null && merged.global_root_heading.Count == 0)
            {
                merged.global_root_heading = null;
            }

            // Keep aligned ordering after merge.
            SortConstraintFramesInPlace(merged);
            return merged;
        }

        private static float[] GetListItemOrFallback(List<float[]> values, int index, int expectedLength)
        {
            if (values == null || values.Count == 0)
            {
                return NewZeroArray(expectedLength);
            }

            int i = Mathf.Clamp(index, 0, values.Count - 1);
            float[] value = values[i];
            if (value == null || value.Length < expectedLength)
            {
                return NewZeroArray(expectedLength);
            }

            var copy = new float[expectedLength];
            Array.Copy(value, copy, expectedLength);
            return copy;
        }

        private static float[][] GetListItemOrFallback(List<float[][]> values, int index)
        {
            if (values == null || values.Count == 0)
            {
                return Array.Empty<float[]>();
            }

            int i = Mathf.Clamp(index, 0, values.Count - 1);
            float[][] value = values[i];
            if (value == null)
            {
                return Array.Empty<float[]>();
            }

            float[][] copy = new float[value.Length][];
            for (int j = 0; j < value.Length; j++)
            {
                float[] axis = value[j];
                if (axis == null || axis.Length < 3)
                {
                    copy[j] = NewZeroArray(3);
                    continue;
                }
                copy[j] = new[] { axis[0], axis[1], axis[2] };
            }
            return copy;
        }

        private static float[] NewZeroArray(int length)
        {
            var a = new float[length];
            for (int i = 0; i < length; i++)
            {
                a[i] = 0f;
            }
            return a;
        }

        private static void SortConstraintFramesInPlace(KimodoConstraintJson c)
        {
            if (c == null || c.frame_indices == null || c.frame_indices.Count <= 1)
            {
                return;
            }

            int count = c.frame_indices.Count;
            int[] order = Enumerable.Range(0, count)
                .OrderBy(i => c.frame_indices[i])
                .ThenBy(i => i)
                .ToArray();

            c.frame_indices = Reorder(c.frame_indices, order);
            if (c.smooth_root_2d != null && c.smooth_root_2d.Count == count)
            {
                c.smooth_root_2d = Reorder(c.smooth_root_2d, order);
            }
            if (c.global_root_heading != null && c.global_root_heading.Count == count)
            {
                c.global_root_heading = Reorder(c.global_root_heading, order);
            }
            if (c.root_positions != null && c.root_positions.Count == count)
            {
                c.root_positions = Reorder(c.root_positions, order);
            }
            if (c.local_joints_rot != null && c.local_joints_rot.Count == count)
            {
                c.local_joints_rot = Reorder(c.local_joints_rot, order);
            }
        }

        private static List<T> Reorder<T>(List<T> list, int[] order)
        {
            var result = new List<T>(order.Length);
            for (int i = 0; i < order.Length; i++)
            {
                result.Add(list[order[i]]);
            }
            return result;
        }

        private static SkeletonPose CapturePose(Transform root, Animator animator)
        {
            var pose = new SkeletonPose();
            if (root == null)
            {
                return pose;
            }

            Transform pelvis = TryResolveTransformBySomaName("Hips", root, animator) ?? root;

            Vector3 worldPos = pelvis.position;
            pose.RootPosition = new Vector3(-worldPos.x, worldPos.y, worldPos.z);

            Vector3 forward = pelvis.forward;
            Vector2 heading = new Vector2(-forward.x, forward.z);
            if (heading.sqrMagnitude <= 1e-8f)
            {
                heading = new Vector2(1f, 0f);
            }
            else
            {
                heading.Normalize();
            }
            pose.RootHeading = heading;

            Transform somaRoot = root.Find("SOMA");
            if (somaRoot == null)
            {
                somaRoot = root;
            }

            Transform[] joints = ResolveSoma30JointTransforms(somaRoot, animator);
            Quaternion[] worldRots = new Quaternion[joints.Length];
            for (int i = 0; i < joints.Length; i++)
            {
                worldRots[i] = joints[i] != null ? joints[i].rotation : Quaternion.identity;
            }

            for (int i = 0; i < joints.Length; i++)
            {
                int parent = Soma30Parents[i];
                Quaternion local;
                if (parent >= 0 && parent < worldRots.Length)
                {
                    local = Quaternion.Inverse(worldRots[parent]) * worldRots[i];
                }
                else
                {
                    local = worldRots[i];
                }

                Quaternion q = local;
                q = new Quaternion(q.x, -q.y, -q.z, q.w);
                pose.LocalAxisAngles.Add(QuaternionToAxisAngleVector(q));
            }

            return pose;
        }

        private static Transform[] ResolveSoma30JointTransforms(Transform root, Animator animator)
        {
            var transforms = new Transform[Soma30Names.Length];
            if (root == null)
            {
                return transforms;
            }

            for (int i = 0; i < Soma30Names.Length; i++)
            {
                transforms[i] = TryResolveTransformBySomaName(Soma30Names[i], root, animator) ?? root;
            }
            return transforms;
        }

        private static Transform TryResolveTransformBySomaName(string somaName, Transform searchRoot, Animator animator)
        {
            Transform byHuman = TryResolveViaHumanBone(somaName, animator);
            if (byHuman != null)
            {
                return byHuman;
            }

            return FindTransformByName(searchRoot, somaName);
        }

        private static Transform TryResolveViaHumanBone(string somaName, Animator animator)
        {
            if (animator == null || !animator.isHuman)
            {
                return null;
            }

            bool hasUpperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest) != null;
            switch (somaName)
            {
                case "Hips": return animator.GetBoneTransform(HumanBodyBones.Hips);
                case "Spine1": return animator.GetBoneTransform(HumanBodyBones.Spine);
                case "Spine2": return animator.GetBoneTransform(HumanBodyBones.Chest);
                case "Chest": return hasUpperChest
                    ? animator.GetBoneTransform(HumanBodyBones.UpperChest)
                    : animator.GetBoneTransform(HumanBodyBones.Chest);
                case "Neck1": return animator.GetBoneTransform(HumanBodyBones.Neck);
                case "Neck2": return animator.GetBoneTransform(HumanBodyBones.Neck);
                case "Head": return animator.GetBoneTransform(HumanBodyBones.Head);
                case "Jaw": return animator.GetBoneTransform(HumanBodyBones.Jaw);
                case "LeftEye": return animator.GetBoneTransform(HumanBodyBones.LeftEye);
                case "RightEye": return animator.GetBoneTransform(HumanBodyBones.RightEye);
                case "LeftShoulder": return animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
                case "LeftArm": return animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                case "LeftForeArm": return animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                case "LeftHand": return animator.GetBoneTransform(HumanBodyBones.LeftHand);
                case "LeftHandThumbEnd": return animator.GetBoneTransform(HumanBodyBones.LeftThumbDistal);
                case "LeftHandMiddleEnd": return animator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal);
                case "RightShoulder": return animator.GetBoneTransform(HumanBodyBones.RightShoulder);
                case "RightArm": return animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                case "RightForeArm": return animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                case "RightHand": return animator.GetBoneTransform(HumanBodyBones.RightHand);
                case "RightHandThumbEnd": return animator.GetBoneTransform(HumanBodyBones.RightThumbDistal);
                case "RightHandMiddleEnd": return animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
                case "LeftLeg": return animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                case "LeftShin": return animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                case "LeftFoot": return animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                case "LeftToeBase": return animator.GetBoneTransform(HumanBodyBones.LeftToes);
                case "RightLeg": return animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                case "RightShin": return animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                case "RightFoot": return animator.GetBoneTransform(HumanBodyBones.RightFoot);
                case "RightToeBase": return animator.GetBoneTransform(HumanBodyBones.RightToes);
                default: return null;
            }
        }

        private static Transform FindTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    stack.Push(current.GetChild(i));
                }
            }

            return null;
        }

        private static Vector3 QuaternionToAxisAngleVector(Quaternion q)
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

            float radians = degrees * Mathf.Deg2Rad;
            return axis.normalized * radians;
        }

        private sealed class SkeletonPose
        {
            public Vector3 RootPosition;
            public Vector2 RootHeading;
            public List<Vector3> LocalAxisAngles = new List<Vector3>();
        }
    }
}
