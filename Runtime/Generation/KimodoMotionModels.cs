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
        internal bool IsArdy;
    }

    internal static class KimodoMotionModelProfiles
    {
        internal const string DefaultModelName = "Kimodo-SOMA-RP-v1";
        internal const float DefaultFrameRate = 30f;
        internal const int MinGenerationFrames = 1;
        internal const int MaxGenerationFrames = 300;
        internal const int DefaultGenerationFrames = 150;

        internal const string ArdyCoreModelName = "ARDY-Core-RP-20FPS-Horizon40";
        internal const string ArdyCore8ModelName = "ARDY-Core-RP-20FPS-Horizon8";
        internal const string ArdyG1ModelName = "ARDY-G1-RP-25FPS-Horizon52";
        internal const string ArdyG18ModelName = "ARDY-G1-RP-25FPS-Horizon8";

        private static readonly KimodoMotionModelProfile ArdyCore = CreateArdy(
            ArdyCoreModelName,
            20f,
            40,
            200,
            27,
            "ardy-core-rp-20fps-h40:nfpt4:motionrep-v1");

        private static readonly KimodoMotionModelProfile ArdyCore8 = CreateArdy(
            ArdyCore8ModelName,
            20f,
            8,
            200,
            27,
            "ardy-core-rp-20fps-h8:nfpt4:motionrep-v1");

        private static readonly KimodoMotionModelProfile ArdyG1 = CreateArdy(
            ArdyG1ModelName,
            25f,
            52,
            248,
            34,
            "ardy-g1-rp-25fps-h52:nfpt4:motionrep-v1");

        private static readonly KimodoMotionModelProfile ArdyG18 = CreateArdy(
            ArdyG18ModelName,
            25f,
            8,
            248,
            34,
            "ardy-g1-rp-25fps-h8:nfpt4:motionrep-v1");

        private static readonly KimodoMotionModelProfile[] Profiles =
        {
            CreateKimodo(DefaultModelName, 77),
            CreateKimodo("Kimodo-SOMA-RP-v1.1", 77),
            CreateKimodo("Kimodo-SMPLX-RP-v1", 22),
            CreateKimodo("Kimodo-G1-RP-v1", 34),
            CreateKimodo("Kimodo-SOMA-SEED-v1", 77),
            CreateKimodo("Kimodo-SOMA-SEED-v1.1", 77),
            CreateKimodo("Kimodo-G1-SEED-v1", 34),
            ArdyCore,
            ArdyCore8,
            ArdyG1,
            ArdyG18
        };

        internal static readonly string[] AllModelNames = BuildModelNames();

        internal static string NormalizeName(string modelName) =>
            string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName.Trim();

        internal static KimodoBakeSkeletonType ResolveBakeSkeletonType(string modelName)
        {
            string normalized = NormalizeName(modelName);
            if (normalized.IndexOf("smplx", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return KimodoBakeSkeletonType.SMPLX;
            }

            return normalized.IndexOf("g1", StringComparison.OrdinalIgnoreCase) >= 0
                ? KimodoBakeSkeletonType.G1
                : KimodoBakeSkeletonType.SOMA;
        }

        internal static bool TryGet(string modelName, out KimodoMotionModelProfile profile)
        {
            string normalized = (modelName ?? string.Empty).Trim();
            for (int i = 0; i < Profiles.Length; i++)
            {
                if (string.Equals(normalized, Profiles[i].ModelName, StringComparison.OrdinalIgnoreCase))
                {
                    profile = Profiles[i];
                    return true;
                }
            }

            return TryGetArdyAlias(normalized, out profile);
        }

        internal static bool TryGetArdy(string modelName, out KimodoMotionModelProfile profile)
        {
            if (TryGet(modelName, out profile) && profile.IsArdy)
            {
                return true;
            }

            profile = null;
            return false;
        }

        internal static float ResolveGenerationFrameRate(string modelName) =>
            Mathf.Max(1f, TryGet(modelName, out KimodoMotionModelProfile profile)
                ? profile.SourceFps
                : DefaultFrameRate);

        internal static int ClampDiffusionSteps(string modelName, int diffusionSteps) =>
            TryGet(modelName, out KimodoMotionModelProfile profile)
                ? Mathf.Clamp(diffusionSteps, profile.IsArdy ? 0 : 1, profile.MaxDiffusionSteps)
                : Mathf.Clamp(diffusionSteps, 1, 1000);

        internal static int ResolveArdyProtocolSteps(int diffusionSteps, KimodoMotionModelProfile profile)
        {
            if (profile == null)
            {
                return Mathf.Clamp(diffusionSteps, 1, 1000);
            }

            return diffusionSteps <= 0
                ? profile.MaxDiffusionSteps
                : Mathf.Clamp(diffusionSteps, 1, profile.MaxDiffusionSteps);
        }

        private static bool TryGetArdyAlias(string modelName, out KimodoMotionModelProfile profile)
        {
            if (Matches(modelName, "ardy-core", "ardy-core40"))
            {
                profile = ArdyCore;
                return true;
            }

            if (Matches(modelName, "ardy-core8"))
            {
                profile = ArdyCore8;
                return true;
            }

            if (Matches(modelName, "ardy-g1", "ardy-g152"))
            {
                profile = ArdyG1;
                return true;
            }

            if (Matches(modelName, "ardy-g18"))
            {
                profile = ArdyG18;
                return true;
            }

            profile = null;
            return false;
        }

        private static bool Matches(string modelName, params string[] aliases)
        {
            for (int i = 0; i < aliases.Length; i++)
            {
                if (string.Equals(modelName, aliases[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static KimodoMotionModelProfile CreateKimodo(string modelName, int jointCount) =>
            new KimodoMotionModelProfile
            {
                ModelName = modelName,
                SourceFps = DefaultFrameRate,
                FramesPerToken = 1,
                JointCount = jointCount,
                MaxDiffusionSteps = 1000,
                DefaultDiffusionSteps = 100
            };

        private static KimodoMotionModelProfile CreateArdy(
            string modelName,
            float sourceFps,
            int horizonFrames,
            int maxContextFrames,
            int jointCount,
            string fingerprint) =>
            new KimodoMotionModelProfile
            {
                ModelName = modelName,
                SourceFps = sourceFps,
                HorizonFrames = horizonFrames,
                FramesPerToken = 4,
                MaxContextFrames = maxContextFrames,
                JointCount = jointCount,
                MaxDiffusionSteps = 10,
                DefaultDiffusionSteps = 10,
                MotionRepFingerprint = fingerprint,
                IsArdy = true
            };

        private static string[] BuildModelNames()
        {
            var names = new string[Profiles.Length];
            for (int i = 0; i < Profiles.Length; i++)
            {
                names[i] = Profiles[i].ModelName;
            }

            return names;
        }
    }
}
