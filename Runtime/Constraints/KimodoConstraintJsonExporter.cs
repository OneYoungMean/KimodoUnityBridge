using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KimodoBridge
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

            if (!KimodoSampleResultPoseUtility.TryDecode(sample, out CharacterAnimationCli.Unity.CharacterPose pose, out error))
            {
                return false;
            }
            // The canonical rootTQ channel is not a hips/world transform.
            // Only an explicit world-space Root2D override may provide the
            // protocol root when no skeleton projector is available.
            rootPositionMeters = sample.enableMask?.root2DPosition == true &&
                sample.root2DOverride != null
                ? sample.root2DOverride.t
                : Vector3.zero;
            pose.root.t = Vector3.zero;
            pose.root.q = Quaternion.identity;
            return TryBuildLocalJointAngles(pose, out localAngles, out error);
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
                KimodoConstraintSampleComposer.ExpandProtocolSamples(samples, DefaultExportFps),
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
                KimodoConstraintInternal[] internals = KimodoConstraintInternal.GetConstraintInternal(
                    sample,
                    KimodoConstraintRigType.Unknown,
                    exportContext);
                for (int internalIndex = 0; internalIndex < internals.Length; internalIndex++)
                {
                    KimodoConstraintJson json = internals[internalIndex].ToJsonObject(
                        clipStartSeconds, clipDurationSeconds, exportFps);
                    if (json != null) output.Add(json);
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

            KimodoConstraintInternal[] internals = KimodoConstraintInternal.GetConstraintInternal(
                sample,
                KimodoConstraintRigType.Unknown,
                exportContext);
            return internals.Length == 0
                ? null
                : internals[0].ToJsonObject(clipStartSeconds, clipDurationSeconds, exportFps);
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
                KimodoConstraintSampleComposer.ExpandProtocolSamples(samples, exportFps),
                exportContext,
                clipStartSeconds,
                clipDurationSeconds,
                exportFps);
            return mergeByType ? MergeConstraintsByType(constraints) : constraints;
        }

        internal static KimodoConstraintJson BuildRoot2DInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            if (sample != null && sample.enableMask?.root2DPosition == true &&
                sample.root2DOverride != null)
            {
                _ = exportContext ?? throw new ArgumentNullException(nameof(exportContext));
                Vector3 root = sample.root2DOverride.t;
                Vector3 forward = sample.root2DOverride.q * Vector3.forward;
                var canonical = new KimodoConstraintJson
                {
                    type = "root2d",
                    frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                    smooth_root_2d = new List<float[]> { new[] { -root.x, root.z } }
                };
                if (sample.enableMask?.root2DHeading == true)
                {
                    canonical.global_root_heading = new List<float[]> { new[] { forward.z, -forward.x } };
                }
                return canonical;
            }

            throw new InvalidOperationException("Root2D world override is invalid.");
        }

        internal static KimodoConstraintJson BuildFullBodyInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            Vector3 rootPositionMeters;
            List<Vector3> localAxisAngles;
            string error;
            if (!TryBuildProjectedProtocolPose(
                    sample,
                    exportContext,
                    out rootPositionMeters,
                    out localAxisAngles,
                    out error))
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

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static KimodoConstraintJson BuildEndEffectorInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintExportContext exportContext,
            string jointType,
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            KimodoConstraintProjectedPose projected = exportContext?.projectedPoseProjector != null
                ? exportContext.projectedPoseProjector(sample)
                : null;
            Vector3 rootPositionMeters;
            List<Vector3> localAxisAngles;
            string error;
            if (projected != null && projected.localJointAngles != null && projected.localJointAngles.Count > 0)
            {
                rootPositionMeters = projected.rootPositionMeters;
                localAxisAngles = projected.localJointAngles;
                error = string.Empty;
            }
            else if (!TryBuildProjectedProtocolPose(sample, exportContext, out rootPositionMeters, out localAxisAngles, out error))
            {
                throw new InvalidOperationException($"End-effector constraint pose projection failed: {error}");
            }
            _ = exportContext ?? throw new ArgumentNullException(nameof(exportContext));
            Vector3 kimodoRoot = new Vector3(-rootPositionMeters.x, rootPositionMeters.y, rootPositionMeters.z);
            var json = new KimodoConstraintJson
            {
                type = jointType,
                frame_indices = BuildFrameIndices(sample.sampleTime - clipStartSeconds, clipDurationSeconds, exportFps),
                joint_names = new List<string> { ResolveEndEffectorJointName(jointType) },
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

            CharacterAnimationCli.Unity.KimodoRigidTransform goal = ResolveEndEffectorGoal(
                sample,
                jointType);
            if (goal != null)
            {
                Vector3 worldTarget = goal.t;
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

        private static CharacterAnimationCli.Unity.KimodoRigidTransform ResolveEndEffectorGoal(
            KimodoMarkerSampleResult sample,
            string type)
        {
            if (sample?.effectors == null) return null;
            switch ((type ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-'))
            {
                case "left-hand": return sample.effectors.leftHand;
                case "right-hand": return sample.effectors.rightHand;
                case "left-foot": return sample.effectors.leftFoot;
                case "right-foot": return sample.effectors.rightFoot;
                default: return null;
            }
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
            if (!KimodoSampleResultPoseUtility.TryDecode(sample, out _, out error))
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
