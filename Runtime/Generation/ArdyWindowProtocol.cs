using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KimodoBridge
{
    [Serializable]
    public sealed class KimodoArdyPositionMask
    {
        public bool x;
        public bool y;
        public bool z;
    }

    [Serializable]
    public sealed class KimodoArdyJointPositionMask
    {
        public string jointName = string.Empty;
        public KimodoArdyPositionMask position = new KimodoArdyPositionMask();
    }

    [Serializable]
    public sealed class KimodoArdyConstraintMask
    {
        public KimodoArdyPositionMask rootPosition = new KimodoArdyPositionMask();
        public bool rootHeading;
        public List<KimodoArdyJointPositionMask> joints = new List<KimodoArdyJointPositionMask>();

        public static KimodoArdyConstraintMask FromAvatarMask(
            string modelName,
            AvatarMask avatarMask,
            bool rootPosition = false,
            bool rootHeading = false)
        {
            if (avatarMask == null)
            {
                throw new ArgumentNullException(nameof(avatarMask));
            }
            string[] names = GetJointNames(modelName);
            var known = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < avatarMask.transformCount; index++)
            {
                if (!avatarMask.GetTransformActive(index))
                {
                    continue;
                }
                string path = avatarMask.GetTransformPath(index) ?? string.Empty;
                int separator = path.LastIndexOf('/');
                string jointName = separator >= 0 ? path.Substring(separator + 1) : path;
                bool hasActiveChild = false;
                string childPrefix = path + "/";
                for (int child = index + 1; child < avatarMask.transformCount; child++)
                {
                    string childPath = avatarMask.GetTransformPath(child) ?? string.Empty;
                    if (!childPath.StartsWith(childPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (avatarMask.GetTransformActive(child))
                    {
                        hasActiveChild = true;
                        break;
                    }
                }
                if (!known.Contains(jointName) && !hasActiveChild)
                {
                    throw new InvalidOperationException(
                        $"AvatarMask joint '{jointName}' does not exist in ARDY profile '{modelName}'.");
                }
                selected.Add(jointName);
            }
            var result = new KimodoArdyConstraintMask
            {
                rootPosition = new KimodoArdyPositionMask { x = rootPosition, y = rootPosition, z = rootPosition },
                rootHeading = rootHeading
            };
            for (int index = 1; index < names.Length; index++)
            {
                bool enabled = selected.Contains(names[index]);
                result.joints.Add(new KimodoArdyJointPositionMask
                {
                    jointName = names[index],
                    position = new KimodoArdyPositionMask { x = enabled, y = enabled, z = enabled }
                });
            }
            return result;
        }

        public static KimodoArdyConstraintMask UpperBody(string modelName)
        {
            string[] names = GetJointNames(modelName);
            int[] parents = KimodoRigProfileDatabase.GetParentIndicesForModel(modelName);
            int upperRoot = Array.FindIndex(names, name =>
                name.IndexOf("spine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("waist", StringComparison.OrdinalIgnoreCase) >= 0);
            if (upperRoot < 0)
            {
                throw new InvalidOperationException($"ARDY profile '{modelName}' has no upper-body root joint.");
            }
            var result = new KimodoArdyConstraintMask();
            for (int index = 1; index < names.Length; index++)
            {
                bool enabled = IsDescendant(index, upperRoot, parents);
                result.joints.Add(new KimodoArdyJointPositionMask
                {
                    jointName = names[index],
                    position = new KimodoArdyPositionMask { x = enabled, y = enabled, z = enabled }
                });
            }
            return result;
        }

        public static KimodoArdyConstraintMask LowerBody(string modelName)
        {
            KimodoArdyConstraintMask upper = UpperBody(modelName);
            foreach (KimodoArdyJointPositionMask joint in upper.joints)
            {
                bool enabled = !(joint.position.x || joint.position.y || joint.position.z);
                joint.position = new KimodoArdyPositionMask { x = enabled, y = enabled, z = enabled };
            }
            return upper;
        }

        public static KimodoArdyConstraintMask FullBody(string modelName, bool includeRoot = false)
        {
            string[] names = GetJointNames(modelName);
            var result = new KimodoArdyConstraintMask
            {
                rootPosition = new KimodoArdyPositionMask { x = includeRoot, y = includeRoot, z = includeRoot },
                rootHeading = includeRoot
            };
            for (int index = 1; index < names.Length; index++)
            {
                result.joints.Add(new KimodoArdyJointPositionMask
                {
                    jointName = names[index],
                    position = new KimodoArdyPositionMask { x = true, y = true, z = true }
                });
            }
            return result;
        }

        private static bool IsDescendant(int joint, int ancestor, int[] parents)
        {
            for (int current = joint; current >= 0; current = parents[current])
            {
                if (current == ancestor)
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] GetJointNames(string modelName)
        {
            if (!KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                throw new InvalidOperationException($"Model '{modelName}' is not a registered ARDY rig.");
            }
            return KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
        }
    }

    [Serializable]
    public sealed class KimodoArdyClipConstraint
    {
        [NonSerialized]
        public AnimationHandleOperator animation;
        public int startFrame;
        public int endFrameExclusive;
        public KimodoArdyConstraintMask mask = new KimodoArdyConstraintMask();
    }

    public static class KimodoArdyClipConstraintProtocol
    {
        public static string SerializeFuture(string modelName, IReadOnlyList<KimodoArdyClipConstraint> clips)
        {
            return ArdyClipConstraintSerializer.SerializeFuture(modelName, clips);
        }

        public static string Append(string constraintsJson, string futureClipConstraintsJson)
        {
            var output = new JArray();
            ArdyClipConstraintSerializer.AppendJson(output, constraintsJson);
            ArdyClipConstraintSerializer.AppendJson(output, futureClipConstraintsJson);
            return output.Count > 0 ? output.ToString(Formatting.None) : string.Empty;
        }
    }

    internal sealed class KimodoMotionModelProfile
    {
        internal string ModelName;
        internal float SourceFps;
        internal int HorizonFrames;
        internal int FramesPerToken;
        internal int MaxContextFrames;
        internal int JointCount;
        internal int MaxDiffusionSteps;
        internal string MotionRepFingerprint;

        internal int MaxHistoryHandles => Mathf.Max(
            0,
            Mathf.CeilToInt((MaxContextFrames - HorizonFrames) / (float)HorizonFrames));
    }

    internal static class KimodoMotionModelProfiles
    {
        internal const string ArdyCoreModelName = "ARDY-Core-RP-20FPS-Horizon40";
        internal const string ArdyCore8ModelName = "ARDY-Core-RP-20FPS-Horizon8";
        internal const string ArdyG1ModelName = "ARDY-G1-RP-25FPS-Horizon52";
        internal const string ArdyG18ModelName = "ARDY-G1-RP-25FPS-Horizon8";

        private static readonly KimodoMotionModelProfile ArdyCore = new KimodoMotionModelProfile
        {
            ModelName = ArdyCoreModelName,
            SourceFps = 20f,
            HorizonFrames = 40,
            FramesPerToken = 4,
            MaxContextFrames = 200,
            JointCount = 27,
            MaxDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-core-rp-20fps-h40:nfpt4:motionrep-v1"
        };

        private static readonly KimodoMotionModelProfile ArdyG1 = new KimodoMotionModelProfile
        {
            ModelName = ArdyG1ModelName,
            SourceFps = 25f,
            HorizonFrames = 52,
            FramesPerToken = 4,
            MaxContextFrames = 248,
            JointCount = 34,
            MaxDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-g1-rp-25fps-h52:nfpt4:motionrep-v1"
        };

        private static readonly KimodoMotionModelProfile ArdyCore8 = new KimodoMotionModelProfile
        {
            ModelName = ArdyCore8ModelName,
            SourceFps = 20f,
            HorizonFrames = 8,
            FramesPerToken = 4,
            MaxContextFrames = 200,
            JointCount = 27,
            MaxDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-core-rp-20fps-h8:nfpt4:motionrep-v1"
        };

        private static readonly KimodoMotionModelProfile ArdyG18 = new KimodoMotionModelProfile
        {
            ModelName = ArdyG18ModelName,
            SourceFps = 25f,
            HorizonFrames = 8,
            FramesPerToken = 4,
            MaxContextFrames = 248,
            JointCount = 34,
            MaxDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-g1-rp-25fps-h8:nfpt4:motionrep-v1"
        };

        internal static bool TryGetArdy(string modelName, out KimodoMotionModelProfile profile)
        {
            string normalized = (modelName ?? string.Empty).Trim();
            if (string.Equals(normalized, ArdyCoreModelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ardy-core", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ardy-core40", StringComparison.OrdinalIgnoreCase))
            {
                profile = ArdyCore;
                return true;
            }

            if (string.Equals(normalized, ArdyCore8ModelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ardy-core8", StringComparison.OrdinalIgnoreCase))
            {
                profile = ArdyCore8;
                return true;
            }

            if (string.Equals(normalized, ArdyG1ModelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ardy-g1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ardy-g152", StringComparison.OrdinalIgnoreCase))
            {
                profile = ArdyG1;
                return true;
            }

            if (string.Equals(normalized, ArdyG18ModelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ardy-g18", StringComparison.OrdinalIgnoreCase))
            {
                profile = ArdyG18;
                return true;
            }

            profile = null;
            return false;
        }
    }

    internal static class ArdyClipConstraintSerializer
    {
        internal static string SerializeFuture(string modelName, IReadOnlyList<KimodoArdyClipConstraint> clips)
        {
            var output = new JArray();
            if (clips == null)
            {
                return string.Empty;
            }
            string[] jointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            if (!KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile) ||
                jointNames.Length != profile.JointCount)
            {
                throw new InvalidOperationException($"Model '{modelName}' is not a registered ARDY rig.");
            }
            for (int index = 0; index < clips.Count; index++)
            {
                KimodoArdyClipConstraint clip = clips[index] ?? throw new InvalidOperationException("Future clip is null.");
                AnimationHandleInfo info = clip.animation?.Info ?? throw new InvalidOperationException("Future clip has no animation Handle.");
                if (clip.animation.IsReleased || info.IsStream)
                {
                    throw new InvalidOperationException("Future clip requires an accessible static KMB Handle.");
                }
                if (!string.Equals(info.ModelName, profile.ModelName, StringComparison.Ordinal) ||
                    info.JointCount != profile.JointCount ||
                    !Mathf.Approximately(info.Fps, profile.SourceFps))
                {
                    throw new InvalidOperationException("Future clip Handle does not match the selected ARDY profile.");
                }
                int end = clip.endFrameExclusive > 0 ? clip.endFrameExclusive : info.FrameCount;
                if (clip.startFrame < 0 || end <= clip.startFrame || end > info.FrameCount)
                {
                    throw new InvalidOperationException($"Invalid future clip slice [{clip.startFrame}, {end}).");
                }
                output.Add(new JObject
                {
                    ["type"] = "clip",
                    ["format"] = "kmb_handle_v1",
                    ["handle"] = info.Handle,
                    ["start_frame"] = clip.startFrame,
                    ["end_frame_exclusive"] = end,
                    ["is_history"] = false,
                    ["mask"] = SerializeMask(clip.mask, jointNames)
                });
            }
            return output.Count > 0 ? output.ToString(Formatting.None) : string.Empty;
        }

        private static JArray SerializeMask(KimodoArdyConstraintMask value, string[] jointNames)
        {
            value ??= new KimodoArdyConstraintMask();
            var byName = new Dictionary<string, KimodoArdyPositionMask>(StringComparer.OrdinalIgnoreCase);
            foreach (KimodoArdyJointPositionMask joint in value.joints ?? new List<KimodoArdyJointPositionMask>())
            {
                if (joint == null || string.IsNullOrWhiteSpace(joint.jointName) || !Array.Exists(jointNames, name => string.Equals(name, joint.jointName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Future clip mask contains unknown ARDY joint '{joint?.jointName}'.");
                }
                byName[joint.jointName] = joint.position ?? new KimodoArdyPositionMask();
            }
            KimodoArdyPositionMask root = value.rootPosition ?? new KimodoArdyPositionMask();
            var result = new JArray(root.x, root.y, root.z, value.rootHeading);
            for (int index = 1; index < jointNames.Length; index++)
            {
                byName.TryGetValue(jointNames[index], out KimodoArdyPositionMask position);
                position ??= new KimodoArdyPositionMask();
                result.Add(position.x);
                result.Add(position.y);
                result.Add(position.z);
            }
            return result;
        }

        internal static string MergeHandles(
            IReadOnlyList<string> handles,
            int maxHandles,
            int horizonFrames,
            string futureConstraintsJson)
        {
            var output = new JArray();
            int first = handles != null ? Mathf.Max(0, handles.Count - Mathf.Max(0, maxHandles)) : 0;
            if (handles != null)
            {
                for (int i = first; i < handles.Count; i++)
                {
                    string handle = handles[i];
                    if (string.IsNullOrWhiteSpace(handle))
                    {
                        continue;
                    }

                    output.Add(new JObject
                    {
                        ["type"] = "clip",
                        ["format"] = "kmb_handle_v1",
                        ["handle"] = handle.Trim(),
                        ["start_frame"] = 0,
                        ["end_frame_exclusive"] = horizonFrames,
                        ["is_history"] = true
                    });
                }
            }

            AppendJson(output, futureConstraintsJson);
            return output.Count > 0 ? output.ToString(Formatting.None) : string.Empty;
        }

        internal static void AppendJson(JArray output, string json)
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

            throw new InvalidOperationException("constraints_json must be a JSON array or object.");
        }
    }

    internal static class ArdyUnityMotionCache
    {
        internal static string ManagedRoot
        {
            get
            {
                string root = Application.isEditor
                    ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "Kimodo", "ArdyKmb"))
                    : Path.GetFullPath(Path.Combine(Application.persistentDataPath, "Kimodo", "ArdyKmb"));
                return root;
            }
        }

        internal static string Write(byte[] payload, string label)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new InvalidOperationException("Cannot cache an empty KMB1 payload.");
            }

            string root = ManagedRoot;
            Directory.CreateDirectory(root);
            string safeLabel = string.IsNullOrWhiteSpace(label) ? "motion" : label.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                safeLabel = safeLabel.Replace(invalid, '_');
            }

            string destination = Path.Combine(root, $"{safeLabel}-{Guid.NewGuid():N}.kmb");
            string temporary = destination + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, payload);
                File.Move(temporary, destination);
                return destination;
            }
            catch
            {
                if (File.Exists(temporary))
                {
                    string archive = Path.Combine(root, "archive");
                    Directory.CreateDirectory(archive);
                    File.Move(temporary, Path.Combine(archive, Path.GetFileName(temporary) + ".incomplete"));
                }
                throw;
            }
        }
    }
}
