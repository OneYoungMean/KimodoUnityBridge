using KimodoBridge;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using TimelineInject;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGeneratePipelineOrchestrator
    {
        private const string DefaultModelName = "Kimodo-SOMA-RP-v1";

        private readonly KimodoEditorConstraintProvider constraintProvider = new KimodoEditorConstraintProvider();
        private readonly KimodoEditorClipWritebackService clipWritebackService = new KimodoEditorClipWritebackService();

        private sealed class GenerateAndBakeRequest
        {
            public string Prompt;
            public string ModelName;
            public KimodoGenerationBackend GenerationBackend;
            public KimodoBridgeVramMode BridgeVramMode;
            public int GenerationFrames;
            public int DiffusionSteps;
            public int EffectiveSeed;
            public string ConstraintsJson;
            public string ConstraintsPath;
            public Avatar OriginRetargetAvatar;
            public Avatar TargetRetargetAvatar;
            public bool ExportMuscleClip;
            public GameObject DirectBindingRoot;
            public KimodoPlayableClip PlayableClip;
            public string ComfyHost = "127.0.0.1";
            public int ComfyPort = 8188;
            public Action<KimodoGeneratePipelineStage, string> Progress;
            public Action OnWritebackCompleted;
            public CancellationToken Token;
        }

        public async Task<KimodoEditorGenerateResult> ExecuteAnimatorToolGenerateAndBakeAsync(
            string prompt,
            string modelName,
            KimodoGenerationBackend generationBackend,
            KimodoBridgeVramMode bridgeVramMode,
            int generationFrames,
            int diffusionSteps,
            int effectiveSeed,
            string externalConstraintsJson,
            Avatar targetRetargetAvatar,
            GameObject previewAvatarRoot,
            Action<KimodoGeneratePipelineStage, string> progress,
            Action onWritebackCompleted,
            CancellationToken token)
        {
            if (!IsValidHumanoid(targetRetargetAvatar))
            {
                throw new InvalidOperationException("Preview retarget avatar is null/invalid/non-humanoid.");
            }

            string resolvedModelName = NormalizeModelName(modelName);
            Avatar originRetargetAvatar = ResolveOriginRetargetAvatar(resolvedModelName);
            if (!IsValidHumanoid(originRetargetAvatar))
            {
                throw new InvalidOperationException("Failed to resolve origin retarget avatar.");
            }

            string constraintsJson = externalConstraintsJson ?? string.Empty;
            return await ExecuteGenerateAndBakeCoreAsync(new GenerateAndBakeRequest
            {
                Prompt = prompt,
                ModelName = resolvedModelName,
                GenerationBackend = generationBackend,
                BridgeVramMode = bridgeVramMode,
                GenerationFrames = Mathf.Clamp(generationFrames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES),
                DiffusionSteps = Mathf.Clamp(diffusionSteps, 1, 1000),
                EffectiveSeed = effectiveSeed,
                ConstraintsJson = constraintsJson,
                ConstraintsPath = string.IsNullOrWhiteSpace(constraintsJson) ? "(none)" : "(inline-json)",
                OriginRetargetAvatar = originRetargetAvatar,
                TargetRetargetAvatar = targetRetargetAvatar,
                ExportMuscleClip = true,
                DirectBindingRoot = previewAvatarRoot,
                Progress = progress,
                OnWritebackCompleted = onWritebackCompleted,
                Token = token
            });
        }

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

            Avatar originRetargetAvatar = ResolveOriginRetargetAvatar(clip);
            Avatar targetRetargetAvatar = ResolveTargetRetargetAvatar(clip, explicitRetargetAvatar, out bool hasBindingAvatar);
            bool hasValidRetargetAvatar =
                IsValidHumanoid(originRetargetAvatar) &&
                hasBindingAvatar &&
                IsValidHumanoid(targetRetargetAvatar);

            GameObject bindingObject = constraintProvider.FindTimelineBindingObjectForAsset(clip);
            bool exportMuscleClip = hasValidRetargetAvatar && TryResolveBindingAnimatorAvatar(clip, out _);

            return await ExecuteGenerateAndBakeCoreAsync(new GenerateAndBakeRequest
            {
                Prompt = prompt,
                ModelName = NormalizeModelName(clip.bridgeModelName),
                GenerationBackend = clip.generationBackend,
                BridgeVramMode = clip.bridgeVramMode,
                GenerationFrames = Mathf.Clamp(clip.generationFrames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES),
                DiffusionSteps = Mathf.Clamp(clip.diffusionSteps, 1, 1000),
                EffectiveSeed = ResolveEffectiveSeed(clip),
                ConstraintsJson = constraintsJson,
                ConstraintsPath = constraintsPath,
                OriginRetargetAvatar = originRetargetAvatar,
                TargetRetargetAvatar = targetRetargetAvatar,
                ExportMuscleClip = exportMuscleClip,
                DirectBindingRoot = hasValidRetargetAvatar ? null : bindingObject,
                PlayableClip = clip,
                ComfyHost = clip.comfyuiIP,
                ComfyPort = clip.comfyuiPort,
                Progress = progress,
                OnWritebackCompleted = onWritebackCompleted,
                Token = token
            });
        }

        public static IReadOnlyList<KimodoConstraintMarkerBase> GetLatestConstraintMarkers()
        {
            return KimodoEditorConstraintProvider.LatestMarkers;
        }

        private async Task<KimodoEditorGenerateResult> ExecuteGenerateAndBakeCoreAsync(GenerateAndBakeRequest request)
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

            string modelName = NormalizeModelName(request.ModelName);
            string directBindingError = null;

            request.Progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Generating motion...");
            string motionJson = await GenerateMotionJsonAsync(request, prompt, modelName);
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new InvalidOperationException("No motion json found in workflow outputs.");
            }

            request.Progress?.Invoke(KimodoGeneratePipelineStage.AssetWrite, "Creating generated clip asset...");
            AnimationClip generatedClip;
            if (request.PlayableClip != null)
            {
                clipWritebackService.CreateAndAssignNewAnimationClip(request.PlayableClip);
                generatedClip = request.PlayableClip.clip;
            }
            else
            {
                generatedClip = clipWritebackService.CreateGeneratedAnimationClipAsset();
            }

            request.Progress?.Invoke(KimodoGeneratePipelineStage.Bake, "Baking animation...");
            if (request.PlayableClip != null)
            {
                if (!clipWritebackService.BakeMotionJsonToPlayableClip(request.PlayableClip, prompt, motionJson, out string bakeError))
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(bakeError) ? "Bake failed." : bakeError);
                }
            }
            else
            {
                clipWritebackService.BakeMotionJsonToClip(generatedClip, motionJson, modelName, out string bakeError);
                if (!string.IsNullOrWhiteSpace(bakeError))
                {
                    throw new InvalidOperationException(bakeError);
                }
            }

            bool canSkipRetarget =
                request.DirectBindingRoot != null &&
                KimodoEditorClipUtility.CanApplyClipDirectlyToHierarchy(generatedClip, request.DirectBindingRoot, out directBindingError);
            if (canSkipRetarget)
            {
                request.Progress?.Invoke(KimodoGeneratePipelineStage.Retarget, "Skipping retarget: binding hierarchy already matches clip bindings.");
                return Complete(request, prompt, generatedClip);
            }

            if (!IsValidHumanoid(request.OriginRetargetAvatar) || !IsValidHumanoid(request.TargetRetargetAvatar))
            {
                string fallback = string.IsNullOrWhiteSpace(directBindingError)
                    ? string.Empty
                    : $" Direct binding fallback failed: {directBindingError}";
                throw new InvalidOperationException("Retarget requires valid humanoid originAvatar and targetAvatar." + fallback);
            }

            request.Progress?.Invoke(KimodoGeneratePipelineStage.Retarget, "Retargeting...");
            if (!KimodoRetargetTools.TryRetargetNew(
                    generatedClip,
                    request.OriginRetargetAvatar,
                    request.TargetRetargetAvatar,
                    request.ExportMuscleClip,
                    out AnimationClip retargetClip,
                    out string retargetError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(retargetError)
                    ? $"Retarget failed. Direct binding fallback: {directBindingError}"
                    : retargetError);
            }

            if (retargetClip != null)
            {
                generatedClip = retargetClip;
                if (request.PlayableClip != null)
                {
                    request.PlayableClip.clip = retargetClip;
                    EditorUtility.SetDirty(request.PlayableClip);
                }

                EditorUtility.SetDirty(generatedClip);
            }

            return Complete(request, prompt, generatedClip);
        }

        private KimodoEditorGenerateResult Complete(GenerateAndBakeRequest request, string prompt, AnimationClip generatedClip)
        {
            request.Progress?.Invoke(KimodoGeneratePipelineStage.Finalize, "Finalizing generated assets...");
            if (request.PlayableClip != null)
            {
                clipWritebackService.TrimGeneratedClipsToLimit(request.PlayableClip);
            }

            request.OnWritebackCompleted?.Invoke();
            request.Progress?.Invoke(KimodoGeneratePipelineStage.Completed, "Generation complete.");

            return new KimodoEditorGenerateResult
            {
                ConstraintsPath = request.ConstraintsPath,
                Prompt = prompt,
                Seed = request.EffectiveSeed,
                GeneratedClip = generatedClip
            };
        }

        private static Avatar ResolveOriginRetargetAvatar(KimodoPlayableClip clip)
        {
            return ResolveOriginRetargetAvatar(clip != null ? clip.bridgeModelName : string.Empty);
        }

        private static Avatar ResolveOriginRetargetAvatar(string modelName)
        {
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(NormalizeModelName(modelName), out Avatar avatar, out _))
            {
                return null;
            }

            return IsValidHumanoid(avatar) ? avatar : null;
        }

        private Avatar ResolveTargetRetargetAvatar(KimodoPlayableClip clip, Avatar explicitRetargetAvatar, out bool hasBindingAvatar)
        {
            hasBindingAvatar = false;
            if (explicitRetargetAvatar != null && explicitRetargetAvatar.isValid && explicitRetargetAvatar.isHuman)
            {
                hasBindingAvatar = true;
                return explicitRetargetAvatar;
            }

            GameObject bindingObject = constraintProvider.FindTimelineBindingObjectForAsset(clip);
            if (bindingObject != null)
            {
                KimodoLocalAvatarUtility.AvatarResolveResult result = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(bindingObject);
                if (result.IsHumanoid && result.Avatar != null)
                {
                    Animator animator = bindingObject.GetComponent<Animator>();
                    hasBindingAvatar = animator != null && animator.avatar != null;
                    return result.Avatar;
                }
            }

            if (clip.CustomRetargetAvatar != null && clip.CustomRetargetAvatar.isValid && clip.CustomRetargetAvatar.isHuman)
            {
                return clip.CustomRetargetAvatar;
            }

            return null;
        }

        private bool TryResolveBindingAnimatorAvatar(KimodoPlayableClip clip, out Avatar avatar)
        {
            avatar = null;
            GameObject bindingObject = constraintProvider.FindTimelineBindingObjectForAsset(clip);
            if (bindingObject == null)
            {
                return false;
            }

            KimodoLocalAvatarUtility.AvatarResolveResult result = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(bindingObject);
            if (!result.IsHumanoid || result.Avatar == null)
            {
                return false;
            }

            if (!string.Equals(result.Source, "Animator", StringComparison.Ordinal))
            {
                return false;
            }

            avatar = result.Avatar;
            return true;
        }

        private static int ResolveEffectiveSeed(KimodoPlayableClip clip)
        {
            int effectiveSeed = clip.randomSeed
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : clip.seed;

            if (clip.randomSeed || clip.seed != effectiveSeed)
            {
                clip.seed = effectiveSeed;
                EditorUtility.SetDirty(clip);
            }

            return effectiveSeed;
        }

        private static async Task<string> GenerateMotionJsonAsync(GenerateAndBakeRequest request, string prompt, string modelName)
        {
            string kimodoRootPath = KimodoBridgeController.ResolveRuntimeRootOrThrow();
            string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(kimodoRootPath);
            bool highVram = request.BridgeVramMode == KimodoBridgeVramMode.High;
            float durationSeconds = request.GenerationFrames / KimodoPlayableClip.FIXED_FRAME_RATE;
            string modelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim();
            if (!string.IsNullOrWhiteSpace(modelsRoot))
            {
                modelsRoot = Path.GetFullPath(modelsRoot);
            }

            var generationRequest = new KimodoGenerationRequestDto
            {
                prompt = prompt,
                duration = durationSeconds,
                seed = request.EffectiveSeed,
                steps = request.DiffusionSteps,
                constraints_json = request.ConstraintsJson ?? string.Empty
            };

            KimodoBackendType backendType = request.GenerationBackend == KimodoGenerationBackend.KimodoBridge
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
                    generationRequest,
                    msg => request.Progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, msg ?? string.Empty),
                    request.Token);

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
                comfyHost = string.IsNullOrWhiteSpace(request.ComfyHost) ? "127.0.0.1" : request.ComfyHost.Trim(),
                comfyPort = request.ComfyPort,
                comfyTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                comfyWorkflowResourceName = "kimodo-unity-workflow"
            };

            var pipelineRequest = new KimodoGeneratePipelineRequest
            {
                BackendType = backendType,
                RuntimeSettings = settings,
                GenerationRequest = generationRequest
            };

            IKimodoGeneratePipeline pipeline = new KimodoGeneratePipeline();
            KimodoGeneratePipelineResult pipelineResult = await pipeline.ExecuteAsync(
                pipelineRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
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

        private static string NormalizeModelName(string modelName)
        {
            return string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName.Trim();
        }

        private static bool IsValidHumanoid(Avatar avatar)
        {
            return avatar != null && avatar.isValid && avatar.isHuman;
        }
    }
}
