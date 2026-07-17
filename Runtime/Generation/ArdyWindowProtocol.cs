using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KimodoBridge
{
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
                        ["format"] = "ardy_handle_v1",
                        ["handle"] = handle.Trim(),
                        ["start_frame"] = 0,
                        ["end_frame_exclusive"] = horizonFrames
                    });
                }
            }

            AppendJson(output, futureConstraintsJson);
            return output.Count > 0 ? output.ToString(Formatting.None) : string.Empty;
        }

        internal static string MergeFiles(IReadOnlyList<string> paths, string futureConstraintsJson)
        {
            var output = new JArray();
            if (paths != null)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(path.Trim());
                    if (!KimodoRawMotionUtility.TryParseFlatBuffer(
                            File.ReadAllBytes(fullPath),
                            out KimodoRawMotionData motion,
                            out string error))
                    {
                        throw new InvalidOperationException($"Invalid ARDY file history '{fullPath}': {error}");
                    }

                    output.Add(new JObject
                    {
                        ["type"] = "clip",
                        ["format"] = "ardy_file_v1",
                        ["path"] = fullPath,
                        ["start_frame"] = 0,
                        ["end_frame_exclusive"] = motion.FrameCount,
                        ["duration_seconds"] = motion.DurationSeconds
                    });
                }
            }

            AppendJson(output, futureConstraintsJson);
            return output.Count > 0 ? output.ToString(Formatting.None) : string.Empty;
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
