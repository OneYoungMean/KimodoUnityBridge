using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KimodoBridge.Editor
{
    internal static class KimodoClipConstraintBakeUtility
    {
        internal static string AppendConstraintsJson(string baseJson, string additionalJson)
        {
            var output = new JArray();
            KimodoConstraintWindow.AppendConstraints(output, baseJson);
            KimodoConstraintWindow.AppendConstraints(output, additionalJson);
            return output.Count == 0 ? string.Empty : output.ToString(Formatting.None);
        }

        internal static string ReplaceRoot2DConstraintsJson(string baseJson, string replacementJson)
        {
            var output = new JArray();
            AppendJsonExceptRoot2D(output, baseJson);
            KimodoConstraintWindow.AppendConstraints(output, replacementJson);
            return output.Count == 0 ? string.Empty : output.ToString(Formatting.None);
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
