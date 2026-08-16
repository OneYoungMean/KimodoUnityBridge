using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Normalizes protocol constraints that the server cannot represent on the
    /// same frame. A Root2D row is folded into its matching FullBody or
    /// EndEffector row.
    /// </summary>
    public static class KimodoConstraintProtocolNormalizer
    {
        private sealed class Root2DRow
        {
            internal JObject Record;
            internal int RowIndex;
            internal int Frame;
            internal float X;
            internal float Z;
            internal bool HasHeading;
            internal Quaternion Heading;
        }

        /// <summary>
        /// Folds every same-frame Root2D row into FullBody, hand, or foot
        /// records. Root2D retains only frames with no root-context record.
        /// </summary>
        public static JArray NormalizeRoot2DIntoFullBody(JArray constraints)
        {
            if (constraints == null || constraints.Count == 0)
            {
                return constraints ?? new JArray();
            }

            var rootRowsByFrame = new Dictionary<int, List<Root2DRow>>();
            var allRootRows = new List<Root2DRow>();
            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i] is JObject record && IsType(record, "root2d"))
                {
                    CollectRoot2DRows(record, rootRowsByFrame, allRootRows);
                }
            }

            if (rootRowsByFrame.Count == 0)
            {
                return constraints;
            }

            var consumedRootRows = new HashSet<Root2DRow>();
            int mergedFrameCount = 0;
            for (int i = 0; i < constraints.Count; i++)
            {
                if (!(constraints[i] is JObject rootContext) || !IsRootContextType(rootContext))
                {
                    continue;
                }

                mergedFrameCount += MergeRootContextRows(rootContext, rootRowsByFrame, consumedRootRows);
            }

            if (consumedRootRows.Count == 0)
            {
                return constraints;
            }

            var normalized = new JArray();
            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i] is JObject root2D && IsType(root2D, "root2d"))
                {
                    if (!FilterConsumedRoot2DRows(root2D, allRootRows, consumedRootRows))
                    {
                        continue;
                    }
                }

                normalized.Add(constraints[i]);
            }

            Debug.Log($"[Kimodo][ConstraintNormalizer] Folded {mergedFrameCount} same-frame Root2D row(s) into root-context constraints.");
            return normalized;
        }

        private static int MergeRootContextRows(
            JObject rootContext,
            Dictionary<int, List<Root2DRow>> rootRowsByFrame,
            HashSet<Root2DRow> consumedRootRows)
        {
            string contextType = IsType(rootContext, "fullbody") ? "FullBody" : "EndEffector";
            JArray frames = RequireArray(rootContext, "frame_indices", contextType);
            JArray rootPositions = RequireAlignedArray(rootContext, "root_positions", frames.Count, contextType);
            JArray smoothRoot = EnsureSmoothRoot(rootContext, rootPositions, frames.Count, contextType);
            JArray localRotations = rootContext["local_joints_rot"] as JArray;
            if (localRotations != null && localRotations.Count != frames.Count)
            {
                throw new InvalidOperationException($"{contextType} local_joints_rot must align with frame_indices.");
            }

            int merged = 0;
            for (int row = 0; row < frames.Count; row++)
            {
                int frame = ReadFrame(frames[row], "FullBody");
                if (!rootRowsByFrame.TryGetValue(frame, out List<Root2DRow> roots) || roots.Count == 0)
                {
                    continue;
                }

                // This follows the server's last-wins behavior for duplicate
                // Root2D rows, while every conflicting row is removed below.
                Root2DRow root2D = roots[roots.Count - 1];
                WriteRootPosition(rootPositions[row], root2D.X, root2D.Z);
                WriteSmoothRoot(smoothRoot[row], root2D.X, root2D.Z);
                if (root2D.HasHeading)
                {
                    if (localRotations == null)
                    {
                        throw new InvalidOperationException(
                            $"{contextType} frame {frame} cannot absorb Root2D heading without local_joints_rot.");
                    }

                    MergeRootHeading(localRotations[row], root2D.Heading, frame);
                }

                for (int index = 0; index < roots.Count; index++)
                {
                    consumedRootRows.Add(roots[index]);
                }
                merged++;
            }

            return merged;
        }

        private static void CollectRoot2DRows(
            JObject record,
            Dictionary<int, List<Root2DRow>> rootRowsByFrame,
            List<Root2DRow> allRootRows)
        {
            JArray frames = RequireArray(record, "frame_indices", "Root2D");
            JArray positions = RequireAlignedArray(record, "smooth_root_2d", frames.Count, "Root2D");
            JArray headings = record["global_root_heading"] as JArray;
            if (headings != null && headings.Count != frames.Count)
            {
                throw new InvalidOperationException("Root2D global_root_heading must align with frame_indices.");
            }

            for (int row = 0; row < frames.Count; row++)
            {
                ReadVector2(positions[row], "Root2D smooth_root_2d", out float x, out float z);
                var root = new Root2DRow
                {
                    Record = record,
                    RowIndex = row,
                    Frame = ReadFrame(frames[row], "Root2D"),
                    X = x,
                    Z = z
                };
                if (headings != null)
                {
                    root.Heading = ReadUnityHeading(headings[row]);
                    root.HasHeading = true;
                }

                if (!rootRowsByFrame.TryGetValue(root.Frame, out List<Root2DRow> frameRows))
                {
                    frameRows = new List<Root2DRow>();
                    rootRowsByFrame.Add(root.Frame, frameRows);
                }
                frameRows.Add(root);
                allRootRows.Add(root);
            }
        }

        private static bool FilterConsumedRoot2DRows(
            JObject record,
            List<Root2DRow> allRootRows,
            HashSet<Root2DRow> consumedRootRows)
        {
            JArray frames = RequireArray(record, "frame_indices", "Root2D");
            var keep = new List<int>(frames.Count);
            for (int row = 0; row < frames.Count; row++)
            {
                Root2DRow root = FindRoot2DRow(allRootRows, record, row);
                if (root == null || !consumedRootRows.Contains(root))
                {
                    keep.Add(row);
                }
            }

            if (keep.Count == 0)
            {
                return false;
            }
            if (keep.Count == frames.Count)
            {
                return true;
            }

            record["frame_indices"] = FilterRows(frames, keep);
            FilterOptionalAlignedRows(record, "smooth_root_2d", frames.Count, keep);
            FilterOptionalAlignedRows(record, "global_root_heading", frames.Count, keep);
            return true;
        }

        private static void MergeRootHeading(JToken localJointFrame, Quaternion root2DHeading, int frame)
        {
            if (!(localJointFrame is JArray joints) || joints.Count == 0)
            {
                throw new InvalidOperationException($"FullBody frame {frame} has no root local rotation.");
            }

            Vector3 protocolAxisAngle = ReadVector3(joints[0], "FullBody root local rotation");
            Quaternion protocolRoot = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(protocolAxisAngle);
            Quaternion unityRoot = FromKimodoRotation(protocolRoot);
            Quaternion oldYaw = ResolvePlanarRotation(unityRoot);
            Quaternion tilt = Quaternion.Inverse(oldYaw) * unityRoot;
            Quaternion mergedUnityRoot = root2DHeading * tilt;
            Vector3 mergedAxisAngle = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(
                ToKimodoRotation(mergedUnityRoot));
            joints[0] = new JArray(mergedAxisAngle.x, mergedAxisAngle.y, mergedAxisAngle.z);
        }

        private static JArray EnsureSmoothRoot(JObject rootContext, JArray rootPositions, int frameCount, string contextType)
        {
            if (rootContext["smooth_root_2d"] is JArray smoothRoot)
            {
                if (smoothRoot.Count != frameCount)
                {
                    throw new InvalidOperationException($"{contextType} smooth_root_2d must align with frame_indices.");
                }
                return smoothRoot;
            }

            var generated = new JArray();
            for (int row = 0; row < rootPositions.Count; row++)
            {
                Vector3 position = ReadVector3(rootPositions[row], $"{contextType} root_positions");
                generated.Add(new JArray(position.x, position.z));
            }
            rootContext["smooth_root_2d"] = generated;
            return generated;
        }

        private static Quaternion ReadUnityHeading(JToken value)
        {
            ReadVector2(value, "Root2D global_root_heading", out float cosine, out float sine);
            if (new Vector2(cosine, sine).sqrMagnitude <= 1e-8f)
            {
                throw new InvalidOperationException("Root2D global_root_heading cannot be zero.");
            }

            // Exporter encoding is [UnityForward.z, -UnityForward.x].
            float yawDegrees = Mathf.Atan2(-sine, cosine) * Mathf.Rad2Deg;
            return Quaternion.AngleAxis(yawDegrees, Vector3.up);
        }

        private static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private static Quaternion ToKimodoRotation(Quaternion unityRotation)
        {
            return new Quaternion(unityRotation.x, -unityRotation.y, -unityRotation.z, unityRotation.w);
        }

        private static Quaternion FromKimodoRotation(Quaternion kimodoRotation)
        {
            return new Quaternion(kimodoRotation.x, -kimodoRotation.y, -kimodoRotation.z, kimodoRotation.w);
        }

        private static void WriteRootPosition(JToken value, float x, float z)
        {
            if (!(value is JArray position) || position.Count < 3)
            {
                throw new InvalidOperationException("FullBody root_positions entries must have three values.");
            }

            _ = ReadFiniteFloat(position[1], "FullBody root_positions Y");
            position[0] = x;
            position[2] = z;
        }

        private static void WriteSmoothRoot(JToken value, float x, float z)
        {
            if (!(value is JArray position) || position.Count < 2)
            {
                throw new InvalidOperationException("FullBody smooth_root_2d entries must have two values.");
            }

            position[0] = x;
            position[1] = z;
        }

        private static Root2DRow FindRoot2DRow(List<Root2DRow> rows, JObject record, int rowIndex)
        {
            for (int index = 0; index < rows.Count; index++)
            {
                Root2DRow row = rows[index];
                if (ReferenceEquals(row.Record, record) && row.RowIndex == rowIndex)
                {
                    return row;
                }
            }
            return null;
        }

        private static void FilterOptionalAlignedRows(JObject record, string propertyName, int expectedCount, List<int> keep)
        {
            if (!(record[propertyName] is JArray values))
            {
                return;
            }
            if (values.Count != expectedCount)
            {
                throw new InvalidOperationException($"Root2D {propertyName} must align with frame_indices.");
            }
            record[propertyName] = FilterRows(values, keep);
        }

        private static JArray FilterRows(JArray source, List<int> keep)
        {
            var filtered = new JArray();
            for (int index = 0; index < keep.Count; index++)
            {
                filtered.Add(source[keep[index]]);
            }
            return filtered;
        }

        private static JArray RequireArray(JObject record, string propertyName, string type)
        {
            if (!(record[propertyName] is JArray values))
            {
                throw new InvalidOperationException($"{type} requires {propertyName}.");
            }
            return values;
        }

        private static JArray RequireAlignedArray(JObject record, string propertyName, int expectedCount, string type)
        {
            JArray values = RequireArray(record, propertyName, type);
            if (values.Count != expectedCount)
            {
                throw new InvalidOperationException($"{type} {propertyName} must align with frame_indices.");
            }
            return values;
        }

        private static int ReadFrame(JToken value, string type)
        {
            float frame = ReadFiniteFloat(value, $"{type} frame index");
            if (Mathf.Abs(frame - Mathf.Round(frame)) > 1e-4f)
            {
                throw new InvalidOperationException($"{type} frame_indices must contain integers.");
            }
            return Mathf.RoundToInt(frame);
        }

        private static void ReadVector2(JToken value, string name, out float x, out float y)
        {
            if (!(value is JArray values) || values.Count < 2)
            {
                throw new InvalidOperationException($"{name} entries must have two values.");
            }
            x = ReadFiniteFloat(values[0], name);
            y = ReadFiniteFloat(values[1], name);
        }

        private static Vector3 ReadVector3(JToken value, string name)
        {
            if (!(value is JArray values) || values.Count < 3)
            {
                throw new InvalidOperationException($"{name} entries must have three values.");
            }
            return new Vector3(
                ReadFiniteFloat(values[0], name),
                ReadFiniteFloat(values[1], name),
                ReadFiniteFloat(values[2], name));
        }

        private static float ReadFiniteFloat(JToken value, string name)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                throw new InvalidOperationException($"{name} is missing.");
            }
            float result = value.Value<float>();
            if (float.IsNaN(result) || float.IsInfinity(result))
            {
                throw new InvalidOperationException($"{name} must be finite.");
            }
            return result;
        }

        private static bool IsType(JObject record, string type)
        {
            return string.Equals(record.Value<string>("type"), type, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRootContextType(JObject record)
        {
            string type = record?.Value<string>("type") ?? string.Empty;
            switch (type.Trim().ToLowerInvariant())
            {
                case "fullbody":
                case "end-effector":
                case "left-hand":
                case "right-hand":
                case "left-foot":
                case "right-foot":
                    return true;
                default:
                    return false;
            }
        }
    }
}
