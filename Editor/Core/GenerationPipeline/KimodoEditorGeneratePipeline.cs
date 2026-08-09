using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using KimodoUnityBridge.Command;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorGeneratePipeline
    {
        private const string DefaultModelName = "Kimodo-SOMA-RP-v1";

        public static async Task<command_generate_result> ExecuteAsync(KimodoEditorGenerateRequest request)
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

        internal static command_generate_result BakeRuntimeResult(
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
                KimodoEditorClipWritebackService.FlushWritebackAssets();
                request.Progress?.Invoke(KimodoBridgeCommandStage.Retarget, "Skipping retarget: binding hierarchy already matches clip bindings.");
                return CompleteBakedOutput(request, prompt, modelName, runtimeResult, outputPlan, rawBoneClip);
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(outputPlan.OriginRetargetAvatar))
            {
                throw new InvalidOperationException("Retarget requires a valid humanoid origin avatar.");
            }

            ThrowIfCanceled(request);
            request.Progress?.Invoke(KimodoBridgeCommandStage.Retarget, "Retargeting...");
            ResolveTimelinePlanarOffset(
                request,
                outputPlan,
                out Vector3 targetPlanarOffset,
                out Quaternion targetPlanarRotation,
                out float targetHumanScale);
            if (!KimodoRetargetToolsEditor.TryBakeMuscleClipToClip(
                    request.TargetClip,
                    outputPlan.OriginRetargetAvatar,
                    request.TargetClip,
                    targetPlanarOffset,
                    targetPlanarRotation,
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
                KimodoEditorClipWritebackService.FlushWritebackAssets();
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
            KimodoEditorClipWritebackService.FlushWritebackAssets();
            ThrowIfCanceled(request);

            return CompleteBakedOutput(request, prompt, modelName, runtimeResult, outputPlan, rawBoneClip);
        }

        private static void ResolveTimelinePlanarOffset(
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerateOutputPlan outputPlan,
            out Vector3 targetPlanarOffset,
            out Quaternion targetPlanarRotation,
            out float targetHumanScale)
        {
            targetPlanarOffset = Vector3.zero;
            targetPlanarRotation = Quaternion.identity;
            targetHumanScale = 1f;

            TrackAsset track = request?.TimelineClipSnapshot?.GetParentTrack();
            if (track == null)
            {
                return;
            }

            Animator animator = null;
            if (request.TimelineDirectorSnapshot != null)
            {
                UnityEngine.Object binding = request.TimelineDirectorSnapshot.GetGenericBinding(track);
                animator = binding as Animator ??
                    (binding as GameObject)?.GetComponentInChildren<Animator>(true);
            }

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                track,
                animator,
                out Vector3 trackPosition,
                out Quaternion trackRotation);

            targetPlanarOffset = new Vector3(trackPosition.x, 0f, trackPosition.z);
            targetPlanarRotation = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(trackRotation);

            if (KimodoRetargetCoreUtility.IsValidHumanoid(outputPlan?.TargetRetargetAvatar))
            {
                targetHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(outputPlan.TargetRetargetAvatar);
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
            request.GeneratedArdySeeds.Add(directResult.ResolvedSeed.Value);
            request.GeneratedArdyFingerprint = directResult.MotionRepFingerprint;

            KimodoRawMotionData sourceMotion = directResult.MotionData;
            byte[] sourcePayload = KimodoRawMotionUtility.ToFlatBuffer(sourceMotion, profile.ModelName);
            if (request.RuntimeTrimStartFrame <= 0)
            {
                request.GeneratedArdyMotionCachePath = ArdyUnityMotionCache.Write(sourcePayload, "timeline-final");
            }
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

        private static command_generate_result CompleteBakedOutput(
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
                        KimodoPlayableClip.ResolveBakeSkeletonTypeFromModelName(modelName),
                        modelName,
                        null,
                        out string rawTrimError))
                {
                    throw new InvalidOperationException($"Trim raw ARDY guard failed: {rawTrimError}");
                }
                EditorUtility.SetDirty(rawBoneClip);
            }

            request.GeneratedArdyMotionCachePath = ArdyUnityMotionCache.Write(
                runtimeResult.MotionBytes,
                "timeline-final");
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

            SkeletonCache cache = null;
            AnimationClip trimmedClip = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
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
            SkeletonCache cache,
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
            SkeletonCache cache,
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
                model = modelName,
                text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(request.TextEncoderMode),
                simulate_free_vram_gb = KimodoPlayableClipGenerationSettings.instance.KeepCpuForceExperimental ? 0 : (int?)null,
                models_root = modelsRoot,
                force_hf_download = false,
                owner_pid = System.Diagnostics.Process.GetCurrentProcess().Id,
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

        private static command_generate_result Complete(
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

            return new command_generate_result
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
                ArdyMotionCachePath = request.GeneratedArdyMotionCachePath,
                ArdyMotionRepFingerprint = request.GeneratedArdyFingerprint,
                ArdyResolvedSeeds = new List<int>(request.GeneratedArdySeeds)
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
            if (string.IsNullOrWhiteSpace(analysisJson) || trimStartFrame <= 0 || frameRate <= 0f)
            {
                return analysisJson;
            }

            try
            {
                var analysis = JObject.Parse(analysisJson);
                if (analysis["keyframes"] is not JArray keyframes)
                {
                    return analysisJson;
                }

                var trimmed = new JArray();
                foreach (JToken keyframe in keyframes)
                {
                    int frame = keyframe.Value<int?>("frame") ?? 0;
                    frame -= trimStartFrame;
                    if (frame < 0 || frame >= frameCount)
                    {
                        continue;
                    }
                    keyframe["frame"] = frame;
                    keyframe["time"] = frame / (double)frameRate;
                    trimmed.Add(keyframe);
                }
                analysis["keyframes"] = trimmed;
                return analysis.ToString(Formatting.None);
            }
            catch
            {
                return analysisJson;
            }
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

        internal static AnimationClip CreateRawBoneWritebackClip(AnimationClip sourceClip)
        {
            if (sourceClip == null)
            {
                return null;
            }

            string sourceName = string.IsNullOrWhiteSpace(sourceClip.name) ? "KimodoRawBone" : sourceClip.name.Trim();
            bool persist = KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips;
            AnimationClip rawBoneClip = persist
                ? KimodoEditorClipWritebackService.CreateGeneratedCacheAnimationClipAsset($"{sourceName}_RawBone")
                : new AnimationClip
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    name = $"{sourceName}_RawBone"
                };
            KimodoEditorClipUtility.CopyClipData(sourceClip, rawBoneClip, forceNoLoopKeepY: true);
            rawBoneClip.legacy = sourceClip.legacy;
            rawBoneClip.frameRate = sourceClip.frameRate;
            if (persist)
            {
                EditorUtility.SetDirty(rawBoneClip);
                KimodoPlayableClipGenerationSettings.DebugLog(
                    $"[Kimodo][Generate] Wrote raw Kimodo bone clip: '{AssetDatabase.GetAssetPath(rawBoneClip)}'.");
            }
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
