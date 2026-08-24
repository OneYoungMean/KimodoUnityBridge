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
using UnityEngine.Playables;
using UnityEngine.Timeline;

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

            KimodoBridgeCommandResult runtimeResult = await ExecuteRuntimePipelineAsync(request, prompt, modelName);
            return BakeRuntimeResult(request, prompt, modelName, runtimeResult);
        }

        internal static KimodoEditorGenerationResult BakeRuntimeResult(
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
                    out string bakeError))
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
                KimodoFootContactTrackUtility.Apply(request.TargetClip, runtimeResult.MotionData);
                request.Progress?.Invoke(KimodoBridgeCommandStage.Retarget, "Skipping retarget: binding hierarchy already matches clip bindings.");
                return CompleteBakedOutput(request, prompt, modelName, runtimeResult, outputPlan, rawBoneClip);
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(outputPlan.OriginRetargetAvatar))
            {
                throw new InvalidOperationException("Retarget requires a valid humanoid origin avatar.");
            }

            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.Retarget, "Retargeting...");
            ResolveTimelineTrackOffset(
                request,
                outputPlan,
                out Vector3 trackOffsetWorld,
                out float targetHumanScale);
            if (!KimodoRetargetToolsEditor.TryBakeMuscleClipToClip(
                    request.TargetClip,
                    outputPlan.OriginRetargetAvatar,
                    request.TargetClip,
                    trackOffsetWorld,
                    targetHumanScale,
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
                return CompleteBakedOutput(request, prompt, modelName, runtimeResult, outputPlan, rawBoneClip);
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
            KimodoFootContactTrackUtility.Apply(request.TargetClip, runtimeResult.MotionData);
            ThrowIfCanceled(request);

            return CompleteBakedOutput(request, prompt, modelName, runtimeResult, outputPlan, rawBoneClip);
        }

        private static void ResolveTimelineTrackOffset(
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerateOutputPlan outputPlan,
            out Vector3 trackOffsetWorld,
            out float targetHumanScale)
        {
            trackOffsetWorld = Vector3.zero;
            targetHumanScale = 1f;

            TrackAsset track = request?.TimelineClipSnapshot?.GetParentTrack();
            if (track != null)
            {
                Animator animator = null;
                PlayableDirector director = request.TimelineDirectorSnapshot;
                if (director != null)
                {
                    UnityEngine.Object binding = director.GetGenericBinding(track);
                    animator = binding as Animator ??
                        (binding as GameObject)?.GetComponentInChildren<Animator>(true);
                }

                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track,
                    animator,
                    out trackOffsetWorld,
                    out _);
            }

            if (trackOffsetWorld.sqrMagnitude > 1e-8f)
            {
                targetHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(
                    outputPlan?.TargetRetargetAvatar);
            }
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
            KimodoBridgeCommandRequest commandRequest = CreateRuntimePipelineRequest(request, prompt, modelName);
            request.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating Kimodo motion...");
            var pipeline = new KimodoBridgeCommand();
            KimodoBridgeCommandResult result = await pipeline.ExecuteAsync(
                commandRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
            if (result?.MotionData == null || result.MotionData.FrameCount != request.EffectiveRuntimeFrameCount)
            {
                throw new InvalidOperationException(
                    $"Kimodo returned {result?.MotionData?.FrameCount ?? 0} frames; expected {request.EffectiveRuntimeFrameCount}.");
            }
            return TrimRuntimeResultForOutput(request, result, modelName);
        }

        private static async Task<KimodoBridgeCommandResult> ExecuteArdyRuntimePipelineAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            KimodoMotionModelProfile profile)
        {
            byte[] historyPayload = BuildInitialArdyHistoryPayload(request, profile);

            KimodoBridgeCommandRequest commandRequest = CreateRuntimePipelineRequest(request, prompt, profile.ModelName);
            commandRequest.GenerationRequest.time_as_double = 0.0;
            commandRequest.GenerationRequest.seed = request.EffectiveSeed;
            commandRequest.GenerationRequest.steps = KimodoMotionModelProfiles.ResolveArdyProtocolSteps(
                request.DiffusionSteps,
                profile);
            PrependArdyHistoryConstraint(commandRequest.GenerationRequest.constraints.clips, historyPayload, profile);
            commandRequest.GenerationRequest.ardy_playback_reserve_seconds = 0.0;

            request.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating complete ARDY KMB...");
            var pipeline = new KimodoBridgeCommand();
            KimodoBridgeCommandResult directResult = await pipeline.ExecuteAsync(
                commandRequest,
                (stage, message) => request.Progress?.Invoke(stage, message),
                request.Token);
            ValidateArdyResult(directResult, profile, request.EffectiveSeed);
            KimodoRawMotionData sourceMotion = directResult.MotionData;
            byte[] sourcePayload = KimodoRawMotionUtility.ToFlatBuffer(sourceMotion, profile.ModelName);
            return new KimodoBridgeCommandResult
            {
                MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(sourceMotion),
                MotionData = sourceMotion,
                MotionBytes = sourcePayload,
                MotionFormat = "kmb_v1",
                Message = "ARDY generation complete.",
                RawStatus = "done",
                MotionRepFingerprint = profile.MotionRepFingerprint,
                ResolvedSeed = directResult.ResolvedSeed,
                AnalysisJson = directResult.AnalysisJson
            };
        }

        internal static KimodoBridgeCommandResult TrimRuntimeResultForOutput(
            KimodoEditorGenerateRequest request,
            KimodoBridgeCommandResult result,
            string modelName)
        {
            if (request == null || result == null || request.RuntimeTrimStartFrame <= 0)
            {
                return result;
            }

            if (!KimodoRawMotionUtility.TrySlice(
                    result.MotionData,
                    request.RuntimeTrimStartFrame,
                    request.TargetFrameCount,
                    out KimodoRawMotionData trimmed,
                    out string trimError))
            {
                throw new InvalidOperationException(trimError);
            }

            result.MotionData = trimmed;
            result.MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(trimmed);
            result.AnalysisJson = TrimAnalysisForOutput(
                result.AnalysisJson,
                request.RuntimeTrimStartFrame,
                request.TargetFrameRate,
                trimmed.FrameCount);
            if (result.MotionBytes != null ||
                string.Equals(result.MotionFormat, "kmb_v1", StringComparison.OrdinalIgnoreCase))
            {
                result.MotionBytes = KimodoRawMotionUtility.ToFlatBuffer(trimmed, modelName);
            }
            if (result.EndFrameExclusive > result.StartFrame)
            {
                result.StartFrame += request.RuntimeTrimStartFrame;
                result.EndFrameExclusive = result.StartFrame + trimmed.FrameCount;
            }
            return result;
        }

        private static KimodoEditorGenerationResult CompleteBakedOutput(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName,
            KimodoBridgeCommandResult runtimeResult,
            KimodoEditorGenerateOutputPlan outputPlan,
            AnimationClip rawBoneClip)
        {
            FinalizeArdyLeadingGuardOutput(
                request,
                modelName,
                runtimeResult,
                outputPlan,
                rawBoneClip);
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
                runtimeResult.MotionJsonCompact,
                runtimeResult.MotionBytes,
                runtimeResult.StartFrame,
                runtimeResult.EndFrameExclusive,
                request.TargetClip,
                rawBoneClip,
                runtimeResult.AnalysisJson);
        }

        private static void FinalizeArdyLeadingGuardOutput(
            KimodoEditorGenerateRequest request,
            string modelName,
            KimodoBridgeCommandResult runtimeResult,
            KimodoEditorGenerateOutputPlan outputPlan,
            AnimationClip rawBoneClip)
        {
            if (request.RuntimeTrimStartFrame <= 0 ||
                !KimodoMotionModelProfiles.TryGetArdy(modelName, out _))
            {
                return;
            }

            Avatar samplingAvatar = outputPlan?.TargetRetargetAvatar;
            if (outputPlan != null && outputPlan.SkipRetarget)
            {
                samplingAvatar = outputPlan.OriginRetargetAvatar;
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(samplingAvatar))
            {
                samplingAvatar = outputPlan?.OriginRetargetAvatar;
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(samplingAvatar))
            {
                throw new InvalidOperationException("ARDY guard trim requires a valid final sampling Avatar.");
            }
            if (!TryTrimRetargetedClipForOutput(
                    request.TargetClip,
                    samplingAvatar,
                    outputPlan.ExportMuscleClip,
                    request.RuntimeTrimStartFrame,
                    request.TargetFrameCount,
                    request.TargetFrameRate,
                    out string clipTrimError))
            {
                throw new InvalidOperationException($"Trim retargeted ARDY guard failed: {clipTrimError}");
            }

            TrimRuntimeResultForOutput(request, runtimeResult, modelName);
            KimodoFootContactTrackUtility.Apply(request.TargetClip, runtimeResult.MotionData);
            EditorUtility.SetDirty(request.TargetClip);

            if (rawBoneClip != null && !ReferenceEquals(rawBoneClip, request.TargetClip))
            {
                if (!KimodoRetargetToolsEditor.BakeIntoClip(
                        rawBoneClip,
                        runtimeResult.MotionJsonCompact,
                        KimodoMotionModelProfiles.ResolveBakeSkeletonType(modelName),
                        modelName,
                        null,
                        out string rawTrimError))
                {
                    throw new InvalidOperationException($"Trim raw ARDY guard failed: {rawTrimError}");
                }
                EditorUtility.SetDirty(rawBoneClip);
            }

            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][ArdyGuard] trimmed final-target leading guard: " +
                $"targetAvatar='{samplingAvatar.name}', visibleFrames={runtimeResult.MotionData.FrameCount}.");
        }

        internal static bool TryTrimRetargetedClipForOutput(
            AnimationClip clip,
            Avatar samplingAvatar,
            bool exportMuscleClip,
            int trimStartFrame,
            int targetFrameCount,
            float sourceFrameRate,
            out string error)
        {
            error = string.Empty;
            if (clip == null ||
                !KimodoRetargetCoreUtility.IsValidHumanoid(samplingAvatar) ||
                trimStartFrame <= 0 ||
                targetFrameCount <= 0 ||
                sourceFrameRate <= 0f ||
                float.IsNaN(sourceFrameRate) ||
                float.IsInfinity(sourceFrameRate))
            {
                error = "Retargeted clip trim inputs are invalid.";
                return false;
            }

            RetargetSkeleton cache = null;
            AnimationClip trimmedClip = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        samplingAvatar,
                        "KimodoArdyRetargetedGuardTrim",
                        out cache,
                        out error))
                {
                    return false;
                }

                float startTime = trimStartFrame / sourceFrameRate;
                if (exportMuscleClip)
                {
                    if (!TryCollectMuscleSamplesFromClipRange(
                            clip,
                            cache,
                            targetFrameCount,
                            startTime,
                            sourceFrameRate,
                            KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                            out MuscleSample[] samples,
                            out error) ||
                        !KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                            samples,
                            sourceFrameRate,
                            out trimmedClip,
                            out error))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!TryCollectBoneSamplesFromClipRange(
                            clip,
                            cache,
                            targetFrameCount,
                            startTime,
                            sourceFrameRate,
                            KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(clip),
                            out BoneSample[] samples,
                            out error) ||
                        !KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                            samples,
                            sourceFrameRate,
                            out trimmedClip,
                            out error))
                    {
                        return false;
                    }
                }

                float outputFrameRate = clip.frameRate;
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                KimodoEditorClipUtility.CopyClipData(trimmedClip, clip, forceNoLoopKeepY: false);
                clip.frameRate = outputFrameRate > 0f ? outputFrameRate : sourceFrameRate;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                clip.EnsureQuaternionContinuity();
                return true;
            }
            finally
            {
                cache?.Dispose();
                if (trimmedClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(trimmedClip);
                }
            }
        }

        private static bool TryCollectBoneSamplesFromClipRange(
            AnimationClip clip,
            RetargetSkeleton cache,
            int frameCount,
            float sampleStartTime,
            float sampleFrameRate,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            out BoneSample[] samples,
            out string error)
        {
            samples = null;
            if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                    clip,
                    cache,
                    "KimodoArdyGuardBoneRange",
                    samplingMode,
                    out KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
                    out error))
            {
                return false;
            }

            try
            {
                samples = new BoneSample[frameCount];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float sampleTime = sampleStartTime + frame / sampleFrameRate;
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                            context,
                            sampleTime,
                            out error))
                    {
                        samples = null;
                        return false;
                    }
                    samples[frame] = KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
                }
                return true;
            }
            finally
            {
                context.Dispose();
            }
        }

        private static bool TryCollectMuscleSamplesFromClipRange(
            AnimationClip clip,
            RetargetSkeleton cache,
            int frameCount,
            float sampleStartTime,
            float sampleFrameRate,
            KimodoRetargetClipSamplingUtility.ClipSamplingMode samplingMode,
            out MuscleSample[] samples,
            out string error)
        {
            samples = null;
            if (!KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                    clip,
                    cache,
                    "KimodoArdyGuardMuscleRange",
                    samplingMode,
                    out KimodoRetargetClipSamplingUtility.ClipSamplingContext context,
                    out error))
            {
                return false;
            }

            try
            {
                samples = new MuscleSample[frameCount];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float sampleTime = sampleStartTime + frame / sampleFrameRate;
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                            context,
                            sampleTime,
                            out error) ||
                        !KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                            cache,
                            out samples[frame],
                            out error))
                    {
                        samples = null;
                        return false;
                    }
                }
                return true;
            }
            finally
            {
                context.Dispose();
            }
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
                duration = request.EffectiveRuntimeDurationSeconds,
                seed = request.EffectiveSeed,
                steps = request.DiffusionSteps,
                constraints = new KimodoConstraintPayload
                {
                    json = request.Constraints.json ?? string.Empty,
                    clips = request.Constraints.clips != null
                        ? new List<KimodoClipConstraint>(request.Constraints.clips)
                        : new List<KimodoClipConstraint>()
                },
                analysis_option_json = request.AnalysisOptionsJson ?? string.Empty,
                model = KimodoMotionModelProfiles.NormalizeName(modelName),
                text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(request.TextEncoderMode),
                simulate_free_vram_gb = KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental ? 0 : (int?)null,
                models_root = modelsRoot,
                ardy_history_weight = request.ArdyHistoryWeight,
                ardy_max_speed = request.ArdyMaxSpeed,
                ardy_max_acceleration = request.ArdyMaxAcceleration
            };

            return new KimodoBridgeCommandRequest
            {
                GenerationRequest = generationRequest
            };
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

            return new KimodoEditorGenerationResult
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
            if (string.IsNullOrWhiteSpace(analysisJson) || trimStartFrame <= 0)
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
