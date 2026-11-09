using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorGeneratePipeline
    {
        public static async Task<KimodoEditorGenerationResult> ExecuteAsync(KimodoEditorGenerateRequest request)
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

            string modelName = KimodoMotionModelProfiles.NormalizeName(request.ModelName);
            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating motion...");

            KimodoGenerationResultDto runtimeResult = await ExecuteRuntimePipelineAsync(request, prompt, modelName);
            return BakeRuntimeResult(request, prompt, modelName, runtimeResult);
        }

        internal static KimodoEditorGenerationResult BakeRuntimeResult(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName,
            KimodoGenerationResultDto runtimeResult)
        {
            if (request == null)
            {
                throw new InvalidOperationException("Generate request is null.");
            }
            if (runtimeResult == null)
            {
                throw new InvalidOperationException("Runtime generation returned null result.");
            }

            string motionJson = string.IsNullOrWhiteSpace(request.BakeMotionJsonCompact)
                ? runtimeResult.motionJsonCompact
                : request.BakeMotionJsonCompact;
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new InvalidOperationException("No motion json found in runtime generation result.");
            }

            ThrowIfCanceled(request);
            request.CreateTargetClip();
            if (request.TargetClip == null)
            {
                throw new InvalidOperationException("Target clip is null.");
            }

            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.Bake, "Baking animation...");
            if (!KimodoRetargetToolsEditor.BakeIntoClip(
                    request.TargetClip,
                    motionJson,
                    KimodoMotionModelProfiles.ResolveBakeSkeletonType(modelName),
                    modelName,
                    null,
                    out string bakeError,
                    hasRealCoverageSample: !string.IsNullOrWhiteSpace(request.BakeMotionJsonCompact)))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(bakeError) ? "Bake failed." : bakeError);
            }

            ThrowIfCanceled(request);
            EditorUtility.SetDirty(request.TargetClip);

            AnimationClip rawBoneClip = KimodoEditorClipWritebackService.CreateRawBoneWritebackClip(request.TargetClip);
            request.RawBoneClip = rawBoneClip;
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                // ponytail: keep native ARDY keys; Unity samples them at the output rate.
                request.TargetClip.frameRate = KimodoMotionModelProfiles.DefaultFrameRate;
            }
            ThrowIfCanceled(request);
            KimodoEditorGenerateOutputPlan outputPlan = request.ResolveOutputPlan(modelName);
            if (outputPlan == null)
            {
                throw new InvalidOperationException("Output plan is null.");
            }
            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][RetargetAvatar] output plan: model='{modelName}', " +
                $"skipRetarget={outputPlan.SkipRetarget}, exportMuscleClip={outputPlan.ExportMuscleClip}, " +
                $"origin={KimodoRetargetToolsEditor.DescribeAvatarForDebug(outputPlan.OriginRetargetAvatar)}, " +
                $"target={KimodoRetargetToolsEditor.DescribeAvatarForDebug(outputPlan.TargetRetargetAvatar)}.");
            ThrowIfCanceled(request);

            if (outputPlan.SkipRetarget)
            {
                TryFilterGeneratedBoneClip(request.TargetClip, outputPlan.TargetRetargetAvatar, outputPlan.CurveFilterOptions);
                ApplyGeneratedFootContacts(request, runtimeResult.motionData);
                request.Progress?.Invoke(KimodoBridgeCommandStage.Retarget, "Skipping retarget: binding hierarchy already matches clip bindings.");
                return CompleteBakedOutput(request, prompt, runtimeResult, rawBoneClip);
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
                ApplyGeneratedFootContacts(request, runtimeResult.motionData);
                EditorUtility.SetDirty(request.TargetClip);
                return CompleteBakedOutput(request, prompt, runtimeResult, rawBoneClip);
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
                    out string retargetError,
                    debugLog: KimodoPlayableClipGenerationSettings.DebugLog))
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
            ApplyGeneratedFootContacts(request, runtimeResult.motionData);
            ThrowIfCanceled(request);

            return CompleteBakedOutput(request, prompt, runtimeResult, rawBoneClip);
        }

        internal static async Task<KimodoGenerationResultDto> ExecuteRuntimePipelineAsync(
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

        private static async Task<KimodoGenerationResultDto> ExecuteKimodoRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName)
        {
            KimodoGenerationRequestDto commandRequest = CreateRuntimePipelineRequest(request, prompt, modelName);
            request.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating Kimodo motion...");
            var pipeline = new KimodoBridgeCommand();
            KimodoGenerationResultDto result = await pipeline.ExecuteAsync(
                commandRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
            request.DebugRuntimeMotion = request.DebugGeneration ? result.debugMotionData : null;
            request.DebugSegmentsJson = result.debugSegmentsJson ?? string.Empty;
            request.DebugConstraintsJson = result.debugConstraintsJson ?? string.Empty;
            return PrepareFinalMotionForBaking(request, result, modelName);
        }

        private static async Task<KimodoGenerationResultDto> ExecuteArdyRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            KimodoMotionModelProfile profile)
        {
            byte[] historyPayload = BuildInitialArdyHistoryPayload(request, profile);

            KimodoGenerationRequestDto commandRequest = CreateRuntimePipelineRequest(request, prompt, profile.ModelName);
            commandRequest.time_as_double = 0.0;
            commandRequest.seed = request.EffectiveSeed;
            commandRequest.steps = KimodoMotionModelProfiles.ResolveArdyProtocolSteps(
                request.DiffusionSteps,
                profile);
            PrependArdyHistoryConstraint(commandRequest.constraints.clips, historyPayload, profile);
            commandRequest.ardy_playback_reserve_seconds = 0.0;

            request.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating complete ARDY KMB...");
            var pipeline = new KimodoBridgeCommand();
            KimodoGenerationResultDto directResult = await pipeline.ExecuteAsync(
                commandRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
            ValidateArdyResult(directResult, profile, request.EffectiveSeed);
            request.DebugRuntimeMotion = request.DebugGeneration ? directResult.debugMotionData : null;
            request.DebugSegmentsJson = directResult.debugSegmentsJson ?? string.Empty;
            request.DebugConstraintsJson = directResult.debugConstraintsJson ?? string.Empty;
            return PrepareFinalMotionForBaking(request, directResult, profile.ModelName);
        }

        internal static KimodoGenerationResultDto PrepareFinalMotionForBaking(
            KimodoEditorGenerateRequest request,
            KimodoGenerationResultDto result,
            string modelName)
        {
            int expectedFrames = request.TargetFrameCount + (request.RequestEndpointSample ? 1 : 0);
            if (result?.motionData == null || result.motionData.FrameCount != expectedFrames)
                throw new InvalidOperationException(
                    $"Backend returned {result?.motionData?.FrameCount ?? 0} final frames; expected {expectedFrames}.");
            if (!request.RequestEndpointSample)
                return result;

            // The server has already cropped context; retain its real endpoint for curve coverage.
            request.BakeMotionData = result.motionData;
            request.BakeMotionJsonCompact = string.IsNullOrWhiteSpace(result.motionJsonCompact)
                ? KimodoRawMotionUtility.ToCompactJson(result.motionData)
                : result.motionJsonCompact;
            if (!KimodoRawMotionUtility.TrySlice(
                    result.motionData, 0, request.TargetFrameCount,
                    out KimodoRawMotionData visible, out string error))
                throw new InvalidOperationException(error);
            result.motionData = visible;
            result.motionJsonCompact = KimodoRawMotionUtility.ToCompactJson(visible);
            result.analysisJson = TrimAnalysisForOutput(
                result.analysisJson, 0, request.TargetFrameRate, visible.FrameCount);
            if (result.motionBytes != null ||
                string.Equals(result.motionFormat, "kmb_v1", StringComparison.OrdinalIgnoreCase))
                result.motionBytes = KimodoRawMotionUtility.ToFlatBuffer(visible, modelName);
            if (result.endFrameExclusive > result.startFrame)
                result.endFrameExclusive = result.startFrame + visible.FrameCount;
            return result;
        }

        private static KimodoEditorGenerationResult CompleteBakedOutput(
            KimodoEditorGenerateRequest request,
            string prompt,
            KimodoGenerationResultDto runtimeResult,
            AnimationClip rawBoneClip)
        {
            KimodoEditorClipWritebackService.FlushWritebackAssets();
            if (rawBoneClip != null && string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(rawBoneClip)))
            {
                UnityEngine.Object.DestroyImmediate(rawBoneClip);
                rawBoneClip = null;
                request.RawBoneClip = null;
            }
            return Complete(
                request,
                prompt,
                runtimeResult.motionJsonCompact,
                runtimeResult.motionBytes,
                runtimeResult.startFrame,
                runtimeResult.endFrameExclusive,
                request.TargetClip,
                rawBoneClip,
                runtimeResult.analysisJson);
        }

        private static void ApplyGeneratedFootContacts(
            KimodoEditorGenerateRequest request,
            KimodoRawMotionData outputMotion)
        {
            if (request?.BakeMotionData != null && request.BakeMotionData.HasFootContacts)
            {
                KimodoFootContactTrackUtility.Apply(
                    request.TargetClip,
                    request.BakeMotionData,
                    appendCoverageHoldFrame: false);
                return;
            }

            KimodoFootContactTrackUtility.Apply(request?.TargetClip, outputMotion);
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

        internal static void PrependArdyHistoryConstraint(
            List<KimodoClipConstraint> constraints,
            byte[] payload,
            KimodoMotionModelProfile profile)
        {
            if (payload == null || payload.Length == 0)
            {
                return;
            }
            if (constraints == null)
            {
                throw new ArgumentNullException(nameof(constraints));
            }
            if (!KimodoRawMotionUtility.TryParseFlatBuffer(
                    payload,
                    out KimodoRawMotionData motion,
                    out string error))
            {
                throw new InvalidOperationException($"ARDY history KMB is invalid: {error}");
            }
            float duration = motion.FrameCount / profile.SourceFps;
            constraints.Insert(0, new KimodoClipConstraint
            {
                motionBytes = payload,
                startTime = -duration,
                duration = duration,
                mask = null
            });
        }

        internal static void ValidateArdyResult(
            KimodoGenerationResultDto result,
            KimodoMotionModelProfile profile,
            int requestedSeed)
        {
            if (result?.motionData == null ||
                result.motionData.FrameCount <= 0 ||
                result.motionData.JointCount != profile.JointCount ||
                Mathf.Abs(result.motionData.FrameRate - profile.SourceFps) > 1e-4f)
            {
                throw new InvalidOperationException("ARDY Generate did not return compatible KMB motion.");
            }
            if (!string.Equals(result.motionRepFingerprint, profile.MotionRepFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ARDY motion representation fingerprint mismatch.");
            }
            if (!result.resolvedSeed.HasValue || result.resolvedSeed.Value != requestedSeed)
            {
                throw new InvalidOperationException("ARDY resolved_seed mismatch.");
            }
        }

        internal static KimodoGenerationRequestDto CreateRuntimePipelineRequest(
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
                duration = request.EffectiveBackendRuntimeDurationSeconds,
                seed = request.EffectiveSeed,
                steps = request.DiffusionSteps,
                constraints = new KimodoConstraintPayload
                {
                    json = request.Constraints.json ?? string.Empty,
                    clips = request.Constraints.clips != null
                        ? new List<KimodoClipConstraint>(request.Constraints.clips)
                        : new List<KimodoClipConstraint>()
                },
                model = KimodoMotionModelProfiles.NormalizeName(modelName),
                text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(request.TextEncoderMode),
                simulate_free_vram_gb = KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental ? 0 : (int?)null,
                models_root = modelsRoot,
                ardy_history_weight = request.ArdyHistoryWeight,
                stage_plan_json = request.StagePlanJson,
                window_constraints_json = request.WindowConstraintsJson,
                debug_generation = request.DebugGeneration
            };

            generationRequest.raw_generate_instruction_json = request.RawGenerateInstructionJson;

            return generationRequest;
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

        private static KimodoEditorGenerationResult Complete(
            KimodoEditorGenerateRequest request,
            string prompt,
            string motionJson,
            byte[] motionBytes,
            int startFrame,
            int endFrameExclusive,
            AnimationClip generatedClip,
            AnimationClip rawBoneClip,
            string analysisJson)
        {
            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.Finalize, "Finalizing generated assets...");
            request.Progress?.Invoke(KimodoBridgeCommandStage.Completed, "Generation complete.");

            var result = new KimodoEditorGenerationResult
            {
                ConstraintsPath = string.Empty,
                Prompt = prompt,
                Seed = request.EffectiveSeed,
                MotionJsonCompact = motionJson,
                AnalysisJson = analysisJson,
                MotionBytes = motionBytes,
                StartFrame = startFrame,
                EndFrameExclusive = endFrameExclusive,
                GeneratedClip = generatedClip,
                RawBoneClip = rawBoneClip,
            };
            if (request.DebugConstraintPasses != null)
                result.DebugConstraintPasses.AddRange(request.DebugConstraintPasses);
            return result;
        }

        private static void ThrowIfCanceled(KimodoEditorGenerateRequest request)
        {
            request?.Token.ThrowIfCancellationRequested();
        }

        private static string TrimAnalysisForOutput(
            string analysisJson,
            int trimStartFrame,
            float frameRate,
            int frameCount)
        {
            if (string.IsNullOrWhiteSpace(analysisJson) || trimStartFrame < 0 || frameCount <= 0)
            {
                return analysisJson;
            }

            try
            {
                var analysis = JObject.Parse(analysisJson);
                TrimKeyframeMarkers(analysis, trimStartFrame, frameRate, frameCount);
                TrimFootContactChanges(analysis, trimStartFrame, frameCount);
                return analysis.ToString(Formatting.None);
            }
            catch
            {
                return analysisJson;
            }
        }

        private static void TrimKeyframeMarkers(JObject analysis, int trimStartFrame, float frameRate, int frameCount)
        {
            if (analysis["keyframes"] is not JArray source)
            {
                return;
            }

            var trimmed = new JArray();
            foreach (JObject item in source.OfType<JObject>())
            {
                int frame = item.Value<int?>("frame") ?? 0;
                frame -= trimStartFrame;
                if (frame < 0 || frame >= frameCount)
                {
                    continue;
                }
                item["frame"] = frame;
                if (frameRate > 0f)
                {
                    item["time"] = frame / (double)frameRate;
                }
                trimmed.Add(item);
            }
            analysis["keyframes"] = trimmed;
        }

        private static void TrimFootContactChanges(JObject analysis, int trimStartFrame, int frameCount)
        {
            if (analysis["foot_contact_changes"] is not JArray source)
            {
                return;
            }

            analysis["foot_contact_changes"] = new JArray(source.OfType<JObject>()
                .Select(item =>
                {
                    int frame = item.Value<int?>("frame") ?? 0;
                    frame -= trimStartFrame;
                    if (frame < 0 || frame >= frameCount)
                    {
                        return null;
                    }
                    JObject trimmed = (JObject)item.DeepClone();
                    trimmed["frame"] = frame;
                    trimmed["duration_frames"] = Math.Min(
                        Math.Max(0, trimmed.Value<int?>("duration_frames") ?? 0),
                        frameCount - frame);
                    return trimmed;
                })
                .Where(item => item != null)
                .OrderBy(item => item.Value<int?>("duration_frames") ?? 0)
                .ThenBy(item => item.Value<string>("foot") ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(item => item.Value<int?>("frame") ?? 0));
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
