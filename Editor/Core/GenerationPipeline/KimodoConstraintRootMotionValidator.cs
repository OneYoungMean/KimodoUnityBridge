using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintRootMotionValidator
    {
        private sealed class RootRow
        {
            public int RecordIndex;
            public int RowIndex;
            public int Frame;
            public Vector3 Position;
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
                for (int row = 0; row < frames.Count && row < positions.Count; row++)
                {
                    if (!TryReadPosition(positions[row], type == "root2d", out Vector3 position)) continue;
                    rows.Add(new RootRow
                    {
                        RecordIndex = recordIndex,
                        RowIndex = row,
                        Frame = frames[row].Value<int>(),
                        Position = position
                    });
                }
            }
            return rows;
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

    }
}
