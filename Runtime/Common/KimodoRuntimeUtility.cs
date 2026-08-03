using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    public static class KimodoRuntimeUtility
    {
        public static string SanitizeName(string input, string defaultName = "joint")
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.IsNullOrWhiteSpace(defaultName) ? "joint" : defaultName;
            }

            return input.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
        }

        public static Vector3 QuaternionToAxisAngleVector(Quaternion q)
        {
            return KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(q);
        }
    }
}
