using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KimodoBridge.Editor
{
    internal static class KimodoClipConstraintBakeUtility
    {
        internal static string AppendConstraintsJson(string baseJson, string additionalJson)
        {
            var output = new JArray();
            AppendJson(output, baseJson);
            AppendJson(output, additionalJson);
            return output.Count == 0 ? string.Empty : output.ToString(Formatting.None);
        }

        internal static string AppendConstraintsReplacingFullBodyFrameJson(
            string baseJson,
            string additionalJson,
            int replacedFrame)
        {
            if (replacedFrame < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(replacedFrame));
            }

            var output = new JArray();
            AppendJsonReplacingFullBodyFrame(output, baseJson, replacedFrame);
            AppendJson(output, additionalJson);
            return output.Count == 0 ? string.Empty : output.ToString(Formatting.None);
        }

        internal static string ReplaceRoot2DConstraintsJson(string baseJson, string replacementJson)
        {
            var output = new JArray();
            AppendJsonExceptRoot2D(output, baseJson);
            AppendJson(output, replacementJson);
            return output.Count == 0 ? string.Empty : output.ToString(Formatting.None);
        }

        private static void AppendJson(JArray output, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }
            JToken token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    output.Add(item.DeepClone());
                }
                return;
            }
            if (token is JObject obj)
            {
                output.Add(obj.DeepClone());
                return;
            }
            throw new InvalidOperationException("Constraint JSON must be an array or object.");
        }

        private static void AppendJsonReplacingFullBodyFrame(JArray output, string json, int replacedFrame)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            JToken token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    if (item is JObject constraint)
                    {
                        AppendFullBodyFrameFiltered(output, constraint, replacedFrame);
                    }
                    else
                    {
                        output.Add(item.DeepClone());
                    }
                }
                return;
            }
            if (token is JObject obj)
            {
                AppendFullBodyFrameFiltered(output, obj, replacedFrame);
                return;
            }
            throw new InvalidOperationException("Constraint JSON must be an array or object.");
        }

        private static void AppendFullBodyFrameFiltered(JArray output, JObject source, int replacedFrame)
        {
            JObject constraint = (JObject)source.DeepClone();
            if (!string.Equals(constraint.Value<string>("type"), "fullbody", StringComparison.OrdinalIgnoreCase) ||
                constraint["frame_indices"] is not JArray frames)
            {
                output.Add(constraint);
                return;
            }

            var keep = new List<int>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].Type != JTokenType.Integer || frames[i].Value<int>() != replacedFrame)
                {
                    keep.Add(i);
                }
            }
            if (keep.Count == frames.Count)
            {
                output.Add(constraint);
                return;
            }
            if (keep.Count == 0) return;

            string[] alignedProperties =
            {
                "frame_indices",
                "root_positions",
                "smooth_root_2d",
                "local_joints_rot",
                "target_positions"
            };
            foreach (string propertyName in alignedProperties)
            {
                if (constraint[propertyName] is not JArray aligned || aligned.Count != frames.Count) continue;
                var filtered = new JArray();
                foreach (int index in keep) filtered.Add(aligned[index].DeepClone());
                constraint[propertyName] = filtered;
            }
            output.Add(constraint);
        }

        private static void AppendJsonExceptRoot2D(JArray output, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            JToken token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    if (!IsRoot2DConstraint(item)) output.Add(item.DeepClone());
                }
                return;
            }
            if (token is JObject obj)
            {
                if (!IsRoot2DConstraint(obj)) output.Add(obj.DeepClone());
                return;
            }
            throw new InvalidOperationException("Constraint JSON must be an array or object.");
        }

        private static bool IsRoot2DConstraint(JToken token)
        {
            return token is JObject constraint &&
                string.Equals(constraint.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase);
        }
}

}
