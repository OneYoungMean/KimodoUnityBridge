using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Process = System.Diagnostics.Process;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorRuntimeGeneratePipeline
    {
        private const string DefaultModelName = "Kimodo-SOMA-RP-v1";

        public static async Task<KimodoEditorGenerateResult> ExecuteAsync(KimodoEditorGenerateRequest request)
        {
            if (request == null)
            {
                throw new InvalidOperationException("Generate request is null.");
            }

            string prompt = request.Prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new InvalidOperationException("Prompt is empty.");
            }

            string modelName = string.IsNullOrWhiteSpace(request.ModelName) ? DefaultModelName : request.ModelName.Trim();
            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating motion...");

            KimodoBridgeCommandResult runtimeResult = await ExecuteRuntimePipelineAsync(request, prompt, modelName);
            string motionJson = runtimeResult.MotionJsonCompact;
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new InvalidOperationException("No motion json found in runtime generation result.");
            }

            ThrowIfCanceled(request);
            CreateTargetClip(request);
            if (request.TargetClip == null)
            {
                throw new InvalidOperationException("Target clip is null.");
            }

            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.Bake, "Baking animation...");
            if (!KimodoRetargetToolsEditor.BakeIntoClip(
                    request.TargetClip,
                    motionJson,
                    KimodoPlayableClip.ResolveBakeSkeletonTypeFromModelName(modelName),
                    modelName,
                    null,
                    out string bakeError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(bakeError) ? "Bake failed." : bakeError);
            }

            ThrowIfCanceled(request);
            EditorUtility.SetDirty(request.TargetClip);

            AnimationClip rawBoneClip = CreateRawBoneWritebackClip(request.TargetClip);
            request.RawBoneClip = rawBoneClip;
            ThrowIfCanceled(request);
            KimodoEditorGenerateOutputPlan outputPlan = ResolveOutputPlan(request, modelName);
            if (outputPlan == null)
            {
                throw new InvalidOperationException("Output plan is null.");
            }
            ThrowIfCanceled(request);

            if (outputPlan.SkipRetarget)
            {
                TryFilterGeneratedBoneClip(request.TargetClip, outputPlan.TargetRetargetAvatar, outputPlan.CurveFilterOptions);
                KimodoEditorClipWritebackService.FlushWritebackAssets();
                request.Progress?.Invoke(KimodoBridgeCommandStage.Retarget, "Skipping retarget: binding hierarchy already matches clip bindings.");
                return Complete(request, prompt, motionJson, request.TargetClip, rawBoneClip);
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(outputPlan.OriginRetargetAvatar))
            {
                throw new InvalidOperationException("Retarget requires a valid humanoid origin avatar.");
            }

            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.Retarget, "Retargeting...");
            if (!KimodoRetargetToolsEditor.TryBakeMuscleClipToClip(
                    request.TargetClip,
                    outputPlan.OriginRetargetAvatar,
                    request.TargetClip,
                    out string muscleCacheError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(muscleCacheError)
                    ? "Build muscle clip cache failed."
                    : muscleCacheError);
            }

            if (outputPlan.ExportMuscleClip)
            {
                request.TargetClip.EnsureQuaternionContinuity();
                EditorUtility.SetDirty(request.TargetClip);
                KimodoEditorClipWritebackService.FlushWritebackAssets();
                return Complete(request, prompt, motionJson, request.TargetClip, rawBoneClip);
            }

            ThrowIfCanceled(request);
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(outputPlan.TargetRetargetAvatar))
            {
                throw new InvalidOperationException("Retarget requires a valid humanoid target avatar.");
            }

            ThrowIfCanceled(request);
            if (!KimodoRetargetCoreUtility.TryRetargetClip(
                    request.TargetClip,
                    outputPlan.OriginRetargetAvatar,
                    outputPlan.TargetRetargetAvatar,
                    outputPlan.ExportMuscleClip,
                    providedSourceHumanoidClip: request.TargetClip,
                    out AnimationClip retargetClip,
                    out string retargetError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(retargetError)
                    ? "Retarget failed."
                    : retargetError);
            }

            if (retargetClip != null)
            {
                request.TargetClip = retargetClip;
                EditorUtility.SetDirty(retargetClip);
            }

            ThrowIfCanceled(request);
            TryFilterGeneratedBoneClip(request.TargetClip, outputPlan.TargetRetargetAvatar, outputPlan.CurveFilterOptions);
            KimodoEditorClipWritebackService.FlushWritebackAssets();
            ThrowIfCanceled(request);

            return Complete(request, prompt, motionJson, request.TargetClip, rawBoneClip);
        }

        internal static async Task<KimodoBridgeCommandResult> ExecuteRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName)
        {
            KimodoBridgeCommandRequest pipelineRequest = CreateRuntimePipelineRequest(request, prompt, modelName);
            IKimodoGeneratePipeline pipeline = new KimodoBridgeCommand();
            return await pipeline.ExecuteAsync(
                pipelineRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
        }

        internal static KimodoBridgeCommandRequest CreateRuntimePipelineRequest(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string kimodoRootPath = KimodoBridgePipeline.ResolveRuntimeRootOrThrow();
            string launcherPath = KimodoBridgePipeline.ResolveStartScriptOrThrow(kimodoRootPath);
            string modelsRoot = string.IsNullOrWhiteSpace(request.ModelsRoot) ? string.Empty : Path.GetFullPath(request.ModelsRoot.Trim());

            var generationRequest = new KimodoGenerationRequestDto
            {
                prompt = prompt ?? string.Empty,
                duration = request.DurationSeconds,
                seed = request.EffectiveSeed,
                steps = request.DiffusionSteps,
                constraints_json = request.ConstraintsJson ?? string.Empty
            };

            return new KimodoBridgeCommandRequest
            {
                RuntimeSettings = BuildRuntimeSettings(
                    kimodoRootPath,
                    launcherPath,
                    modelName,
                    request.BridgeVramMode,
                    modelsRoot,
                    request.GenerationTimeoutSeconds),
                GenerationRequest = generationRequest
            };
        }

        internal static KimodoRuntimeGenerationSettings BuildRuntimeSettings(
            string kimodoRootPath,
            string launcherPath,
            string modelName,
            KimodoBridgeVramMode bridgeVramMode,
            string modelsRoot,
            float generationTimeoutSeconds)
        {
            bool highVram = bridgeVramMode == KimodoBridgeVramMode.High;
            return new KimodoRuntimeGenerationSettings
            {
                bridgeSettings = new BridgeRuntimeSettings
                {
                    runtimeRoot = kimodoRootPath,
                    launcherPath = launcherPath,
                    modelName = modelName,
                    highVram = highVram,
                    modelsRoot = modelsRoot,
                    ownerProcessId = Process.GetCurrentProcess().Id,
                    startupTimeoutMs = ComputeBridgeStartupTimeoutMs(kimodoRootPath, highVram, modelName, generationTimeoutSeconds),
                    preserveProcessOnCancellation = KimodoPlayableClipGenerationSettings.instance != null &&
                        KimodoPlayableClipGenerationSettings.instance.AlwaysKeepServerExperimental,
                    idleTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance != null
                        ? KimodoPlayableClipGenerationSettings.instance.ServerIdleShutdownSeconds
                        : 0
                }
            };
        }

        private static KimodoEditorGenerateResult Complete(
            KimodoEditorGenerateRequest request,
            string prompt,
            string motionJson,
            AnimationClip generatedClip,
            AnimationClip rawBoneClip)
        {
            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.Finalize, "Finalizing generated assets...");
            request.Progress?.Invoke(KimodoBridgeCommandStage.Completed, "Generation complete.");

            return new KimodoEditorGenerateResult
            {
                ConstraintsPath = string.Empty,
                Prompt = prompt,
                Seed = request.EffectiveSeed,
                MotionJsonCompact = motionJson,
                GeneratedClip = generatedClip,
                RawBoneClip = rawBoneClip
            };
        }

        private static void ThrowIfCanceled(KimodoEditorGenerateRequest request)
        {
            request?.Token.ThrowIfCancellationRequested();
        }

        private static void CreateTargetClip(KimodoEditorGenerateRequest request)
        {
            if (request == null || request.CreateTargetClip == null)
            {
                return;
            }

            AnimationClip clip = request.CreateTargetClip();
            request.CreateTargetClip = null;
            if (clip == null)
            {
                throw new InvalidOperationException("Created target clip is null.");
            }

            request.TargetClip = clip;
        }

        private static KimodoEditorGenerateOutputPlan ResolveOutputPlan(KimodoEditorGenerateRequest request, string modelName)
        {
            if (request == null || request.ResolveOutputPlan == null)
            {
                return request != null ? request.OutputPlan : null;
            }

            KimodoEditorGenerateOutputPlan plan = request.ResolveOutputPlan(request.TargetClip, modelName);
            request.ResolveOutputPlan = null;
            if (plan == null)
            {
                throw new InvalidOperationException("Output plan is null.");
            }

            request.OutputPlan = plan;
            return plan;
        }

        private static AnimationClip CreateRawBoneWritebackClip(AnimationClip sourceClip)
        {
            if (sourceClip == null)
            {
                return null;
            }

            string sourceName = string.IsNullOrWhiteSpace(sourceClip.name) ? "KimodoRawBone" : sourceClip.name.Trim();
            AnimationClip rawBoneClip = KimodoEditorClipWritebackService.CreateGeneratedCacheAnimationClipAsset($"{sourceName}_RawBone");
            KimodoEditorClipUtility.CopyClipData(sourceClip, rawBoneClip, forceNoLoopKeepY: true);
            rawBoneClip.legacy = sourceClip.legacy;
            rawBoneClip.frameRate = sourceClip.frameRate;
            EditorUtility.SetDirty(rawBoneClip);
            Debug.Log($"[Kimodo][Generate] Wrote raw Kimodo bone clip: '{AssetDatabase.GetAssetPath(rawBoneClip)}'.");
            return rawBoneClip;
        }

        private static void TryFilterGeneratedBoneClip(
            AnimationClip clip,
            Avatar samplerAvatar,
            KimodoCurveFilterOptions options)
        {
            if (clip == null || options == null || !options.enabled)
            {
                return;
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(samplerAvatar))
            {
                return;
            }

            if (!KimodoRetargetToolsEditor.TryFilterClipInPlace(clip, samplerAvatar, options, out string filterError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(filterError)
                    ? "Curve filter failed."
                    : filterError);
            }

            EditorUtility.SetDirty(clip);
        }

        private static int ComputeBridgeStartupTimeoutMs(string runtimeRoot, bool highVram, string modelName, float generationTimeoutSeconds)
        {
            int requestedMs = Math.Max(30000, Mathf.RoundToInt(generationTimeoutSeconds * 1000f));
            int timeoutMs = requestedMs;

            ModelSetupStatus modelStatus =
                KimodoBridgePipeline.EvaluateModelSetupStatus(runtimeRoot, highVram, modelName, modelsRootOverride: null);
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
