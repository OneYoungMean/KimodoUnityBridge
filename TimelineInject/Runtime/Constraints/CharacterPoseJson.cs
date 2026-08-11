using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CharacterAnimationCli.Unity
{
    public static class CharacterPoseJson
    {
        public static JObject ToJson(CharacterPose pose)
        {
            if (pose == null)
            {
                throw new ArgumentNullException(nameof(pose));
            }
            if (!pose.TryValidate(out string error))
            {
                throw new InvalidOperationException(error);
            }

            return new JObject
            {
                ["muscles"] = new JArray(pose.muscles),
                ["root"] = TransformToJson(pose.root),
                ["hands"] = new JObject
                {
                    ["left"] = TransformToJson(pose.hands.left),
                    ["right"] = TransformToJson(pose.hands.right)
                },
                ["feet"] = new JObject
                {
                    ["left"] = TransformToJson(pose.feet.left),
                    ["right"] = TransformToJson(pose.feet.right)
                }
            };
        }

        public static CharacterPose Parse(JObject json)
        {
            RequireObject(json, "pose");
            RequireToken(json, "muscles");
            RequireToken(json, "root");
            RequireToken(json, "hands");
            RequireToken(json, "feet");

            JObject root = json["root"] as JObject;
            JObject hands = json["hands"] as JObject;
            JObject feet = json["feet"] as JObject;
            RequireTransform(root, "root");
            RequireTransform(hands?["left"] as JObject, "hands.left");
            RequireTransform(hands?["right"] as JObject, "hands.right");
            RequireTransform(feet?["left"] as JObject, "feet.left");
            RequireTransform(feet?["right"] as JObject, "feet.right");

            return ApplyPatch(new CharacterPose(), json);
        }

        public static CharacterPose ApplyPatch(CharacterPose current, JObject patch)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }
            RequireObject(patch, "pose patch");

            CharacterPose result = current.Clone();
            if (patch["muscles"] != null)
            {
                result.muscles = ReadMuscles(patch["muscles"]);
            }
            if (patch["root"] != null)
            {
                result.root = PatchTransform(result.root, patch["root"] as JObject, "root");
            }
            if (patch["hands"] != null)
            {
                JObject hands = patch["hands"] as JObject
                    ?? throw new InvalidOperationException("hands must be an object.");
                if (hands["left"] != null)
                {
                    result.hands.left = PatchTransform(result.hands.left, hands["left"] as JObject, "hands.left");
                }
                if (hands["right"] != null)
                {
                    result.hands.right = PatchTransform(result.hands.right, hands["right"] as JObject, "hands.right");
                }
            }
            if (patch["feet"] != null)
            {
                JObject feet = patch["feet"] as JObject
                    ?? throw new InvalidOperationException("feet must be an object.");
                if (feet["left"] != null)
                {
                    result.feet.left = PatchTransform(result.feet.left, feet["left"] as JObject, "feet.left");
                }
                if (feet["right"] != null)
                {
                    result.feet.right = PatchTransform(result.feet.right, feet["right"] as JObject, "feet.right");
                }
            }

            if (!result.TryValidate(out string error))
            {
                throw new InvalidOperationException(error);
            }
            return result;
        }

        private static JObject TransformToJson(CharacterPoseTransform value)
        {
            return new JObject
            {
                ["t"] = new JArray(value.t.x, value.t.y, value.t.z),
                ["q"] = new JArray(value.q.x, value.q.y, value.q.z, value.q.w)
            };
        }

        private static CharacterPoseTransform PatchTransform(
            CharacterPoseTransform current,
            JObject patch,
            string name)
        {
            if (patch == null)
            {
                throw new InvalidOperationException($"{name} must be an object.");
            }

            var result = current != null ? current.Clone() : new CharacterPoseTransform();
            if (patch["t"] != null)
            {
                result.t = ReadVector3(patch["t"], $"{name}.t");
            }
            if (patch["q"] != null)
            {
                result.q = ReadQuaternion(patch["q"], $"{name}.q");
            }
            return result;
        }

        private static float[] ReadMuscles(JToken token)
        {
            JArray array = token as JArray
                ?? throw new InvalidOperationException("muscles must be an array.");
            if (array.Count != CharacterPose.MuscleCount)
            {
                throw new InvalidOperationException($"muscles must contain exactly {CharacterPose.MuscleCount} values.");
            }

            var result = new float[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                result[i] = ReadFiniteFloat(array[i], $"muscles[{i}]");
            }
            return result;
        }

        private static Vector3 ReadVector3(JToken token, string name)
        {
            JArray array = token as JArray
                ?? throw new InvalidOperationException($"{name} must be [x,y,z].");
            if (array.Count != 3)
            {
                throw new InvalidOperationException($"{name} must contain exactly three values.");
            }
            return new Vector3(
                ReadFiniteFloat(array[0], $"{name}[0]"),
                ReadFiniteFloat(array[1], $"{name}[1]"),
                ReadFiniteFloat(array[2], $"{name}[2]"));
        }

        private static Quaternion ReadQuaternion(JToken token, string name)
        {
            JArray array = token as JArray
                ?? throw new InvalidOperationException($"{name} must be [x,y,z,w].");
            if (array.Count != 4)
            {
                throw new InvalidOperationException($"{name} must contain exactly four values.");
            }
            var value = new Quaternion(
                ReadFiniteFloat(array[0], $"{name}[0]"),
                ReadFiniteFloat(array[1], $"{name}[1]"),
                ReadFiniteFloat(array[2], $"{name}[2]"),
                ReadFiniteFloat(array[3], $"{name}[3]"));
            float magnitudeSquared = value.x * value.x + value.y * value.y +
                value.z * value.z + value.w * value.w;
            if (magnitudeSquared <= 1e-8f)
            {
                throw new InvalidOperationException($"{name} must be non-zero.");
            }
            return value.normalized;
        }

        private static float ReadFiniteFloat(JToken token, string name)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                throw new InvalidOperationException($"{name} must be a number.");
            }
            float value = token.Value<float>();
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException($"{name} must be finite.");
            }
            return value;
        }

        private static void RequireTransform(JObject value, string name)
        {
            RequireObject(value, name);
            RequireToken(value, "t", name);
            RequireToken(value, "q", name);
        }

        private static void RequireObject(JObject value, string name)
        {
            if (value == null)
            {
                throw new InvalidOperationException($"{name} must be an object.");
            }
        }

        private static void RequireToken(JObject value, string key, string parent = "pose")
        {
            if (value?[key] == null)
            {
                throw new InvalidOperationException($"{parent}.{key} is required.");
            }
        }
    }
}
