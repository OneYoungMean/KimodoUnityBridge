using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoPoseGuidedGenerationUtility
    {
        internal static async Task<KimodoGenerationResultDto> GenerateFromPromptWithOptionalBoundaryPosesAsync(
            string prompt,
            int frames,
            int steps,
            int? seed,
            string modelName,
            KimodoBridgeVramMode vramMode,
            KimodoMarkerSampleResult startPose = null,
            KimodoMarkerSampleResult endPose = null,
            Action<string> progress = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("Prompt is empty.", nameof(prompt));
            }

            int clampedFrames = Mathf.Clamp(frames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            int clampedSteps = Mathf.Clamp(steps, 1, 1000);
            string constraintsJson = BuildBoundaryFullBodyConstraintsJson(startPose, endPose, clampedFrames);

            string runtimeRoot = KimodoBridgeController.ResolveRuntimeRootOrThrow();
            string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(runtimeRoot);

            string resolvedModelName = string.IsNullOrWhiteSpace(modelName)
                ? "Kimodo-SOMA-RP-v1"
                : modelName.Trim();

            bool highVram = vramMode == KimodoBridgeVramMode.High;

            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string modelsRoot = settings != null ? settings.LocalModelsPath?.Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(modelsRoot))
            {
                modelsRoot = Path.GetFullPath(modelsRoot);
            }

            var request = new KimodoGenerationRequestDto
            {
                prompt = prompt,
                duration = clampedFrames / KimodoPlayableClip.FIXED_FRAME_RATE,
                seed = seed,
                steps = clampedSteps,
                constraints_json = constraintsJson
            };

            var pipelineRequest = new KimodoGeneratePipelineRequest
            {
                BackendType = KimodoBackendType.Bridge,
                RuntimeSettings = KimodoEditorRuntimeGeneratePipeline.BuildRuntimeSettings(
                    runtimeRoot,
                    launcherPath,
                    resolvedModelName,
                    highVram ? KimodoBridgeVramMode.High : KimodoBridgeVramMode.Low,
                    modelsRoot,
                    settings != null ? settings.GenerationTimeoutSeconds : 120f,
                    comfyHost: "127.0.0.1",
                    comfyPort: 8188),
                GenerationRequest = request
            };

            IKimodoGeneratePipeline pipeline = new KimodoGeneratePipeline();
            KimodoGeneratePipelineResult result = await pipeline.ExecuteAsync(
                pipelineRequest,
                (_, message) => progress?.Invoke(message),
                token);
            return new KimodoGenerationResultDto
            {
                backendType = result.BackendType,
                rawStatus = result.RawStatus,
                message = result.Message,
                motionJsonCompact = result.MotionJsonCompact
            };
        }

        internal static string BuildBoundaryFullBodyConstraintsJson(
            KimodoMarkerSampleResult startPose,
            KimodoMarkerSampleResult endPose,
            int totalFrames)
        {
            int clampedFrames = Mathf.Max(1, totalFrames);
            int endFrame = Mathf.Max(0, clampedFrames - 1);
            var entries = new List<(int frame, KimodoMarkerSampleResult pose)>(2);

            if (IsValidPose(startPose))
            {
                entries.Add((0, startPose));
            }

            if (IsValidPose(endPose))
            {
                entries.Add((endFrame, endPose));
            }

            if (entries.Count == 0)
            {
                return string.Empty;
            }

            entries.Sort((a, b) => a.frame.CompareTo(b.frame));

            var samples = new List<KimodoMarkerSampleResult>(entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                int frame = entries[i].frame;
                KimodoMarkerSampleResult pose = entries[i].pose;
                KimodoMarkerSampleResult sample = pose.Clone();
                sample.constraintType = "fullbody";
                sample.sampleTime = frame / KimodoPlayableClip.FIXED_FRAME_RATE;
                samples.Add(sample);
            }

            return KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                clipStartSeconds: 0.0,
                clipDurationSeconds: clampedFrames / KimodoPlayableClip.FIXED_FRAME_RATE);
        }

        private static bool IsValidPose(KimodoMarkerSampleResult pose)
        {
            return pose != null
                && pose.localAxisAngles != null
                && pose.localAxisAngles.Count > 0;
        }

    }
}
