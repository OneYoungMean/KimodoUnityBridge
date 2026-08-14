using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
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

        public static int SecondsToProtocolFrameIndex(double seconds, double frameRate)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) ||
                double.IsNaN(frameRate) || double.IsInfinity(frameRate) || frameRate <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds));
            }
            double frame = Math.Ceiling(seconds * frameRate - FrameTolerance);
            if (frame >= int.MaxValue) return int.MaxValue;
            if (frame <= int.MinValue) return int.MinValue;
            return (int)frame;
        }
    }

    public static class KimodoConstraintRotationUtility
    {
        public static Quaternion AxisAngleVectorToQuaternion(Vector3 axisAngle)
        {
            float radians = axisAngle.magnitude;
            return radians <= 1e-8f
                ? Quaternion.identity
                : Quaternion.AngleAxis(radians * Mathf.Rad2Deg, axisAngle / radians);
        }

        public static Vector3 QuaternionToAxisAngleVector(Quaternion rotation)
        {
            rotation.Normalize();
            rotation.ToAngleAxis(out float degrees, out Vector3 axis);
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
    }

    /// <summary>Result of projecting a canonical pose through the profile
    /// humanoid. RootPositionMeters is the position of the profile Hips joint
    /// after the muscle clip has been evaluated.</summary>
    public sealed class KimodoConstraintProjectedPose
    {
        public Vector3 rootPositionMeters;
        public List<Vector3> localJointAngles;
    }

    /// <summary>Avatar/retarget data used only while projecting canonical
    /// normalized CharacterPose values into metre-based protocol positions.</summary>
    public sealed class KimodoConstraintExportContext
    {
        public float humanScale = 1f;
        public Func<CharacterAnimationCli.Unity.CharacterPose, List<Vector3>> localJointAngleProjector;
        public Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> projectedPoseProjector;

        public KimodoConstraintExportContext() { }
        public KimodoConstraintExportContext(float humanScale,
            Func<CharacterAnimationCli.Unity.CharacterPose, List<Vector3>> localJointAngleProjector = null)
        {
            this.humanScale = Mathf.Max(1e-6f, humanScale);
            this.localJointAngleProjector = localJointAngleProjector;
        }

        public KimodoConstraintExportContext(float humanScale,
            Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> projectedPoseProjector)
        {
            this.humanScale = Mathf.Max(1e-6f, humanScale);
            this.projectedPoseProjector = projectedPoseProjector;
        }
        internal float HumanScale => Mathf.Max(1e-6f, humanScale);

        internal bool TryBuildProjectedPose(
            KimodoMarkerSampleResult sample,
            out Vector3 rootPositionMeters,
            out List<Vector3> localAngles,
            out string error)
        {
            rootPositionMeters = Vector3.zero;
            localAngles = null;
            error = string.Empty;

            if (projectedPoseProjector != null)
            {
                KimodoConstraintProjectedPose projected = projectedPoseProjector(sample);
                if (projected == null || projected.localJointAngles == null || projected.localJointAngles.Count == 0)
                {
                    error = "Model skeleton projector returned no projected pose.";
                    return false;
                }

                rootPositionMeters = projected.rootPositionMeters;
                localAngles = projected.localJointAngles;
                return true;
            }

            rootPositionMeters = sample.characterPose.root.t * HumanScale;
            return TryBuildLocalJointAngles(sample.characterPose, out localAngles, out error);
        }

        internal bool TryBuildLocalJointAngles(CharacterAnimationCli.Unity.CharacterPose pose, out List<Vector3> localAngles, out string error)
        {
            localAngles = null;
            error = string.Empty;
            if (localJointAngleProjector == null)
            {
                localAngles = new List<Vector3>
                {
                    KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(pose.root.q)
                };
                return true;
            }
            localAngles = localJointAngleProjector(pose);
            if (localAngles == null || localAngles.Count == 0)
            {
                error = "Model skeleton projector returned no joints.";
                return false;
            }
            return true;
        }
    }

    public static class KimodoConstraintJsonExporter
    {
        private const double DefaultExportFps = 30.0;

        public static string ToConstraintsJson(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            KimodoConstraintExportContext exportContext,
            double clipStartSeconds = 0.0,
            double? clipDurationSeconds = null,
            double exportFps = DefaultExportFps,
            bool denseRootPath = false)
        {
            List<KimodoConstraintJson> constraints = BuildConstraints(
                samples,
                exportContext,
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

        public static List<KimodoConstraintJson> BuildConstraints(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            KimodoConstraintExportContext exportContext)
        {
            return BuildConstraints(
                KimodoConstraintSampleResolver.ExpandProtocolSamples(samples, DefaultExportFps),
                exportContext ?? throw new ArgumentNullException(nameof(exportContext)),
                0.0,
                null,
                DefaultExportFps);
        }

        private static List<KimodoConstraintJson> BuildConstraints(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            KimodoConstraintExportContext exportContext,
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
                KimodoConstraintJson json = BuildConstraint(sample, exportContext, clipStartSeconds, clipDurationSeconds, exportFps);
                if (json != null)
                {
                    output.Add(json);
                }
            }

            return output;
        }

        public static KimodoConstraintJson BuildConstraint(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
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

            if (string.Equals(type, "constraint", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRoot2D(sample, exportContext, clipStartSeconds, clipDurationSeconds, exportFps);
            }

            if (string.Equals(type, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                return BuildFullBody(sample, exportContext, clipStartSeconds, clipDurationSeconds, exportFps);
            }

            return BuildEndEffector(sample, exportContext, clipStartSeconds, clipDurationSeconds, exportFps);
        }

        public static List<KimodoConstraintJson> BuildConstraints(
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            KimodoConstraintExportContext exportContext,
            bool mergeByType,
            double clipStartSeconds = 0.0,
            double? clipDurationSeconds = null,
            double exportFps = DefaultExportFps)
        {
            List<KimodoConstraintJson> constraints = BuildConstraints(
                KimodoConstraintSampleResolver.ExpandProtocolSamples(samples, exportFps),
                exportContext,
                clipStartSeconds,
                clipDurationSeconds,
                exportFps);
            return mergeByType ? MergeConstraintsByType(constraints) : constraints;
        }

        private static KimodoConstraintJson BuildRoot2D(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            if (sample.characterPose != null && sample.characterPose.TryValidate(out _))
            {
                Vector3 root = sample.characterPose.root.t * ((exportContext ?? throw new ArgumentNullException(nameof(exportContext))).HumanScale);
                Vector3 forward = sample.characterPose.root.q * Vector3.forward;
                var canonical = new KimodoConstraintJson
                {
                    type = "root2d",
                    frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                    smooth_root_2d = new List<float[]> { new[] { -root.x, root.z } }
                };
                if (sample.hasRootHeading)
                {
                    canonical.global_root_heading = new List<float[]> { new[] { forward.z, -forward.x } };
                }
                return canonical;
            }

            var json = new KimodoConstraintJson
            {
                type = "root2d",
                frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                smooth_root_2d = new List<float[]>
                {
                    new[] { -sample.characterPose.root.t.x, sample.characterPose.root.t.z }
                }
            };

            if (sample.hasRootHeading)
            {
                json.global_root_heading = new List<float[]>
                {
                    new[] { new Vector2((sample.characterPose.root.q * Vector3.forward).x, (sample.characterPose.root.q * Vector3.forward).z).y, -new Vector2((sample.characterPose.root.q * Vector3.forward).x, (sample.characterPose.root.q * Vector3.forward).z).x }
                };
            }

            return json;
        }

        private static KimodoConstraintJson BuildFullBody(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            if (!TryBuildProjectedProtocolPose(sample, exportContext, out Vector3 rootPositionMeters, out List<Vector3> localAxisAngles, out string error))
            {
                throw new InvalidOperationException($"FullBody constraint pose projection failed: {error}");
            }
            Vector3 kimodoRoot = new Vector3(-rootPositionMeters.x, rootPositionMeters.y, rootPositionMeters.z);
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
                    BuildLocalJointFrame(localAxisAngles)
                }
            };

            return json;
        }

        private static KimodoConstraintJson BuildEndEffector(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            if (!TryBuildProjectedProtocolPose(sample, exportContext, out Vector3 rootPositionMeters, out List<Vector3> localAxisAngles, out string error))
            {
                throw new InvalidOperationException($"End-effector constraint pose projection failed: {error}");
            }
            float humanScale = (exportContext ?? throw new ArgumentNullException(nameof(exportContext))).HumanScale;
            Vector3 kimodoRoot = new Vector3(-rootPositionMeters.x, rootPositionMeters.y, rootPositionMeters.z);
            var json = new KimodoConstraintJson
            {
                type = sample.constraintType,
                frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                joint_names = new List<string> { ResolveEndEffectorJointName(sample.constraintType) },
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
                    BuildLocalJointFrame(localAxisAngles)
                }
            };

            CharacterAnimationCli.Unity.CharacterPoseTransform goal = ResolveEndEffectorGoal(sample.characterPose, sample.constraintType);
            if (goal != null)
            {
                // Hand/Foot T/Q use Unity's HumanPose body-relative IK-goal
                // convention. Unity stores body position and IK goals in units
                // normalized by humanScale; model protocol space uses metres.
                Vector3 worldTarget = (sample.characterPose.root.t + sample.characterPose.root.q * goal.t) * humanScale;
                json.target_positions = new List<float[]> { new[] { -worldTarget.x, worldTarget.y, worldTarget.z } };
            }

            return json;
        }

        private static string ResolveEndEffectorJointName(string type)
        {
            switch ((type ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-'))
            {
                case "left-hand": return "LeftHand";
                case "right-hand": return "RightHand";
                case "left-foot": return "LeftFoot";
                case "right-foot": return "RightFoot";
                default: return "";
            }
        }

        private static CharacterAnimationCli.Unity.CharacterPoseTransform ResolveEndEffectorGoal(CharacterAnimationCli.Unity.CharacterPose pose, string type)
        {
            if (pose == null) return null;
            switch ((type ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-'))
            {
                case "left-hand": return pose.hands?.left;
                case "right-hand": return pose.hands?.right;
                case "left-foot": return pose.feet?.left;
                case "right-foot": return pose.feet?.right;
                default: return null;
            }
        }

        private static bool TryBuildProtocolPose(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            out Vector3 root,
            out List<Vector3> localAxisAngles,
            out string error)
        {
            root = Vector3.zero;
            localAxisAngles = null;
            error = string.Empty;
            if (sample?.characterPose == null || !sample.characterPose.TryValidate(out error))
            {
                return false;
            }

            // The server's first FK joint is the canonical HumanPose body root.
            // All authoring data remains in CharacterPose; no historical joint
            // arrays are retained on the constraint DTO.
            root = sample.characterPose.root.t;
            return exportContext != null && exportContext.TryBuildLocalJointAngles(
                sample.characterPose,
                out localAxisAngles,
                out error);
        }

        private static bool TryBuildProjectedProtocolPose(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            out Vector3 rootPositionMeters,
            out List<Vector3> localAxisAngles,
            out string error)
        {
            rootPositionMeters = Vector3.zero;
            localAxisAngles = null;
            error = string.Empty;
            if (sample?.characterPose == null || !sample.characterPose.TryValidate(out error))
            {
                return false;
            }

            return exportContext != null && exportContext.TryBuildProjectedPose(
                sample,
                out rootPositionMeters,
                out localAxisAngles,
                out error);
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
            Quaternion unityLocal = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(unityAxisAngle);
            Quaternion kimodoLocal = new Quaternion(unityLocal.x, -unityLocal.y, -unityLocal.z, unityLocal.w);
            return KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(kimodoLocal);
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

                group = group.OrderBy(item =>
                    item.frame_indices != null && item.frame_indices.Count > 0
                        ? item.frame_indices[0]
                        : int.MaxValue).ToList();

                output.Add(BuildMergedConstraint(type, group));
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
            bool hasAnyTargetPositions = isEndEffectorFamily && group.Any(item => item?.target_positions != null);
            if (hasAnyTargetPositions)
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
                if (merged.global_root_heading != null && c.global_root_heading != null)
                {
                    merged.global_root_heading.AddRange(c.global_root_heading);
                }
                if (merged.target_positions != null)
                {
                    int frameCount = c.frame_indices != null ? c.frame_indices.Count : 0;
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        merged.target_positions.Add(c.target_positions != null && frame < c.target_positions.Count
                            ? c.target_positions[frame]
                            : null);
                    }
                }
            }

            return merged;
        }
    }
}
