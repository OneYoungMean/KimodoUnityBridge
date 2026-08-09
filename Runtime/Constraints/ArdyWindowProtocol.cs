using System;
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

        internal static float ResolveGenerationFrameRate(string modelName) =>
            Mathf.Max(1f, TryGetArdy(modelName, out KimodoMotionModelProfile profile)
                ? profile.SourceFps
                : KimodoPlayableClip.FIXED_FRAME_RATE);

        internal static int ClampDiffusionSteps(string modelName, int diffusionSteps) =>
            TryGetArdy(modelName, out KimodoMotionModelProfile profile)
                ? Mathf.Clamp(diffusionSteps, 0, profile.MaxDiffusionSteps)
                : Mathf.Clamp(diffusionSteps, 1, 1000);

        internal static int ResolveArdyProtocolSteps(int diffusionSteps, KimodoMotionModelProfile profile)
        {
            if (profile == null) return Mathf.Clamp(diffusionSteps, 1, 1000);
            return diffusionSteps <= 0
                ? profile.MaxDiffusionSteps
                : Mathf.Clamp(diffusionSteps, 1, profile.MaxDiffusionSteps);
        }
    }

    internal static class ArdyUnityMotionCache
    {
        internal static string ManagedRoot => Application.isEditor
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "Kimodo", "ArdyKmb"))
            : Path.GetFullPath(Path.Combine(Application.persistentDataPath, "Kimodo", "ArdyKmb"));

        internal static string Write(byte[] payload, string label)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new InvalidOperationException("Cannot cache an empty KMB1 payload.");
            }
            string root = ManagedRoot;
            Directory.CreateDirectory(root);
            string safeLabel = string.IsNullOrWhiteSpace(label) ? "motion" : label.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) safeLabel = safeLabel.Replace(invalid, '_');
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
