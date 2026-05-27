using KimodoUnityMotionTools.Bridge;
using KimodoUnityMotionTools.Generation;
using KimodoUnityMotionTools.Generation.Pipeline;
using KimodoUnityMotionTools.ProjectEditor.Manager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor.GenerationPipeline
{
    internal sealed class KimodoEditorGeneratePipelineOrchestrator
    {
        private const float TargetFps = 30f;
        private static readonly Dictionary<int, int> LastSubmittedSeedByClip = new Dictionary<int, int>();

        private readonly KimodoEditorConstraintProvider constraintProvider = new KimodoEditorConstraintProvider();
        private readonly KimodoEditorClipWritebackService clipWritebackService = new KimodoEditorClipWritebackService();
        private readonly KimodoEditorRetargetService retargetService = new KimodoEditorRetargetService();
        public async Task<KimodoEditorGenerateResult> ExecuteGenerateAndBakeAsync(
            KimodoPlayableClip clip,
            string promptOverride,
            Action<KimodoGeneratePipelineStage, string> progress,
            Action onWritebackCompleted,
            string externalConstraintsJson,
            bool useExternalConstraints,
            Avatar explicitRetargetAvatar,
            CancellationToken token)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            string prompt = string.IsNullOrWhiteSpace(promptOverride) ? (clip.motionPrompt ?? string.Empty) : promptOverride.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new InvalidOperationException("Prompt is empty.");
            }

            if (!string.Equals(clip.motionPrompt, prompt, StringComparison.Ordinal))
            {
                clip.motionPrompt = prompt;
                EditorUtility.SetDirty(clip);
            }

            progress?.Invoke(KimodoGeneratePipelineStage.Constraint, "Building constraints...");
            string constraintsJson = useExternalConstraints
                ? (externalConstraintsJson ?? string.Empty)
                : constraintProvider.BuildConstraintsJsonOrThrow(clip);
            string constraintsPath = string.IsNullOrWhiteSpace(constraintsJson) ? "(none)" : "(inline-json)";

            int effectiveSeed = ResolveEffectiveSeed(clip);

            progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Generating motion...");
            string motionJson = await GenerateMotionJsonAsync(clip, constraintsJson, effectiveSeed, progress, token);
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new InvalidOperationException("No motion json found in workflow outputs.");
            }

            progress?.Invoke(KimodoGeneratePipelineStage.AssetWrite, "Creating generated clip asset...");
            clipWritebackService.CreateAndAssignNewAnimationClip(clip);
            clipWritebackService.ApplyMotionJsonToClip(clip, prompt, motionJson);

            progress?.Invoke(KimodoGeneratePipelineStage.Bake, "Baking animation...");
            if (!clipWritebackService.BakeCurrentMotionData(clip, out string bakeError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(bakeError) ? "Bake failed." : bakeError);
            }

            progress?.Invoke(KimodoGeneratePipelineStage.Retarget, "Retargeting...");
            TimelineClip timelineClip = constraintProvider.FindTimelineClipForAsset(clip);
            retargetService.TryRetarget(clip, timelineClip, explicitRetargetAvatar, out _);

            progress?.Invoke(KimodoGeneratePipelineStage.Finalize, "Finalizing generated assets...");
            clipWritebackService.TrimGeneratedClipsToLimit(clip);
            onWritebackCompleted?.Invoke();

            progress?.Invoke(KimodoGeneratePipelineStage.Completed, "Generation complete.");

            return new KimodoEditorGenerateResult
            {
                ConstraintsPath = constraintsPath,
                Prompt = prompt,
                Seed = effectiveSeed,
                GeneratedClip = clip.clip
            };
        }

        public static IReadOnlyList<KimodoConstraintMarkerBase> GetLatestConstraintMarkers()
        {
            return KimodoEditorConstraintProvider.LatestMarkers;
        }

        private static int ResolveEffectiveSeed(KimodoPlayableClip clip)
        {
            int clipId = clip.GetInstanceID();
            int effectiveSeed = clip.randomSeed
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : clip.seed;

            if (LastSubmittedSeedByClip.TryGetValue(clipId, out int previous) && previous == effectiveSeed)
            {
                unchecked
                {
                    effectiveSeed += 1;
                }
            }

            LastSubmittedSeedByClip[clipId] = effectiveSeed;

            if (clip.randomSeed || clip.seed != effectiveSeed)
            {
                clip.seed = effectiveSeed;
                EditorUtility.SetDirty(clip);
            }

            return effectiveSeed;
        }

        private static async Task<string> GenerateMotionJsonAsync(
            KimodoPlayableClip clip,
            string constraintsJson,
            int effectiveSeed,
            Action<KimodoGeneratePipelineStage, string> progress,
            CancellationToken token)
        {
            string kimodoRootPath = KimodoBridgeController.ResolveRuntimeRootOrThrow();
            string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(kimodoRootPath);
            string modelName = string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
            bool highVram = clip.bridgeVramMode == KimodoBridgeVramMode.High;
            float durationSeconds = clip.generationFrames / TargetFps;
            string modelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim();
            if (!string.IsNullOrWhiteSpace(modelsRoot))
            {
                modelsRoot = Path.GetFullPath(modelsRoot);
            }

            var request = new KimodoGenerationRequestDto
            {
                prompt = clip.motionPrompt,
                duration = durationSeconds,
                seed = effectiveSeed,
                steps = clip.diffusionSteps,
                constraints_json = constraintsJson ?? string.Empty
            };

            KimodoBackendType backendType = clip.generationBackend == KimodoGenerationBackend.KimodoBridge
                ? KimodoBackendType.Bridge
                : KimodoBackendType.ComfyUi;

            if (backendType == KimodoBackendType.Bridge)
            {
                KimodoGenerationResultDto bridgeResult = await KimodoBridgeController.GenerateBridgeAsync(
                    launcherPath,
                    modelName,
                    highVram,
                    kimodoRootPath,
                    modelsRoot,
                    request,
                    msg => progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, msg ?? string.Empty),
                    token);

                KimodoBridgeController.RequestServerStateRefresh(force: true);
                return bridgeResult.motionJsonCompact;
            }

            var settings = new KimodoRuntimeGenerationSettings
            {
                bridgeSettings = new BridgeRuntimeSettings
                {
                    runtimeRoot = kimodoRootPath,
                    launcherPath = launcherPath,
                    modelName = modelName,
                    highVram = highVram,
                    modelsRoot = modelsRoot,
                    startupTimeoutMs = ComputeBridgeStartupTimeoutMs(kimodoRootPath, highVram, modelName)
                },
                comfyHost = clip.comfyuiIP,
                comfyPort = clip.comfyuiPort,
                comfyTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                comfyWorkflowResourceName = "kimodo-unity-workflow"
            };

            var pipelineRequest = new KimodoGeneratePipelineRequest
            {
                BackendType = backendType,
                RuntimeSettings = settings,
                GenerationRequest = request
            };

            IKimodoGeneratePipeline pipeline = new KimodoGeneratePipeline();
            KimodoGeneratePipelineResult pipelineResult = await pipeline.ExecuteAsync(
                pipelineRequest,
                (stage, message) => progress?.Invoke(stage, message),
                token);
            return pipelineResult.MotionJsonCompact;
        }

        private static int ComputeBridgeStartupTimeoutMs(string runtimeRoot, bool highVram, string modelName)
        {
            float timeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds;
            int requestedMs = Math.Max(30000, Mathf.RoundToInt(timeoutSeconds * 1000f));
            int timeoutMs = requestedMs;

            KimodoBridgeController.ModelSetupStatus modelStatus =
                KimodoBridgeController.EvaluateModelSetupStatus(runtimeRoot, highVram, modelName, modelsRootOverride: null);
            if (modelStatus.Missing)
            {
                int minutes = modelStatus.EstimatedMinutes;
                int dynamicMs = (int)Math.Round(Math.Max(600f, minutes * 60f) * 1000f);
                timeoutMs = Math.Max(timeoutMs, dynamicMs);
            }

            return timeoutMs;
        }
    }
}
