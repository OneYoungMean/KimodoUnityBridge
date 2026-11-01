using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoConstraintWindow
    {
        private static readonly string[] FrameAlignedProperties =
        {
            "frame_indices",
            "smooth_root_2d",
            "global_root_heading",
            "local_joints_rot",
            "root_positions",
            "target_positions"
        };

        public int ModelFrameCount;
        public string Json = string.Empty;

        public bool HasConstraints => !string.IsNullOrWhiteSpace(Json);

        public static KimodoConstraintWindow FromJson(string json, int modelFrameCount = 0)
        {
            return new KimodoConstraintWindow
            {
                Json = json ?? string.Empty,
                ModelFrameCount = Math.Max(0, modelFrameCount)
            };
        }

        public int FirstSampleFrame
        {
            get
            {
                ResolveSampleRange(out int firstFrame, out _);
                return firstFrame;
            }
        }

        public int EffectiveModelFrameCount
        {
            get
            {
                if (ModelFrameCount > 0) return ModelFrameCount;
                ResolveSampleRange(out int firstFrame, out int lastFrame);
                return lastFrame < firstFrame ? 0 : lastFrame - firstFrame + 1;
            }
        }

        public static KimodoConstraintWindow SliceToLocalFrames(
            string constraintsJson,
            int sourceStartFrame,
            int modelFrameCount)
        {
            var result = new KimodoConstraintWindow { ModelFrameCount = Math.Max(0, modelFrameCount) };
            if (string.IsNullOrWhiteSpace(constraintsJson)) return result;

            JArray source = JArray.Parse(constraintsJson);
            var mappedConstraints = new JArray();
            foreach (JObject constraint in source.Children<JObject>())
            {
                if (!(constraint["frame_indices"] is JArray))
                    throw new InvalidOperationException("A generation constraint is missing frame_indices.");

                var mapped = (JObject)constraint.DeepClone();
                if (!FilterFrameAlignedRecord(
                        mapped,
                        frame => frame >= sourceStartFrame && frame < sourceStartFrame + modelFrameCount))
                    continue;
                JArray localFrames = (JArray)mapped["frame_indices"];
                for (int index = 0; index < localFrames.Count; index++)
                    localFrames[index] = localFrames[index].Value<int>() - sourceStartFrame;
                mappedConstraints.Add(mapped);
            }

            result.Json = mappedConstraints.Count == 0
                ? string.Empty
                : mappedConstraints.ToString(Formatting.None);
            return result;
        }

        public KimodoConstraintWindow ShiftFrames(int frameOffset, int runtimeFrameCount)
        {
            if (string.IsNullOrWhiteSpace(Json) || frameOffset == 0)
            {
                return new KimodoConstraintWindow { Json = Json ?? string.Empty, ModelFrameCount = ModelFrameCount };
            }

            JArray shiftedConstraints = JArray.Parse(Json);
            foreach (JObject constraint in shiftedConstraints.Children<JObject>())
            {
                if (!(constraint["frame_indices"] is JArray frames)) continue;
                for (int index = 0; index < frames.Count; index++)
                {
                    if (frames[index].Type != JTokenType.Integer) continue;
                    int shifted = frames[index].Value<int>() + frameOffset;
                    if (shifted < 0 || shifted >= runtimeFrameCount)
                    {
                        throw new InvalidOperationException(
                            $"Reused constraint frame {shifted} is outside runtime range [0,{runtimeFrameCount}).");
                    }
                    frames[index] = shifted;
                }
            }

            return new KimodoConstraintWindow
            {
                Json = shiftedConstraints.ToString(Formatting.None),
                ModelFrameCount = ModelFrameCount
            };
        }

        public static KimodoConstraintWindow Combine(
            KimodoConstraintWindow first,
            KimodoConstraintWindow second)
        {
            var combined = new JArray();
            AppendConstraints(combined, first?.Json);
            AppendConstraints(combined, second?.Json);
            return new KimodoConstraintWindow
            {
                Json = combined.Count == 0 ? string.Empty : combined.ToString(Formatting.None),
                // A replacement or appended window can intentionally widen the
                // destination span (for example the Stage 3 loop window).
                ModelFrameCount = Math.Max(first?.ModelFrameCount ?? 0, second?.ModelFrameCount ?? 0)
            };
        }

        public KimodoConstraintWindow AppendReplacingFullBodyFrame(
            KimodoConstraintWindow additional,
            int replacedFrame)
        {
            if (replacedFrame < 0)
                throw new ArgumentOutOfRangeException(nameof(replacedFrame));

            var combined = new JArray();
            AppendReplacingFullBodyFrame(combined, Json, replacedFrame);
            AppendConstraints(combined, additional?.Json);
            return new KimodoConstraintWindow
            {
                Json = combined.Count == 0 ? string.Empty : combined.ToString(Formatting.None),
                ModelFrameCount = Math.Max(ModelFrameCount, additional?.ModelFrameCount ?? 0)
            };
        }

        private void ResolveSampleRange(out int firstFrame, out int lastFrame)
        {
            firstFrame = int.MaxValue;
            lastFrame = -1;
            if (string.IsNullOrWhiteSpace(Json)) return;

            JToken parsed = JToken.Parse(Json);
            if (!(parsed is JArray constraints)) return;
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                if (!(constraint["frame_indices"] is JArray frames)) continue;
                foreach (JToken frame in frames)
                {
                    if (frame.Type != JTokenType.Integer) continue;
                    int value = frame.Value<int>();
                    firstFrame = Math.Min(firstFrame, value);
                    lastFrame = Math.Max(lastFrame, value);
                }
            }
        }

        internal static void AppendConstraints(JArray output, string json)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (string.IsNullOrWhiteSpace(json)) return;
            JToken token = JToken.Parse(json);
            if (token is JArray constraints)
            {
                foreach (JToken item in constraints) output.Add(item.DeepClone());
                return;
            }
            if (token is JObject constraint)
            {
                output.Add(constraint.DeepClone());
                return;
            }
            throw new InvalidOperationException("Constraint JSON must be an array or object.");
        }

        internal static bool FilterFrameAlignedRecord(
            JObject record,
            Func<int, bool> keepFrame)
        {
            if (record == null) return false;
            if (!(record["frame_indices"] is JArray frames)) return true;
            if (keepFrame == null) throw new ArgumentNullException(nameof(keepFrame));

            var retainedIndices = new List<int>(frames.Count);
            for (int index = 0; index < frames.Count; index++)
            {
                if (frames[index].Type != JTokenType.Integer)
                    throw new InvalidOperationException("Constraint frame_indices must contain integers.");
                if (keepFrame(frames[index].Value<int>())) retainedIndices.Add(index);
            }

            foreach (string propertyName in FrameAlignedProperties)
            {
                if (propertyName == "frame_indices") continue;
                if (!(record[propertyName] is JArray values)) continue;
                if (values.Count != frames.Count)
                {
                    throw new InvalidOperationException(
                        $"Constraint property '{propertyName}' does not match frame_indices.");
                }
                record[propertyName] = new JArray(
                    retainedIndices.Select(index => values[index].DeepClone()));
            }

            record["frame_indices"] = new JArray(
                retainedIndices.Select(index => frames[index].DeepClone()));
            return retainedIndices.Count > 0;
        }

        private static void AppendReplacingFullBodyFrame(JArray output, string json, int replacedFrame)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            JToken token = JToken.Parse(json);
            if (token is JArray constraints)
            {
                foreach (JToken item in constraints)
                {
                    if (item is JObject constraint)
                        AppendFullBodyWithoutFrame(output, constraint, replacedFrame);
                    else
                        output.Add(item.DeepClone());
                }
                return;
            }
            if (token is JObject singleConstraint)
            {
                AppendFullBodyWithoutFrame(output, singleConstraint, replacedFrame);
                return;
            }
            throw new InvalidOperationException("Constraint JSON must be an array or object.");
        }

        private static void AppendFullBodyWithoutFrame(JArray output, JObject source, int replacedFrame)
        {
            var constraint = (JObject)source.DeepClone();
            if (!string.Equals(constraint.Value<string>("type"), "fullbody", StringComparison.OrdinalIgnoreCase) ||
                !(constraint["frame_indices"] is JArray frames))
            {
                output.Add(constraint);
                return;
            }

            if (FilterFrameAlignedRecord(constraint, frame => frame != replacedFrame))
                output.Add(constraint);
        }
    }
}
