using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintProtocolNormalizerTests
    {
        private static JArray Heading(float unityYawDegrees)
        {
            float radians = unityYawDegrees * Mathf.Deg2Rad;
            return new JArray(Mathf.Cos(radians), -Mathf.Sin(radians));
        }

        private static Vector3 ReadVector3(JToken value)
        {
            return new Vector3(value[0].Value<float>(), value[1].Value<float>(), value[2].Value<float>());
        }

        private static Quaternion ResolvePlanarRotation(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        private static Quaternion ToKimodoRotation(Quaternion unityRotation)
        {
            return new Quaternion(unityRotation.x, -unityRotation.y, -unityRotation.z, unityRotation.w);
        }

        private static Quaternion FromKimodoRotation(Quaternion kimodoRotation)
        {
            return new Quaternion(kimodoRotation.x, -kimodoRotation.y, -kimodoRotation.z, kimodoRotation.w);
        }
    }
}
