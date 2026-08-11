using System;
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
        internal int DefaultDiffusionSteps;
        internal string MotionRepFingerprint;
        internal string Backend;
        internal bool SupportsStreaming;
        internal bool SupportsTimelineSegments;

        internal bool IsArdy => string.Equals(Backend, "ardy", StringComparison.Ordinal);
    }

    internal static class KimodoMotionModelProfiles
    {
        internal const string ArdyCoreModelName = "ARDY-Core-RP-20FPS-Horizon40";
        internal const string ArdyCore8ModelName = "ARDY-Core-RP-20FPS-Horizon8";
        internal const string ArdyG1ModelName = "ARDY-G1-RP-25FPS-Horizon52";
        internal const string ArdyG18ModelName = "ARDY-G1-RP-25FPS-Horizon8";

        private static readonly KimodoMotionModelProfile[] KimodoProfiles =
        {
            CreateKimodo("Kimodo-SOMA-RP-v1", 77),
            CreateKimodo("Kimodo-SOMA-RP-v1.1", 77),
            CreateKimodo("Kimodo-SMPLX-RP-v1", 22),
            CreateKimodo("Kimodo-G1-RP-v1", 34),
            CreateKimodo("Kimodo-SOMA-SEED-v1", 77),
            CreateKimodo("Kimodo-SOMA-SEED-v1.1", 77),
            CreateKimodo("Kimodo-G1-SEED-v1", 34)
        };

        private static readonly KimodoMotionModelProfile ArdyCore = new KimodoMotionModelProfile
        {
            ModelName = ArdyCoreModelName,
            SourceFps = 20f,
            HorizonFrames = 40,
            FramesPerToken = 4,
            MaxContextFrames = 200,
            JointCount = 27,
            MaxDiffusionSteps = 10,
            DefaultDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-core-rp-20fps-h40:nfpt4:motionrep-v1",
            Backend = "ardy",
            SupportsStreaming = true,
            SupportsTimelineSegments = true
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
            DefaultDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-g1-rp-25fps-h52:nfpt4:motionrep-v1",
            Backend = "ardy",
            SupportsStreaming = true,
            SupportsTimelineSegments = true
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
            DefaultDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-core-rp-20fps-h8:nfpt4:motionrep-v1",
            Backend = "ardy",
            SupportsStreaming = true,
            SupportsTimelineSegments = true
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
            DefaultDiffusionSteps = 10,
            MotionRepFingerprint = "ardy-g1-rp-25fps-h8:nfpt4:motionrep-v1",
            Backend = "ardy",
            SupportsStreaming = true,
            SupportsTimelineSegments = true
        };

        internal static readonly string[] AllModelNames =
        {
            "Kimodo-SOMA-RP-v1",
            "Kimodo-SOMA-RP-v1.1",
            "Kimodo-SMPLX-RP-v1",
            "Kimodo-G1-RP-v1",
            "Kimodo-SOMA-SEED-v1",
            "Kimodo-SOMA-SEED-v1.1",
            "Kimodo-G1-SEED-v1",
            ArdyCoreModelName,
            ArdyCore8ModelName,
            ArdyG1ModelName,
            ArdyG18ModelName
        };

        internal static bool TryGet(string modelName, out KimodoMotionModelProfile profile)
        {
            if (TryGetArdy(modelName, out profile)) return true;
            string normalized = (modelName ?? string.Empty).Trim();
            for (int i = 0; i < KimodoProfiles.Length; i++)
            {
                if (string.Equals(normalized, KimodoProfiles[i].ModelName, StringComparison.OrdinalIgnoreCase))
                {
                    profile = KimodoProfiles[i];
                    return true;
                }
            }
            profile = null;
            return false;
        }

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
            Mathf.Max(1f, TryGet(modelName, out KimodoMotionModelProfile profile)
                ? profile.SourceFps
                : KimodoPlayableClip.FIXED_FRAME_RATE);

        internal static int ClampDiffusionSteps(string modelName, int diffusionSteps) =>
            TryGet(modelName, out KimodoMotionModelProfile profile)
                ? Mathf.Clamp(diffusionSteps, profile.IsArdy ? 0 : 1, profile.MaxDiffusionSteps)
                : Mathf.Clamp(diffusionSteps, 1, 1000);

        internal static int ResolveArdyProtocolSteps(int diffusionSteps, KimodoMotionModelProfile profile)
        {
            if (profile == null) return Mathf.Clamp(diffusionSteps, 1, 1000);
            return diffusionSteps <= 0
                ? profile.MaxDiffusionSteps
                : Mathf.Clamp(diffusionSteps, 1, profile.MaxDiffusionSteps);
        }

        private static KimodoMotionModelProfile CreateKimodo(string modelName, int jointCount)
        {
            return new KimodoMotionModelProfile
            {
                ModelName = modelName,
                SourceFps = KimodoPlayableClip.FIXED_FRAME_RATE,
                FramesPerToken = 1,
                JointCount = jointCount,
                MaxDiffusionSteps = 1000,
                DefaultDiffusionSteps = 100,
                Backend = "kimodo",
                SupportsTimelineSegments = true
            };
        }
    }
}
