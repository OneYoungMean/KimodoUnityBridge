using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TimelineInject
{
    public static class KimodoFrameTimeUtility
    {
        public const double FrameTolerance = 1e-4;

        public static int SecondsToFrameCount(double seconds, double frameRate)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) ||
                double.IsNaN(frameRate) || double.IsInfinity(frameRate) ||
                seconds <= 0.0 || frameRate <= 0.0)
            {
                return 0;
            }

            double frames = Math.Ceiling(seconds * frameRate - FrameTolerance);
            return frames >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)frames);
        }

        public static int SecondsToFrameIndex(double seconds, double frameRate)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) ||
                double.IsNaN(frameRate) || double.IsInfinity(frameRate) ||
                seconds <= 0.0 || frameRate <= 0.0)
            {
                return 0;
            }

            double tolerance = Math.Max(Math.Abs(seconds), 1.0) * frameRate * 1e-14;
            double frame = Math.Floor(seconds * frameRate + tolerance);
            return frame >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)frame);
        }
    }

    public static class KimodoConstraintJsonExporter
    {
        private const double DefaultExportFps = 30.0;

        public static string ToConstraintsJson(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double clipStartSeconds = 0.0,
            double? clipDurationSeconds = null,
            double exportFps = DefaultExportFps,
            bool denseRootPath = false)
        {
            List<KimodoConstraintJson> constraints = BuildConstraints(
                samples,
                mergeByType: true,
                clipStartSeconds: clipStartSeconds,
                clipDurationSeconds: clipDurationSeconds,
                exportFps: exportFps);
            if (denseRootPath)
            {
                for (int i = 0; i < constraints.Count; i++)
                {
                    if (string.Equals(constraints[i].type, "root2d", StringComparison.OrdinalIgnoreCase))
                    {
                        constraints[i].dense_path = true;
                    }
                }
            }
            if (constraints.Count == 0)
            {
                return string.Empty;
            }

            return JsonConvert.SerializeObject(
                constraints,
                Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        public static List<KimodoConstraintJson> BuildConstraints(IReadOnlyList<KimodoMarkerSampleResult> samples)
        {
            return BuildConstraints(samples, 0.0, null, DefaultExportFps);
        }

        private static List<KimodoConstraintJson> BuildConstraints(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            var output = new List<KimodoConstraintJson>();
            if (samples == null)
            {
                return output;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                KimodoConstraintJson json = BuildConstraint(sample, clipStartSeconds, clipDurationSeconds, exportFps);
                if (json != null)
                {
                    output.Add(json);
                }
            }

            return output;
        }

        public static KimodoConstraintJson BuildConstraint(KimodoMarkerSampleResult sample)
        {
            return BuildConstraint(sample, 0.0, null, DefaultExportFps);
        }

        public static KimodoConstraintJson BuildConstraint(
            KimodoMarkerSampleResult sample,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps = DefaultExportFps)
        {
            if (sample == null)
            {
                return null;
            }

            string type = sample.constraintType ?? string.Empty;
            if (string.IsNullOrWhiteSpace(type))
            {
                return null;
            }

            if (string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRoot2D(sample, clipStartSeconds, clipDurationSeconds, exportFps);
            }

            if (string.Equals(type, "root2d_target", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRoot2DTarget(sample);
            }

            if (string.Equals(type, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                return BuildFullBody(sample, clipStartSeconds, clipDurationSeconds, exportFps);
            }

            return BuildEndEffector(sample, clipStartSeconds, clipDurationSeconds, exportFps);
        }

        public static List<KimodoConstraintJson> BuildConstraints(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            bool mergeByType,
            double clipStartSeconds = 0.0,
            double? clipDurationSeconds = null,
            double exportFps = DefaultExportFps)
        {
            List<KimodoConstraintJson> constraints = BuildConstraints(samples, clipStartSeconds, clipDurationSeconds, exportFps);
            return mergeByType ? MergeConstraintsByType(constraints) : constraints;
        }

        private static KimodoConstraintJson BuildRoot2D(
            KimodoMarkerSampleResult sample,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            var json = new KimodoConstraintJson
            {
                type = "root2d",
                frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                smooth_root_2d = new List<float[]>
                {
                    new[] { -sample.kimodoRootPosition.x, sample.kimodoRootPosition.z }
                }
            };

            if (sample.hasRootHeading)
            {
                json.global_root_heading = new List<float[]>
                {
                    new[] { -sample.rootHeading.x, sample.rootHeading.y }
                };
            }

            return json;
        }

        private static KimodoConstraintJson BuildRoot2DTarget(KimodoMarkerSampleResult sample)
        {
            return new KimodoConstraintJson
            {
                type = "root2d_target",
                frame_indices = null,
                target_root_2d = new[] { -sample.kimodoRootPosition.x, sample.kimodoRootPosition.z },
                max_speed = Mathf.Max(0.01f, sample.rootTargetMaxSpeed),
                max_acceleration = Mathf.Max(0.01f, sample.rootTargetMaxAcceleration),
                arrival_threshold = Mathf.Max(0f, sample.rootTargetArrivalThreshold),
                include_heading = sample.rootTargetIncludeHeading
            };
        }

        private static KimodoConstraintJson BuildFullBody(
            KimodoMarkerSampleResult sample,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            Vector3 kimodoRoot = new Vector3(-sample.kimodoRootPosition.x, sample.kimodoRootPosition.y, sample.kimodoRootPosition.z);
            var json = new KimodoConstraintJson
            {
                type = "fullbody",
                frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                smooth_root_2d = new List<float[]>
                {
                    new[] { kimodoRoot.x, kimodoRoot.z }
                },
                root_positions = new List<float[]>
                {
                    new[] { kimodoRoot.x, kimodoRoot.y, kimodoRoot.z }
                },
                local_joints_rot = new List<float[][]>
                {
                    BuildLocalJointFrame(sample.localAxisAngles)
                }
            };

            return json;
        }

        private static float[] BuildEndEffectorTargetPosition(KimodoMarkerSampleResult sample)
        {
            Quaternion rootRotation = sample.localAxisAngles != null && sample.localAxisAngles.Count > 0
                ? AxisAngleToQuaternion(sample.localAxisAngles[0])
                : Quaternion.identity;
            Vector3 target = sample.kimodoRootPosition +
                rootRotation * sample.endEffectorTargetPositionRootLocal;
            return new[] { -target.x, target.y, target.z };
        }

        private static KimodoConstraintJson BuildEndEffector(
            KimodoMarkerSampleResult sample,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            Vector3 kimodoRoot = new Vector3(-sample.kimodoRootPosition.x, sample.kimodoRootPosition.y, sample.kimodoRootPosition.z);
            var json = new KimodoConstraintJson
            {
                type = sample.constraintType,
                frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                joint_names = sample.jointNames != null ? new List<string>(sample.jointNames) : new List<string>(),
                smooth_root_2d = new List<float[]>
                {
                    new[] { kimodoRoot.x, kimodoRoot.z }
                },
                root_positions = new List<float[]>
                {
                    new[] { kimodoRoot.x, kimodoRoot.y, kimodoRoot.z }
                },
                local_joints_rot = new List<float[][]>
                {
                    BuildLocalJointFrame(sample.localAxisAngles)
                }
            };

            if (sample.hasEndEffectorTargetPosition)
            {
                json.target_positions = new List<float[]>
                {
                    BuildEndEffectorTargetPosition(sample)
                };
            }

            return json;
        }

        private static List<int> BuildFrameIndices(double sampleTime, double? clipDurationSeconds, double exportFps)
        {
            return new List<int> { ToFrameIndex(sampleTime, clipDurationSeconds, exportFps) };
        }

        private static int ToFrameIndex(double sampleTime, double? clipDurationSeconds, double exportFps)
        {
            double fps = exportFps > 0.0 ? exportFps : DefaultExportFps;
            int frame = KimodoFrameTimeUtility.SecondsToFrameIndex(sampleTime, fps);
            if (clipDurationSeconds.HasValue)
            {
                int maxFrame = Mathf.Max(
                    0,
                    KimodoFrameTimeUtility.SecondsToFrameCount(clipDurationSeconds.Value, fps) - 1);
                frame = Mathf.Clamp(frame, 0, maxFrame);
            }

            return frame;
        }

        private static float[][] BuildLocalJointFrame(List<Vector3> joints)
        {
            if (joints == null || joints.Count == 0)
            {
                return Array.Empty<float[]>();
            }

            float[][] data = new float[joints.Count][];
            for (int i = 0; i < joints.Count; i++)
            {
                Vector3 v = ToKimodoAxisAngle(joints[i]);
                data[i] = new[] { v.x, v.y, v.z };
            }

            return data;
        }

        private static Vector3 ToKimodoAxisAngle(Vector3 unityAxisAngle)
        {
            float angleRad = unityAxisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Vector3.zero;
            }

            Vector3 axis = unityAxisAngle / angleRad;
            Quaternion unityLocal = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
            Quaternion kimodoLocal = new Quaternion(unityLocal.x, -unityLocal.y, -unityLocal.z, unityLocal.w);
            return QuaternionToAxisAngleVector(kimodoLocal);
        }

        private static Quaternion AxisAngleToQuaternion(Vector3 axisAngle)
        {
            float radians = axisAngle.magnitude;
            return radians <= 1e-8f
                ? Quaternion.identity
                : Quaternion.AngleAxis(radians * Mathf.Rad2Deg, axisAngle / radians);
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

                output.Add(BuildMergedConstraint(type, group));
            }

            return output;
        }

        private static KimodoConstraintJson BuildMergedConstraint(string type, List<KimodoConstraintJson> group)
        {
            if (string.Equals(type, "root2d_target", StringComparison.OrdinalIgnoreCase))
            {
                return group[group.Count - 1];
            }

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
            bool root2DHasCompleteHeading = true;

            if (isRoot2D)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    KimodoConstraintJson item = group[i];
                    int frameCount = item != null && item.frame_indices != null ? item.frame_indices.Count : 0;
                    int headingCount = item != null && item.global_root_heading != null ? item.global_root_heading.Count : 0;
                    if (frameCount > 0 && headingCount != frameCount)
                    {
                        root2DHasCompleteHeading = false;
                        break;
                    }
                }
            }

            if (isRoot2D || isFullBody || isEndEffectorFamily)
            {
                merged.smooth_root_2d = new List<float[]>();
            }
            if (isFullBody || isEndEffectorFamily)
            {
                merged.root_positions = new List<float[]>();
                merged.local_joints_rot = new List<float[][]>();
            }
            if (isRoot2D && root2DHasCompleteHeading)
            {
                merged.global_root_heading = new List<float[]>();
            }

            if (isEndEffectorFamily && group[0].joint_names != null && group[0].joint_names.Count > 0)
            {
                merged.joint_names = new List<string>(group[0].joint_names);
            }
            if (isEndEffectorFamily && group.Exists(c => c?.target_positions != null && c.target_positions.Count > 0))
            {
                merged.target_positions = new List<float[]>();
            }

            for (int i = 0; i < group.Count; i++)
            {
                KimodoConstraintJson c = group[i];
                if (c.frame_indices == null || c.frame_indices.Count == 0)
                {
                    continue;
                }

                merged.frame_indices.AddRange(c.frame_indices);
                if (merged.smooth_root_2d != null && c.smooth_root_2d != null)
                {
                    merged.smooth_root_2d.AddRange(c.smooth_root_2d);
                }
                if (merged.root_positions != null && c.root_positions != null)
                {
                    merged.root_positions.AddRange(c.root_positions);
                }
                if (merged.local_joints_rot != null && c.local_joints_rot != null)
                {
                    merged.local_joints_rot.AddRange(c.local_joints_rot);
                }
                if (merged.target_positions != null)
                {
                    for (int frame = 0; frame < c.frame_indices.Count; frame++)
                    {
                        merged.target_positions.Add(
                            c.target_positions != null && frame < c.target_positions.Count
                                ? c.target_positions[frame]
                                : null);
                    }
                }
                if (merged.global_root_heading != null && c.global_root_heading != null)
                {
                    merged.global_root_heading.AddRange(c.global_root_heading);
                }
            }

            return merged;
        }
    }
}
