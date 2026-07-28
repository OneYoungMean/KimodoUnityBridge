using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
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
            return BakeRuntimeResult(request, prompt, modelName, runtimeResult);
        }

        internal static KimodoEditorGenerateResult BakeRuntimeResult(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName,
            KimodoBridgeCommandResult runtimeResult)
        {
            if (request == null)
            {
                throw new InvalidOperationException("Generate request is null.");
            }
            if (runtimeResult == null)
            {
                throw new InvalidOperationException("Runtime generation returned null result.");
            }

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
            ValidateTargetLength(request);
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile))
            {
                return await ExecuteArdyRuntimePipelineAsync(request, prompt, profile);
            }

            return await ExecuteKimodoRuntimePipelineAsync(request, prompt, modelName);
        }

        private static async Task<KimodoBridgeCommandResult> ExecuteKimodoRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName)
        {
            List<int> segmentFrameCounts = PlanKimodoSegmentFrames(request.TargetFrameCount);
            var motions = new List<KimodoRawMotionData>(segmentFrameCounts.Count);
            KimodoMarkerSampleResult previousTail = null;
            KimodoBridgeCommandResult lastResult = null;
            int segmentStartFrame = 0;
            var pipeline = new KimodoBridgeCommand();
            if (segmentFrameCounts.Count > 1 &&
                (request.ConstraintSamples == null || request.ConstraintSamples.Count == 0) &&
                HasConstraints(request.ConstraintsJson))
            {
                throw new InvalidOperationException(
                    "Kimodo generation longer than 10 seconds requires sampled constraints so each segment can be remapped safely.");
            }

            for (int segmentIndex = 0; segmentIndex < segmentFrameCounts.Count; segmentIndex++)
            {
                ThrowIfCanceled(request);
                int segmentFrameCount = segmentFrameCounts[segmentIndex];
                List<KimodoMarkerSampleResult> segmentSamples = BuildKimodoSegmentConstraintSamples(
                    request.ConstraintSamples,
                    previousTail,
                    request.TargetFrameCount,
                    segmentStartFrame,
                    segmentFrameCount,
                    request.TargetFrameRate);
                string segmentConstraints = segmentSamples.Count > 0
                    ? KimodoConstraintJsonExporter.ToConstraintsJson(
                        segmentSamples,
                        0.0,
                        segmentFrameCount / request.TargetFrameRate,
                        request.TargetFrameRate)
                    : segmentFrameCounts.Count == 1
                        ? request.ConstraintsJson ?? string.Empty
                        : string.Empty;

                KimodoBridgeCommandRequest commandRequest = CreateRuntimePipelineRequest(request, prompt, modelName);
                commandRequest.GenerationRequest.duration = segmentFrameCount / request.TargetFrameRate;
                commandRequest.GenerationRequest.constraints_json = segmentConstraints;
                request.Progress?.Invoke(
                    KimodoBridgeCommandStage.InvokeBackend,
                    segmentFrameCounts.Count > 1
                        ? $"Generating Kimodo segment {segmentIndex + 1}/{segmentFrameCounts.Count}..."
                        : "Generating Kimodo motion...");
                lastResult = await pipeline.ExecuteAsync(
                    commandRequest,
                    (stage, message) => request.Progress?.Invoke(
                        stage,
                        segmentFrameCounts.Count > 1
                            ? $"[{segmentIndex + 1}/{segmentFrameCounts.Count}] {message}"
                            : message),
                    request.Token);
                if (lastResult?.MotionData == null || lastResult.MotionData.FrameCount != segmentFrameCount)
                {
                    throw new InvalidOperationException(
                        $"Kimodo segment {segmentIndex + 1} returned {lastResult?.MotionData?.FrameCount ?? 0} frames; expected {segmentFrameCount}.");
                }

                motions.Add(lastResult.MotionData);
                segmentStartFrame += segmentFrameCount;
                if (segmentIndex + 1 < segmentFrameCounts.Count &&
                    !KimodoRawMotionUtility.TryExtractTailMarkerSample(
                        lastResult.MotionData,
                        modelName,
                        out previousTail,
                        out string tailError,
                        constraintType: "fullbody",
                        sampleTime: 0.0))
                {
                    throw new InvalidOperationException($"Build Kimodo segment In Constraint failed: {tailError}");
                }
            }

            if (!KimodoRawMotionUtility.TryConcatenate(
                    motions,
                    request.TargetFrameCount,
                    out KimodoRawMotionData combined,
                    out string concatenateError))
            {
                throw new InvalidOperationException(concatenateError);
            }

            byte[] payload = KimodoRawMotionUtility.ToFlatBuffer(combined, modelName);
            return new KimodoBridgeCommandResult
            {
                MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(combined),
                MotionData = combined,
                MotionBytes = payload,
                MotionFormat = "kmb_v1",
                Message = segmentFrameCounts.Count > 1
                    ? $"Kimodo generation complete ({segmentFrameCounts.Count} segments)."
                    : lastResult?.Message ?? "Kimodo generation complete.",
                RawStatus = lastResult?.RawStatus ?? "done",
                MotionRepFingerprint = lastResult?.MotionRepFingerprint ?? string.Empty,
                ResolvedSeed = lastResult?.ResolvedSeed
            };
        }

        private static async Task<KimodoBridgeCommandResult> ExecuteArdyRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            KimodoMotionModelProfile profile)
        {
            byte[] historyPayload = BuildInitialArdyHistoryPayload(request, profile);

            KimodoBridgeCommandRequest commandRequest = CreateRuntimePipelineRequest(request, prompt, profile.ModelName);
            commandRequest.GenerationRequest.duration = request.TargetFrameCount / request.TargetFrameRate;
            commandRequest.GenerationRequest.time_as_double = 0.0;
            commandRequest.GenerationRequest.seed = request.EffectiveSeed;
            commandRequest.GenerationRequest.steps = request.DiffusionSteps <= 0
                ? profile.MaxDiffusionSteps
                : Mathf.Clamp(request.DiffusionSteps, 1, profile.MaxDiffusionSteps);
            commandRequest.GenerationRequest.ardy_history_kmb = historyPayload;
            commandRequest.GenerationRequest.ardy_playback_reserve_seconds = 0.0;
            commandRequest.GenerationRequest.ardy_adaptive_playback_reserve = false;

            request.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating complete ARDY KMB...");
            var pipeline = new KimodoBridgeCommand();
            KimodoBridgeCommandResult directResult = await pipeline.ExecuteAsync(
                commandRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
            ValidateArdyResult(directResult, profile, request.EffectiveSeed);
            request.GeneratedArdySeeds.Add(directResult.ResolvedSeed.Value);
            request.GeneratedArdyFingerprint = directResult.MotionRepFingerprint;

            KimodoRawMotionData sourceMotion = directResult.MotionData;
            byte[] sourcePayload = KimodoRawMotionUtility.ToFlatBuffer(sourceMotion, profile.ModelName);
            request.GeneratedArdyMotionCachePath = ArdyUnityMotionCache.Write(sourcePayload, "timeline-final");
            return new KimodoBridgeCommandResult
            {
                MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(sourceMotion),
                MotionData = sourceMotion,
                MotionBytes = sourcePayload,
                MotionFormat = "kmb_v1",
                Message = "ARDY generation complete.",
                RawStatus = "done",
                MotionRepFingerprint = profile.MotionRepFingerprint,
                ResolvedSeed = directResult.ResolvedSeed
            };
        }

        internal static byte[] BuildInitialArdyHistoryPayload(
            KimodoEditorGenerateRequest request,
            KimodoMotionModelProfile profile)
        {
            if (request?.InitialArdyHistorySource == null)
            {
                return null;
            }

            request.Progress?.Invoke(KimodoBridgeCommandStage.Constraint, "Sampling Timeline history to ARDY KMB1...");
            if (!ArdyEditorHistoryEncoder.TryEncode(
                    request.InitialArdyHistorySource,
                    profile,
                    out byte[] historyPayload,
                    out string error))
            {
                throw new InvalidOperationException($"Build ARDY history failed: {error}");
            }
            return historyPayload;
        }

        internal static void ValidateArdyResult(
            KimodoBridgeCommandResult result,
            KimodoMotionModelProfile profile,
            int requestedSeed)
        {
            if (result?.MotionData == null ||
                result.MotionData.FrameCount <= 0 ||
                result.MotionData.JointCount != profile.JointCount ||
                Mathf.Abs(result.MotionData.FrameRate - profile.SourceFps) > 1e-4f)
            {
                throw new InvalidOperationException("ARDY Generate did not return compatible KMB motion.");
            }
            if (!string.Equals(result.MotionRepFingerprint, profile.MotionRepFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ARDY motion representation fingerprint mismatch.");
            }
            if (!result.ResolvedSeed.HasValue || result.ResolvedSeed.Value != requestedSeed)
            {
                throw new InvalidOperationException("ARDY resolved_seed mismatch.");
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
                duration = request.TargetFrameCount / request.TargetFrameRate,
                seed = request.EffectiveSeed,
                steps = request.DiffusionSteps,
                text_weight = Mathf.Clamp(request.TextWeight, 0f, 4f),
                constraints_json = request.ConstraintsJson ?? string.Empty,
                model = modelName,
                text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(request.TextEncoderMode),
                simulate_free_vram_gb = KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental ? 0 : (int?)null,
                models_root = modelsRoot,
                force_hf_download = false,
                owner_pid = System.Diagnostics.Process.GetCurrentProcess().Id
            };

            return new KimodoBridgeCommandRequest
            {
                GenerationRequest = generationRequest
            };
        }

        internal static List<int> PlanKimodoSegmentFrames(int targetFrameCount)
        {
            int totalFrames = Mathf.Max(1, targetFrameCount);
            int segmentCount = Mathf.CeilToInt(totalFrames / (float)KimodoPlayableClip.MAX_FRAMES);
            int baseFrames = totalFrames / segmentCount;
            int remainder = totalFrames % segmentCount;
            var result = new List<int>(segmentCount);
            for (int i = 0; i < segmentCount; i++)
            {
                result.Add(baseFrames + (i < remainder ? 1 : 0));
            }
            return result;
        }

        internal static List<KimodoMarkerSampleResult> BuildKimodoSegmentConstraintSamples(
            IReadOnlyList<KimodoMarkerSampleResult> sourceSamples,
            KimodoMarkerSampleResult previousTail,
            int totalFrameCount,
            int segmentStartFrame,
            int segmentFrameCount,
            float frameRate)
        {
            float fps = frameRate > 0f ? frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
            int safeTotalFrames = Mathf.Max(1, totalFrameCount);
            int segmentEndFrame = segmentStartFrame + Mathf.Max(1, segmentFrameCount);
            var result = new List<KimodoMarkerSampleResult>();
            if (previousTail != null && segmentStartFrame > 0)
            {
                KimodoMarkerSampleResult continuity = previousTail.Clone();
                continuity.constraintType = "fullbody";
                continuity.sampleTime = 0.0;
                result.Add(continuity);
            }

            if (sourceSamples == null)
            {
                return result;
            }
            for (int i = 0; i < sourceSamples.Count; i++)
            {
                KimodoMarkerSampleResult source = sourceSamples[i];
                if (source == null)
                {
                    continue;
                }
                if (string.Equals(source.constraintType, "root2d_target", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(source.Clone());
                    continue;
                }

                int sourceFrame = Mathf.Clamp(
                    (int)Math.Floor(Math.Max(0.0, source.sampleTime) * fps + 1e-9),
                    0,
                    safeTotalFrames - 1);
                if (sourceFrame < segmentStartFrame || sourceFrame >= segmentEndFrame)
                {
                    continue;
                }

                KimodoMarkerSampleResult local = source.Clone();
                local.sampleTime = (sourceFrame - segmentStartFrame) / fps;
                result.Add(local);
            }
            return result;
        }

        private static bool HasConstraints(string constraintsJson)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson))
            {
                return false;
            }
            string value = constraintsJson.Trim();
            return !string.Equals(value, "[]", StringComparison.Ordinal) &&
                !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateTargetLength(KimodoEditorGenerateRequest request)
        {
            if (request == null ||
                request.TargetFrameCount <= 0 ||
                request.TargetFrameRate <= 0f ||
                float.IsNaN(request.TargetFrameRate) ||
                float.IsInfinity(request.TargetFrameRate))
            {
                throw new InvalidOperationException("Generation requires a positive target frame count and frame rate.");
            }
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
