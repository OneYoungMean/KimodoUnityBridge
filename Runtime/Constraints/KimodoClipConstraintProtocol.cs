using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    [Serializable]
    public sealed class KimodoClipConstraintPositionMask
    {
        public bool x;
        public bool y;
        public bool z;
    }

    [Serializable]
    public sealed class KimodoClipConstraintJointMask
    {
        public string jointName = string.Empty;
        public KimodoClipConstraintPositionMask position = new KimodoClipConstraintPositionMask();
        public bool rotation;
    }

    [Serializable]
    public sealed class KimodoClipConstraintMask
    {
        public KimodoClipConstraintPositionMask rootPosition = new KimodoClipConstraintPositionMask();
        public bool rootHeading;
        public bool rootRotation;
        public List<KimodoClipConstraintJointMask> joints = new List<KimodoClipConstraintJointMask>();

        public static KimodoClipConstraintMask FromAvatarMask(string modelName, AvatarMask avatarMask)
        {
            if (avatarMask == null) throw new ArgumentNullException(nameof(avatarMask));
            string[] names = GetJointNames(modelName);
            int[] parents = KimodoRigProfileDatabase.GetParentIndicesForModel(modelName);
            var selected = new bool[names.Length];
            if (avatarMask.transformCount > 0)
            {
                var jointByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < names.Length; index++) jointByName[names[index]] = index;
                for (int index = 0; index < avatarMask.transformCount; index++)
                {
                    if (!avatarMask.GetTransformActive(index)) continue;
                    string path = avatarMask.GetTransformPath(index) ?? string.Empty;
                    int separator = path.LastIndexOf('/');
                    string jointName = separator >= 0 ? path.Substring(separator + 1) : path;
                    if (jointByName.TryGetValue(jointName, out int jointIndex))
                    {
                        MarkDescendants(selected, jointIndex, parents);
                    }
                }
                if (Array.IndexOf(selected, true) < 0)
                {
                    throw new InvalidOperationException(
                        $"AvatarMask contains no transform that maps to the '{modelName}' profile.");
                }
            }
            else
            {
                SelectHumanoidBodyParts(selected, names, avatarMask);
            }

            var result = new KimodoClipConstraintMask
            {
                rootPosition = new KimodoClipConstraintPositionMask
                {
                    x = selected.Length > 0 && selected[0],
                    y = selected.Length > 0 && selected[0],
                    z = selected.Length > 0 && selected[0]
                },
                rootHeading = selected.Length > 0 && selected[0],
                rootRotation = selected.Length > 0 && selected[0]
            };
            for (int index = 1; index < names.Length; index++)
            {
                bool enabled = selected[index];
                result.joints.Add(new KimodoClipConstraintJointMask
                {
                    jointName = names[index],
                    position = new KimodoClipConstraintPositionMask { x = enabled, y = enabled, z = enabled },
                    rotation = enabled
                });
            }
            return result;
        }

        public static KimodoClipConstraintMask UpperBody(string modelName)
        {
            string[] names = GetJointNames(modelName);
            int[] parents = KimodoRigProfileDatabase.GetParentIndicesForModel(modelName);
            int upperRoot = Array.FindIndex(names, name =>
                name.IndexOf("spine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("waist", StringComparison.OrdinalIgnoreCase) >= 0);
            if (upperRoot < 0)
            {
                throw new InvalidOperationException($"Model profile '{modelName}' has no upper-body root joint.");
            }
            var result = new KimodoClipConstraintMask();
            for (int index = 1; index < names.Length; index++)
            {
                bool enabled = IsDescendant(index, upperRoot, parents);
                result.joints.Add(new KimodoClipConstraintJointMask
                {
                    jointName = names[index],
                    position = new KimodoClipConstraintPositionMask { x = enabled, y = enabled, z = enabled },
                    rotation = enabled
                });
            }
            return result;
        }

        public static KimodoClipConstraintMask LowerBody(string modelName)
        {
            KimodoClipConstraintMask upper = UpperBody(modelName);
            foreach (KimodoClipConstraintJointMask joint in upper.joints)
            {
                bool enabled = !(joint.position.x || joint.position.y || joint.position.z);
                joint.position = new KimodoClipConstraintPositionMask { x = enabled, y = enabled, z = enabled };
                joint.rotation = enabled;
            }
            return upper;
        }

        public static KimodoClipConstraintMask FullBody(string modelName, bool includeRoot = false)
        {
            string[] names = GetJointNames(modelName);
            var result = new KimodoClipConstraintMask
            {
                rootPosition = new KimodoClipConstraintPositionMask { x = includeRoot, y = includeRoot, z = includeRoot },
                rootHeading = includeRoot,
                rootRotation = includeRoot
            };
            for (int index = 1; index < names.Length; index++)
            {
                result.joints.Add(new KimodoClipConstraintJointMask
                {
                    jointName = names[index],
                    position = new KimodoClipConstraintPositionMask { x = true, y = true, z = true },
                    rotation = true
                });
            }
            return result;
        }

        private static void MarkDescendants(bool[] selected, int ancestor, int[] parents)
        {
            for (int index = 0; index < selected.Length; index++)
            {
                if (IsDescendant(index, ancestor, parents)) selected[index] = true;
            }
        }

        private static void SelectHumanoidBodyParts(bool[] selected, string[] names, AvatarMask avatarMask)
        {
            for (int index = 0; index < names.Length; index++)
            {
                string name = names[index].ToLowerInvariant();
                bool isLeft = name.Contains("left");
                bool isRight = name.Contains("right");
                bool isFinger = name.Contains("thumb") || name.Contains("index") || name.Contains("middle") ||
                                name.Contains("ring") || name.Contains("pinky") || name.Contains("finger");
                selected[index] =
                    (index == 0 && avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Root)) ||
                    ((name.Contains("spine") || name.Contains("chest") || name.Contains("waist")) &&
                     avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Body)) ||
                    ((name.Contains("neck") || name.Contains("head") || name.Contains("jaw") || name.Contains("eye")) &&
                     avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Head)) ||
                    (isLeft && !isFinger && (name.Contains("shoulder") || name.Contains("arm") || name.Contains("hand")) &&
                     avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm)) ||
                    (isRight && !isFinger && (name.Contains("shoulder") || name.Contains("arm") || name.Contains("hand")) &&
                     avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm)) ||
                    (isLeft && isFinger && avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers)) ||
                    (isRight && isFinger && avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers)) ||
                    (isLeft && (name.Contains("leg") || name.Contains("shin") || name.Contains("upleg") || name.Contains("foot") || name.Contains("toe")) &&
                     avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg)) ||
                    (isRight && (name.Contains("leg") || name.Contains("shin") || name.Contains("upleg") || name.Contains("foot") || name.Contains("toe")) &&
                     avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg));
            }
        }

        private static bool IsDescendant(int joint, int ancestor, int[] parents)
        {
            for (int current = joint; current >= 0; current = parents[current])
            {
                if (current == ancestor) return true;
            }
            return false;
        }

        private static string[] GetJointNames(string modelName) =>
            KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
    }

    [Serializable]
    public sealed class KimodoClipConstraint
    {
        [NonSerialized] public byte[] motionBytes;
        public float startTime;
        public float duration;
        public KimodoClipConstraintMask mask;
    }

    [Serializable]
    public sealed class KimodoConstraintPayload
    {
        public string json = string.Empty;
        [NonSerialized] public List<KimodoClipConstraint> clips = new List<KimodoClipConstraint>();

        public bool IsEmpty => string.IsNullOrWhiteSpace(json) && (clips == null || clips.Count == 0);

        internal string Serialize(string modelName, List<byte[]> attachments)
        {
            var output = new JArray();
            AppendTo(output, modelName, attachments);
            return output.Count > 0
                ? output.ToString(Formatting.None)
                : string.IsNullOrWhiteSpace(json) ? string.Empty : "[]";
        }

        private void AppendTo(JArray output, string modelName, List<byte[]> attachments)
        {
            KimodoClipConstraintSerializer.AppendJson(output, json);
            KimodoClipConstraintSerializer.Append(output, modelName, clips, attachments);
        }
    }

    public static class KimodoClipConstraintProtocol
    {
        public static string Serialize(
            string modelName,
            IReadOnlyList<KimodoClipConstraint> clips,
            List<byte[]> attachments)
        {
            var output = new JArray();
            KimodoClipConstraintSerializer.Append(output, modelName, clips, attachments);
            return output.Count > 0 ? output.ToString(Formatting.None) : string.Empty;
        }
    }

    internal static class KimodoClipConstraintSerializer
    {
        internal static void Append(
            JArray output,
            string modelName,
            IReadOnlyList<KimodoClipConstraint> clips,
            List<byte[]> attachments)
        {
            if (clips == null) return;
            string[] jointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            float sourceFrameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
            if (jointNames == null || jointNames.Length == 0)
            {
                throw new InvalidOperationException($"Model profile '{modelName}' has no joint layout.");
            }
            for (int index = 0; index < clips.Count; index++)
            {
                KimodoClipConstraint clip = clips[index] ?? throw new InvalidOperationException("ClipConstraint is null.");
                KimodoRawMotionData motion = ParseMotionPayload(clip.motionBytes);
                if (motion.JointCount != jointNames.Length || !Mathf.Approximately(motion.FrameRate, sourceFrameRate))
                {
                    throw new InvalidOperationException("ClipConstraint KMB does not match the selected model profile.");
                }
                if (float.IsNaN(clip.startTime) || float.IsInfinity(clip.startTime) ||
                    float.IsNaN(clip.duration) || float.IsInfinity(clip.duration) || clip.duration <= 0f)
                {
                    throw new InvalidOperationException("ClipConstraint startTime/duration must be finite and duration must be positive.");
                }
                float motionDuration = motion.FrameCount / motion.FrameRate;
                if (Mathf.Abs(clip.duration - motionDuration) > 0.5f / motion.FrameRate)
                {
                    throw new InvalidOperationException(
                        $"ClipConstraint duration {clip.duration:R}s does not match its {motion.FrameCount}-frame KMB ({motionDuration:R}s).");
                }
                if (attachments == null) throw new ArgumentNullException(nameof(attachments));
                int attachment = attachments.Count;
                attachments.Add(clip.motionBytes);
                var item = new JObject
                {
                    ["type"] = "clip",
                    ["format"] = "kmb_attachment_v1",
                    ["attachment"] = attachment,
                    ["start_time"] = clip.startTime,
                    ["duration"] = clip.duration
                };
                if (clip.mask != null) item["mask"] = SerializeMask(clip.mask, jointNames);
                output.Add(item);
            }
        }

        private static KimodoRawMotionData ParseMotionPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new InvalidOperationException("ClipConstraint KMB is empty.");
            }
            if (!KimodoRawMotionUtility.TryParseFlatBuffer(payload, out KimodoRawMotionData motion, out string error))
            {
                throw new InvalidOperationException($"ClipConstraint KMB is invalid: {error}");
            }
            return motion;
        }

        private static JObject SerializeMask(KimodoClipConstraintMask value, string[] jointNames)
        {
            var byName = new Dictionary<string, KimodoClipConstraintJointMask>(StringComparer.OrdinalIgnoreCase);
            foreach (KimodoClipConstraintJointMask joint in value.joints ?? new List<KimodoClipConstraintJointMask>())
            {
                if (joint == null || string.IsNullOrWhiteSpace(joint.jointName) ||
                    !Array.Exists(jointNames, name => string.Equals(name, joint.jointName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"ClipConstraint mask contains unknown joint '{joint?.jointName}'.");
                }
                byName[joint.jointName] = joint;
            }
            KimodoClipConstraintPositionMask root = value.rootPosition ?? new KimodoClipConstraintPositionMask();
            var joints = new JArray();
            for (int index = 1; index < jointNames.Length; index++)
            {
                byName.TryGetValue(jointNames[index], out KimodoClipConstraintJointMask joint);
                KimodoClipConstraintPositionMask position = joint?.position ?? new KimodoClipConstraintPositionMask();
                joints.Add(new JObject
                {
                    ["joint_name"] = jointNames[index],
                    ["position"] = new JArray(position.x, position.y, position.z),
                    ["rotation"] = joint != null && joint.rotation
                });
            }
            return new JObject
            {
                ["root_position"] = new JArray(root.x, root.y, root.z),
                ["root_heading"] = value.rootHeading,
                ["root_rotation"] = value.rootRotation,
                ["joints"] = joints
            };
        }

        internal static void AppendJson(JArray output, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            JToken token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (JToken item in array) output.Add(item.DeepClone());
                return;
            }
            if (token is JObject obj)
            {
                output.Add(obj.DeepClone());
                return;
            }
            throw new InvalidOperationException("constraints_json must be a JSON array or object.");
        }
    }
}
