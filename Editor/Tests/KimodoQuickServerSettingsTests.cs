using System;
using System.IO;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoQuickServerSettingsTests
    {
        [Test]
        public void ConfiguredDirectoryProvidesRuntimeRootAndPackageVersion()
        {
            string directory = Path.Combine(Path.GetTempPath(), "kimodo-quickserver-" + Guid.NewGuid().ToString("N"));
            string previousPath = KimodoPlayableClipGenerationSettings.instance.QuickServerPath;
            string previousOverride = KimodoServerRuntimeUtil.RuntimeRootOverrideForTests;
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(Path.Combine(directory, "package.json"), "{\"version\":\"1.3.0\"}");
                KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = null;
                KimodoPlayableClipGenerationSettings.instance.QuickServerPath = directory;

                Assert.That(KimodoServerRuntimeUtil.GetRuntimeRootPath(), Is.EqualTo(Path.GetFullPath(directory)));
                Assert.That(KimodoServerRuntimeUtil.ReadQuickServerVersion(directory), Is.EqualTo("1.3.0"));
                File.Delete(Path.Combine(directory, "package.json"));
                Assert.That(KimodoServerRuntimeUtil.ReadQuickServerVersion(directory), Is.EqualTo("unknown"));
            }
            finally
            {
                KimodoPlayableClipGenerationSettings.instance.QuickServerPath = previousPath;
                KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = previousOverride;
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void EmptyDirectoriesDisplayDefaultServerAndModelsPaths()
        {
            string previousQuickServerPath = KimodoPlayableClipGenerationSettings.instance.QuickServerPath;
            string previousModelsPath = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath;
            string previousOverride = KimodoServerRuntimeUtil.RuntimeRootOverrideForTests;
            try
            {
                KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = null;
                KimodoPlayableClipGenerationSettings.instance.QuickServerPath = string.Empty;
                KimodoPlayableClipGenerationSettings.instance.LocalModelsPath = string.Empty;

                string runtimeRoot = KimodoServerRuntimeUtil.GetRuntimeRootPath();
                Assert.That(
                    Path.GetFileName(runtimeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    Is.EqualTo("NvlabKimodoQuickServer~"));
                Assert.That(
                    KimodoServerManagerSettingsProvider.ResolveDisplayedModelsPath(string.Empty, runtimeRoot),
                    Is.EqualTo(Path.Combine(runtimeRoot, "models")));
            }
            finally
            {
                KimodoPlayableClipGenerationSettings.instance.QuickServerPath = previousQuickServerPath;
                KimodoPlayableClipGenerationSettings.instance.LocalModelsPath = previousModelsPath;
                KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = previousOverride;
            }
        }

        [Test]
        public void SetupWarmupRequestUsesConfiguredDefaults()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string previousModel = settings.DefaultBridgeModelName;
            KimodoTextEncoderMode previousEncoderMode = settings.DefaultTextEncoderMode;
            string previousModelsPath = settings.LocalModelsPath;

            try
            {
                settings.DefaultBridgeModelName = KimodoMotionModelProfiles.ArdyCoreModelName;
                settings.DefaultTextEncoderMode = KimodoTextEncoderMode.HighPrecision;
                settings.LocalModelsPath = Path.Combine(Path.GetTempPath(), "kimodo-warmup-models");

                KimodoGenerationRequestDto request =
                    KimodoSetupWizardWindow.CreateDefaultModelWarmupRequest(settings);

                Assert.That(request.model, Is.EqualTo(KimodoMotionModelProfiles.ArdyCoreModelName));
                Assert.That(request.text_encoder_mode, Is.EqualTo(KimodoTextEncoderModeProtocol.HighPrecision));
                Assert.That(request.models_root, Is.EqualTo(settings.LocalModelsPath));
                Assert.That(request.duration, Is.EqualTo(1f));
                Assert.That(request.steps, Is.EqualTo(1));
                Assert.That(request.prompt, Is.Not.Empty);
            }
            finally
            {
                settings.DefaultBridgeModelName = previousModel;
                settings.DefaultTextEncoderMode = previousEncoderMode;
                settings.LocalModelsPath = previousModelsPath;
            }
        }

        [Test]
        public void ArdyModelInstallCheckUsesArdyCheckpointFiles()
        {
            string runtimeRoot = Path.Combine(Path.GetTempPath(), "kimodo-quickserver-" + Guid.NewGuid().ToString("N"));
            string modelDir = Path.Combine(
                runtimeRoot,
                "models",
                KimodoMotionModelProfiles.ArdyCoreModelName);

            try
            {
                Directory.CreateDirectory(Path.Combine(modelDir, "stats", "motion"));
                File.WriteAllText(Path.Combine(modelDir, "config.yaml"), string.Empty);
                File.WriteAllText(Path.Combine(modelDir, "tokenizer.safetensors"), string.Empty);
                File.WriteAllText(Path.Combine(modelDir, "denoiser.safetensors"), string.Empty);
                File.WriteAllText(Path.Combine(modelDir, "stats", "motion", "mean.npy"), string.Empty);
                File.WriteAllText(Path.Combine(modelDir, "stats", "motion", "std.npy"), string.Empty);

                Assert.That(
                    KimodoServerRuntimeUtil.IsSelectedBridgeModelInstalled(
                        runtimeRoot,
                        KimodoMotionModelProfiles.ArdyCoreModelName,
                        null),
                    Is.True);

                File.Delete(Path.Combine(modelDir, "denoiser.safetensors"));
                Assert.That(
                    KimodoServerRuntimeUtil.IsSelectedBridgeModelInstalled(
                        runtimeRoot,
                        KimodoMotionModelProfiles.ArdyCoreModelName,
                        null),
                    Is.False);
            }
            finally
            {
                if (Directory.Exists(runtimeRoot))
                {
                    Directory.Delete(runtimeRoot, recursive: true);
                }
            }
        }

        [Test]
        public void ModelSetupStatusHonorsModelsRootOverride()
        {
            string runtimeRoot = Path.Combine(Path.GetTempPath(), "kimodo-quickserver-" + Guid.NewGuid().ToString("N"));
            string modelsRoot = Path.Combine(Path.GetTempPath(), "kimodo-models-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(runtimeRoot);
                Directory.CreateDirectory(Path.Combine(modelsRoot, "KIMODO-Meta3_llm2vec_FP16"));
                File.WriteAllText(Path.Combine(runtimeRoot, ".setup.complete"), "setup_profile=cuda");
                File.WriteAllText(Path.Combine(modelsRoot, "KIMODO-Meta3_llm2vec_FP16", "model.safetensors"), string.Empty);

                ModelSetupStatus status = KimodoBridgeRuntimeInstallFacade.EvaluateModelSetupStatus(
                    runtimeRoot,
                    KimodoTextEncoderMode.HighPrecision,
                    "Kimodo-SOMA-RP-v1",
                    modelsRoot);

                Assert.That(status.Missing, Is.True);
                Assert.That(status.MissingPoints, Is.EqualTo(2));
            }
            finally
            {
                if (Directory.Exists(runtimeRoot))
                {
                    Directory.Delete(runtimeRoot, recursive: true);
                }

                if (Directory.Exists(modelsRoot))
                {
                    Directory.Delete(modelsRoot, recursive: true);
                }
            }
        }

        [Test]
        public void SetupProfileFallsBackToTorchRuntime()
        {
            string runtimeRoot = Path.Combine(Path.GetTempPath(), "kimodo-quickserver-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(runtimeRoot);
                File.WriteAllText(Path.Combine(runtimeRoot, ".setup.complete"), "torch_runtime=cuda");

                Assert.That(KimodoServerRuntimeUtil.TryReadSetupProfile(runtimeRoot, out string profile), Is.True);
                Assert.That(profile, Is.EqualTo("cuda"));
            }
            finally
            {
                if (Directory.Exists(runtimeRoot))
                {
                    Directory.Delete(runtimeRoot, recursive: true);
                }
            }
        }
    }
}
