using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorGeneratePipeline
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
            KimodoFootContactTrackUtility.Apply(request.TargetClip, runtimeResult.MotionData);

            AnimationClip rawBoneClip = CreateRawBoneWritebackClip(request.TargetClip);
            request.RawBoneClip = rawBoneClip;
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                // ponytail: keep native ARDY keys; Unity samples them at the output rate.
                request.TargetClip.frameRate = KimodoPlayableClip.FIXED_FRAME_RATE;
            }
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
                KimodoFootContactTrackUtility.Apply(request.TargetClip, runtimeResult.MotionData);
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
                KimodoFootContactTrackUtility.Apply(request.TargetClip, runtimeResult.MotionData);
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
            KimodoFootContactTrackUtility.Apply(request.TargetClip, runtimeResult.MotionData);
            KimodoEditorClipWritebackService.FlushWritebackAssets();
            ThrowIfCanceled(request);

            return Complete(request, prompt, motionJson, request.TargetClip, rawBoneClip);
        }

        internal static async Task<KimodoBridgeCommandResult> ExecuteRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName)
        {
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile))
            {
                return await ExecuteArdyRuntimePipelineAsync(request, prompt, profile);
            }

            KimodoBridgeCommandRequest pipelineRequest = CreateRuntimePipelineRequest(request, prompt, modelName);
            IKimodoGeneratePipeline pipeline = new KimodoBridgeCommand();
            return await pipeline.ExecuteAsync(
                pipelineRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
        }

        private static async Task<KimodoBridgeCommandResult> ExecuteArdyRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            KimodoMotionModelProfile profile)
        {
            var initialFiles = new List<string>();
            if (request.InitialArdyHistorySource != null)
            {
                request.Progress?.Invoke(KimodoBridgeCommandStage.Constraint, "Sampling Timeline history to ARDY KMB1...");
                if (!ArdyEditorHistoryEncoder.TryEncode(
                        request.InitialArdyHistorySource,
                        profile,
                        out string redirectedPath,
                        out string redirectError))
                {
                    throw new InvalidOperationException($"Build ARDY history failed: {redirectError}");
                }
                initialFiles.Add(redirectedPath);
            }

            int targetSourceFrames = Mathf.Max(1, (int)Math.Floor(request.DurationSeconds * profile.SourceFps + 1e-9));
            var motions = new List<KimodoRawMotionData>();
            var handles = new List<string>();
            int generatedFrames = 0;
            int windowIndex = 0;
            while (generatedFrames < targetSourceFrames)
            {
                ThrowIfCanceled(request);
                string futureConstraints = BuildArdyWindowConstraints(request, profile, generatedFrames, windowIndex);
                string constraintsJson = windowIndex == 0
                    ? ArdyClipConstraintSerializer.MergeFiles(initialFiles, futureConstraints)
                    : ArdyClipConstraintSerializer.MergeHandles(
                        handles,
                        profile.MaxHistoryHandles,
                        profile.HorizonFrames,
                        futureConstraints);
                int windowSeed = unchecked(request.EffectiveSeed + windowIndex);
                KimodoBridgeCommandRequest commandRequest = CreateRuntimePipelineRequest(
                    request,
                    prompt,
                    profile.ModelName);
                commandRequest.GenerationRequest.duration = profile.HorizonFrames / profile.SourceFps;
                commandRequest.GenerationRequest.seed = windowSeed;
                commandRequest.GenerationRequest.steps = request.DiffusionSteps <= 0
                    ? profile.MaxDiffusionSteps
                    : Mathf.Clamp(request.DiffusionSteps, 1, profile.MaxDiffusionSteps);
                commandRequest.GenerationRequest.constraints_json = constraintsJson;

                request.Progress?.Invoke(
                    KimodoBridgeCommandStage.InvokeBackend,
                    $"Generating ARDY window {windowIndex + 1}...");
                var pipeline = new KimodoBridgeCommand();
                KimodoBridgeCommandResult result = await pipeline.ExecuteAsync(
                    commandRequest,
                    (stage, message) => request.Progress?.Invoke(stage, message),
                    request.Token);
                ValidateArdyWindowResult(result, profile, windowSeed);

                motions.Add(result.MotionData);
                handles.Add(result.ClipHandle);
                request.GeneratedArdyHandles.Add(result.ClipHandle);
                request.GeneratedArdySeeds.Add(result.ResolvedSeed.Value);
                request.GeneratedArdyFingerprint = result.MotionRepFingerprint;
                request.GeneratedArdyWindowCachePaths.Add(
                    ArdyUnityMotionCache.Write(result.MotionBytes, $"window-{windowIndex:D3}"));
                generatedFrames += profile.HorizonFrames;
                windowIndex++;
            }

            if (!KimodoRawMotionUtility.TryConcatenate(
                    motions,
                    targetSourceFrames,
                    out KimodoRawMotionData sourceMotion,
                    out string concatenateError))
            {
                throw new InvalidOperationException(concatenateError);
            }
            byte[] sourcePayload = KimodoRawMotionUtility.ToFlatBuffer(sourceMotion, profile.ModelName);
            request.GeneratedArdyMotionCachePath = ArdyUnityMotionCache.Write(sourcePayload, "timeline-final");

            return new KimodoBridgeCommandResult
            {
                MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(sourceMotion),
                MotionData = sourceMotion,
                MotionBytes = sourcePayload,
                MotionFormat = "flatbuf_motion_v1",
                Message = "ARDY window generation complete.",
                RawStatus = "done",
                ClipHandle = handles[handles.Count - 1],
                MotionRepFingerprint = profile.MotionRepFingerprint,
                ResolvedSeed = request.GeneratedArdySeeds[request.GeneratedArdySeeds.Count - 1]
            };
        }

        private static string BuildArdyWindowConstraints(
            KimodoEditorGenerateRequest request,
            KimodoMotionModelProfile profile,
            int windowStartFrame,
            int windowIndex)
        {
            if (request.ConstraintSamples == null || request.ConstraintSamples.Count == 0)
            {
                return windowIndex == 0 ? request.ConstraintsJson ?? string.Empty : string.Empty;
            }

            int exclusiveEnd = windowStartFrame + profile.MaxContextFrames - profile.FramesPerToken;
            var samples = new List<TimelineInject.KimodoMarkerSampleResult>();
            for (int i = 0; i < request.ConstraintSamples.Count; i++)
            {
                TimelineInject.KimodoMarkerSampleResult source = request.ConstraintSamples[i];
                if (source == null)
                {
                    continue;
                }
                int globalFrame = Math.Max(0, (int)Math.Floor(source.sampleTime * profile.SourceFps + 1e-9));
                if (globalFrame < windowStartFrame || globalFrame >= exclusiveEnd)
                {
                    continue;
                }

                TimelineInject.KimodoMarkerSampleResult clone = source.Clone();
                clone.sampleTime = (globalFrame - windowStartFrame) / profile.SourceFps;
                samples.Add(clone);
            }
            return TimelineInject.KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                0.0,
                (profile.MaxContextFrames - profile.FramesPerToken) / profile.SourceFps,
                profile.SourceFps);
        }

        private static void ValidateArdyWindowResult(
            KimodoBridgeCommandResult result,
            KimodoMotionModelProfile profile,
            int requestedSeed)
        {
            if (result?.MotionData == null ||
                result.MotionData.FrameCount != profile.HorizonFrames ||
                result.MotionData.JointCount != profile.JointCount ||
                Mathf.Abs(result.MotionData.FrameRate - profile.SourceFps) > 1e-4f)
            {
                throw new InvalidOperationException("ARDY window returned incompatible Horizon, FPS, or rig metadata.");
            }
            if (result.MotionBytes == null || result.MotionBytes.Length == 0 || string.IsNullOrWhiteSpace(result.ClipHandle))
            {
                throw new InvalidOperationException("ARDY window did not return KMB1 bytes and a clip handle.");
            }
            if (!string.Equals(result.MotionRepFingerprint, profile.MotionRepFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ARDY window motion representation fingerprint mismatch.");
            }
            if (!result.ResolvedSeed.HasValue || result.ResolvedSeed.Value != requestedSeed)
            {
                throw new InvalidOperationException("ARDY window resolved_seed mismatch.");
            }
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

            string modelsRoot = string.IsNullOrWhiteSpace(request.ModelsRoot)
                ? string.Empty
                : System.IO.Path.GetFullPath(request.ModelsRoot.Trim());

            var generationRequest = new KimodoGenerationRequestDto
            {
                prompt = prompt ?? string.Empty,
                duration = request.DurationSeconds,
                seed = request.EffectiveSeed,
                steps = request.DiffusionSteps,
                constraints_json = request.ConstraintsJson ?? string.Empty,
                model = modelName,
                highvram = request.BridgeVramMode == KimodoBridgeVramMode.High,
                force_cpu = false,
                models_root = modelsRoot,
                force_hf_download = false,
                owner_pid = System.Diagnostics.Process.GetCurrentProcess().Id
            };

            return new KimodoBridgeCommandRequest
            {
                GenerationRequest = generationRequest
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
                RawBoneClip = rawBoneClip,
                ArdyMotionCachePath = request.GeneratedArdyMotionCachePath,
                ArdyClipHandles = new List<string>(request.GeneratedArdyHandles),
                ArdyMotionRepFingerprint = request.GeneratedArdyFingerprint,
                ArdyResolvedSeeds = new List<int>(request.GeneratedArdySeeds)
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

    }
}
