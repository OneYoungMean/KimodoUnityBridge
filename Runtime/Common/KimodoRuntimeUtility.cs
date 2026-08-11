using TimelineInject;
using UnityEngine;
using System;

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

    public static class KimodoUnityObjectIdUtility
    {
        public static string NameKey(UnityEngine.Object value)
        {
            return value != null ? value.name ?? string.Empty : string.Empty;
        }

        public static int IdHash(UnityEngine.Object value)
        {
            if (value == null)
            {
                return 0;
            }

#if UNITY_6000_5_OR_NEWER
            return value.GetEntityId().GetHashCode();
#else
            return value.GetInstanceID();
#endif
        }

        public static int NameHash(UnityEngine.Object value)
        {
            return StringComparer.Ordinal.GetHashCode(NameKey(value));
        }
    }
}
