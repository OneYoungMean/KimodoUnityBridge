using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KimodoBridge.Editor
{
    /// <summary>
    /// Re-anchors deferred In/Out raw constraints after the first trajectory pass.
    /// Raw constraints are already in the SOMA protocol space; only their planar
    /// root transform is changed, while Y, pitch/roll and local body rotations remain intact.
    /// </summary>
    internal static class KimodoPostApplyOutConstraintUtility
    {
        private sealed class RootRow
        {
            public JObject Record;
            public int RecordIndex;
            public int RowIndex;
            public int Frame;
            public Vector3 Position;
            public Quaternion Heading;
            public bool HasHeading;
        }

        internal static string ApplyInOut(
            string existingJson,
            string deferredInJson,
            string deferredOutJson,
            KimodoRawMotionData firstPassMotion,
            string modelName,
            float frameRate,
            int runtimeTrimStartFrame)
        {
            JArray existing = ParseArray(existingJson);
            JArray deferredIn = ParseArray(deferredInJson);
            JArray deferredOut = ParseArray(deferredOutJson);
            List<RootRow> inRows = ReadRows(deferredIn);
            List<RootRow> outRows = ReadRows(deferredOut);
            if (inRows.Count == 0 && outRows.Count == 0)
                return existing.ToString(Formatting.None);

            PlanarTransform inTransform = ResolveTransform(inRows, firstPassMotion, modelName, runtimeTrimStartFrame);
            PlanarTransform outTransform = ResolveTransform(outRows, firstPassMotion, modelName, runtimeTrimStartFrame);
            JArray filtered = new JArray();
            var deferredFrames = new HashSet<int>(inRows.Concat(outRows).Select(row => row.Frame));
            foreach (JObject record in existing.OfType<JObject>())
            {
                if (!FilterRecordOutsideFrames(record, deferredFrames)) continue;
                filtered.Add(record);
            }
            AppendTransformed(deferredIn, inTransform, filtered);
            AppendTransformed(
                deferredOut,
                outTransform,
                filtered,
                inRows.Count > 0 ? new HashSet<int>(inRows.Select(row => row.Frame)) : null);

            string result = filtered.ToString(Formatting.None);
            if (!TryValidateRawConstraintRootMotion(result, frameRate, out string error))
            {
                throw new InvalidOperationException($"Postapply In/Out constraints violate root motion limits: {error}");
            }
            return result;
        }

        internal static bool TryValidateRawConstraintRootMotion(
            string rawConstraintJson,
            float frameRate,
            out string error)
        {
            return TryValidateRootRows(ReadRows(ParseArray(rawConstraintJson)), frameRate, out error);
        }

        private static bool TryValidateRootRows(List<RootRow> rows, float frameRate, out string error)
        {
            error = string.Empty;
            RootRow previous = null;
            RootRow beforePrevious = null;
            IEnumerable<RootRow> orderedRows = rows
                .OrderBy(row => row.Frame)
                .ThenBy(row => row.RecordIndex)
                .ThenBy(row => row.RowIndex)
                .GroupBy(row => row.Frame)
                .Select(group => group.Last());
            foreach (RootRow current in orderedRows)
            {
                if (previous == null)
                {
                    previous = current;
                    continue;
                }
                int frameDelta = current.Frame - previous.Frame;
                float dt = frameDelta / Mathf.Max(1f, frameRate);
                Vector3 velocity = (current.Position - previous.Position) / dt;
                float speed = velocity.magnitude;
                if (speed > KimodoGenerationWarningCheckService.MaxSpeedMetersPerSecond)
                {
                    error = $"Raw constraint root speed {speed:F3} m/s exceeds {KimodoGenerationWarningCheckService.MaxSpeedMetersPerSecond:F1} m/s.";
                    return false;
                }
                if (beforePrevious != null)
                {
                    float previousDt = (previous.Frame - beforePrevious.Frame) / Mathf.Max(1f, frameRate);
                    if (previousDt > 0f)
                    {
                        Vector3 previousVelocity = (previous.Position - beforePrevious.Position) / previousDt;
                        // The two velocities are centered at different times. Use the
                        // non-uniform three-point Lagrange second derivative instead of
                        // dividing their difference by only the latest interval.
                        float centerDistance = 0.5f * (previousDt + dt);
                        float acceleration = (velocity - previousVelocity).magnitude / centerDistance;
                        if (acceleration > KimodoGenerationWarningCheckService.MaxAccelerationMetersPerSecondSquared)
                        {
                            error = $"Raw constraint root acceleration {acceleration:F3} m/s² exceeds {KimodoGenerationWarningCheckService.MaxAccelerationMetersPerSecondSquared:F1} m/s².";
                            return false;
                        }
                    }
                }
                beforePrevious = previous;
                previous = current;
            }
            return true;
        }

        private static PlanarTransform ResolveTransform(
            List<RootRow> deferredRows,
            KimodoRawMotionData firstPassMotion,
            string modelName,
            int runtimeTrimStartFrame)
        {
            if (deferredRows == null || deferredRows.Count == 0 || firstPassMotion == null)
                return PlanarTransform.Identity;

            int anchorFrame = ResolveDeferredWindowAnchorFrame(deferredRows.Select(row => row.Frame).ToArray());
            RootRow anchor = deferredRows
                .Where(row => row.Frame == anchorFrame)
                .OrderBy(row => row.RecordIndex)
                .ThenBy(row => row.RowIndex)
                .First();
            int outputFrame = Mathf.Clamp(
                anchor.Frame - Mathf.Max(0, runtimeTrimStartFrame),
                0,
                Mathf.Max(0, firstPassMotion.FrameCount - 1));
            if (!TryReadMotionRoot(firstPassMotion, modelName, outputFrame,
                    out Vector3 generatedPosition, out Quaternion generatedHeading))
                return PlanarTransform.Identity;

            Quaternion sourceHeading = anchor.HasHeading ? anchor.Heading : generatedHeading;
            return PlanarTransform.FromSourceToTarget(
                anchor.Position, sourceHeading, generatedPosition, generatedHeading);
        }

        internal static int ResolveDeferredWindowAnchorFrame(IReadOnlyList<int> sampleFrames)
        {
            if (sampleFrames == null || sampleFrames.Count == 0)
                throw new ArgumentException();
            return sampleFrames.Min();
        }

        private static void AppendTransformed(
            JArray source,
            PlanarTransform transform,
            JArray destination,
            ISet<int> excludedFrames = null)
        {
            foreach (JObject record in source.OfType<JObject>())
            {
                if (excludedFrames != null &&
                    !FilterRecordFrames(record, frame => !excludedFrames.Contains(frame)))
                {
                    continue;
                }
                TransformRecord(record, transform);
                destination.Add(record);
            }
        }

        private static JArray ParseArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JArray();
            JToken parsed = JToken.Parse(json);
            return parsed as JArray ?? throw new InvalidOperationException("Constraint payload must be a JSON array.");
        }

        private static List<RootRow> ReadRows(JArray records)
        {
            var rows = new List<RootRow>();
            for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                JObject record = records[recordIndex] as JObject;
                if (record == null || record["frame_indices"] is not JArray frames) continue;
                string type = record.Value<string>("type")?.Trim().ToLowerInvariant();
                JArray positions = type == "root2d"
                    ? record["smooth_root_2d"] as JArray
                    : record["root_positions"] as JArray ?? record["smooth_root_2d"] as JArray;
                if (positions == null) continue;
                JArray headings = record["global_root_heading"] as JArray;
                for (int row = 0; row < frames.Count && row < positions.Count; row++)
                {
                    if (!TryReadPosition(positions[row], type == "root2d", out Vector3 position)) continue;
                    Quaternion heading = Quaternion.identity;
                    bool hasHeading = headings != null && row < headings.Count && TryReadHeading(headings[row], out heading);
                    if (!hasHeading && record["local_joints_rot"]?[row]?[0] is JArray localRoot)
                    {
                        hasHeading = TryReadProtocolHeading(localRoot, out heading);
                    }
                    rows.Add(new RootRow
                    {
                        Record = record,
                        RecordIndex = recordIndex,
                        RowIndex = row,
                        Frame = frames[row].Value<int>(),
                        Position = position,
                        Heading = hasHeading ? heading : Quaternion.identity,
                        HasHeading = hasHeading
                    });
                }
            }
            return rows;
        }

        private static bool FilterRecordOutsideFrames(
            JObject record,
            ISet<int> deferredFrames)
        {
            if (record["frame_indices"] is not JArray) return true;
            return FilterRecordFrames(record, frame => deferredFrames == null || !deferredFrames.Contains(frame));
        }

        private static bool FilterRecordFrames(JObject record, Func<int, bool> keepFrame)
        {
            if (record["frame_indices"] is not JArray frames) return true;
            var keep = new List<int>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                int frame = frames[i].Value<int>();
                if (keepFrame(frame)) keep.Add(i);
            }
            if (keep.Count == frames.Count) return true;
            if (keep.Count == 0) return false;
            string[] alignedProperties =
            {
                "frame_indices",
                "smooth_root_2d",
                "global_root_heading",
                "local_joints_rot",
                "root_positions",
                "target_positions"
            };
            foreach (string propertyName in alignedProperties)
            {
                if (record[propertyName] is JArray aligned && aligned.Count == frames.Count)
                {
                    record[propertyName] = new JArray(keep.Select(index => aligned[index].DeepClone()));
                }
            }
            return true;
        }

        private static void TransformRecord(JObject record, PlanarTransform transform)
        {
            if (record["frame_indices"] is not JArray frames) return;
            string type = record.Value<string>("type")?.Trim().ToLowerInvariant();
            if (record["root_positions"] is JArray roots)
            {
                for (int i = 0; i < roots.Count && i < frames.Count; i++)
                {
                    if (TryReadPosition(roots[i], false, out Vector3 position)) WritePosition(roots[i], transform.Apply(position), false);
                }
            }
            if (record["smooth_root_2d"] is JArray smooth)
            {
                for (int i = 0; i < smooth.Count && i < frames.Count; i++)
                {
                    if (TryReadPosition(smooth[i], true, out Vector3 position)) WritePosition(smooth[i], transform.Apply(position), true);
                }
            }
            if (record["global_root_heading"] is JArray headings)
            {
                for (int i = 0; i < headings.Count; i++)
                {
                    if (TryReadHeading(headings[i], out Quaternion heading)) WriteHeading(headings[i], transform.Apply(heading));
                }
            }
            if (record["target_positions"] is JArray targets)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (TryReadPosition(targets[i], false, out Vector3 position))
                        WritePosition(targets[i], transform.Apply(position), false);
                }
            }
            if (type == "fullbody" && record["local_joints_rot"] is JArray local)
            {
                for (int i = 0; i < local.Count; i++)
                {
                    if (local[i] is not JArray joints || joints.Count == 0) continue;
                    if (joints[0] is not JArray rootAxis || rootAxis.Count < 3) continue;
                    Vector3 axis = new Vector3(rootAxis[0].Value<float>(), rootAxis[1].Value<float>(), rootAxis[2].Value<float>());
                    Quaternion protocol = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(axis);
                    Quaternion unity = new Quaternion(protocol.x, -protocol.y, -protocol.z, protocol.w);
                    Quaternion adjusted = KimodoMotionMath.ApplyPlanarHeading(unity, transform.Yaw * KimodoMotionMath.ResolvePlanarHeading(unity));
                    Quaternion output = new Quaternion(adjusted.x, -adjusted.y, -adjusted.z, adjusted.w);
                    Vector3 result = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(output);
                    joints[0] = new JArray(result.x, result.y, result.z);
                }
            }
        }

        private static bool TryReadMotionRoot(
            KimodoRawMotionData motion,
            string modelName,
            int frame,
            out Vector3 position,
            out Quaternion heading)
        {
            position = Vector3.zero;
            heading = Quaternion.identity;
            string json = KimodoRawMotionConstraintBuilder.BuildFullBodyConstraintsJson(
                motion,
                modelName,
                new[] { frame },
                0.0,
                motion.DurationSeconds);
            JObject fullBody = JArray.Parse(json).OfType<JObject>().FirstOrDefault();
            if (fullBody == null || !TryReadPosition(fullBody["root_positions"]?[0], false, out position))
            {
                return false;
            }
            if (fullBody["local_joints_rot"]?[0]?[0] is JArray root)
            {
                TryReadProtocolHeading(root, out heading);
            }
            return true;
        }

        private static bool TryReadPosition(JToken value, bool root2d, out Vector3 position)
        {
            position = Vector3.zero;
            if (value is not JArray array || array.Count < (root2d ? 2 : 3)) return false;
            position = root2d
                ? new Vector3(-array[0].Value<float>(), 0f, array[1].Value<float>())
                : new Vector3(-array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
            return true;
        }

        private static void WritePosition(JToken value, Vector3 position, bool root2d)
        {
            if (value is not JArray array) return;
            array[0] = -position.x;
            if (root2d) array[1] = position.z;
            else { array[1] = position.y; array[2] = position.z; }
        }

        private static bool TryReadHeading(JToken value, out Quaternion heading)
        {
            heading = Quaternion.identity;
            if (value is not JArray array || array.Count < 2) return false;
            float cosine = array[0].Value<float>();
            float sine = array[1].Value<float>();
            heading = Quaternion.AngleAxis(Mathf.Atan2(-sine, cosine) * Mathf.Rad2Deg, Vector3.up);
            return new Vector2(cosine, sine).sqrMagnitude > 1e-8f;
        }

        private static bool TryReadProtocolHeading(JArray axisAngle, out Quaternion heading)
        {
            heading = Quaternion.identity;
            if (axisAngle == null || axisAngle.Count < 3) return false;
            Vector3 axis = new Vector3(axisAngle[0].Value<float>(), axisAngle[1].Value<float>(), axisAngle[2].Value<float>());
            Quaternion protocol = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(axis);
            heading = KimodoMotionMath.ResolvePlanarHeading(
                new Quaternion(protocol.x, -protocol.y, -protocol.z, protocol.w));
            return true;
        }

        private static void WriteHeading(JToken value, Quaternion heading)
        {
            if (value is not JArray array) return;
            Vector3 forward = KimodoMotionMath.ResolvePlanarHeading(heading) * Vector3.forward;
            array[0] = forward.z;
            array[1] = -forward.x;
        }

        private readonly struct PlanarTransform
        {
            public readonly Quaternion Yaw;
            public readonly Vector3 Translation;
            public PlanarTransform(Quaternion yaw, Vector3 translation) { Yaw = yaw; Translation = translation; }
            public static PlanarTransform Identity => new PlanarTransform(Quaternion.identity, Vector3.zero);
            public Vector3 Apply(Vector3 position) => Yaw * position + Translation;
            public Quaternion Apply(Quaternion heading) => (Yaw * heading).normalized;
            public static PlanarTransform FromSourceToTarget(Vector3 source, Quaternion sourceHeading, Vector3 target, Quaternion targetHeading)
            {
                Quaternion yaw = Quaternion.Inverse(KimodoMotionMath.ResolvePlanarHeading(sourceHeading));
                yaw = KimodoMotionMath.ResolvePlanarHeading(targetHeading) * yaw;
                Vector3 sourcePlanar = KimodoMotionMath.FromPlanar(
                    KimodoMotionMath.ToPlanar(source), 0f);
                Vector3 targetPlanar = KimodoMotionMath.FromPlanar(
                    KimodoMotionMath.ToPlanar(target), 0f);
                Vector3 translation = targetPlanar - yaw * sourcePlanar;
                return new PlanarTransform(yaw, translation);
            }
        }
    }
}
