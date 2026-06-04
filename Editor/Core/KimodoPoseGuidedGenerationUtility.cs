using KimodoBridge;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Timeline;
using TimelineInject;

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

            string modelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim();
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

            return await KimodoBridgeController.GenerateBridgeAsync(
                launcherPath,
                resolvedModelName,
                highVram,
                runtimeRoot,
                modelsRoot,
                request,
                progress,
                token);
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
