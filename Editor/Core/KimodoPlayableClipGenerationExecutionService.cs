using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoPlayableClipGenerationExecutionService
    {
        private const double TimelineEpsilonSeconds = 1e-6;

        private sealed class ContinuousClipEntry
        {
            public TimelineClip TimelineClip;
            public KimodoPlayableClip Clip;
            public int StartFrame;
            public int FrameCount;
            public KimodoEditorGenerateRequest Request;
        }

        internal static int GetSelectedClipCount(KimodoPlayableClip fallback)
        {
            return Math.Max(1, KimodoEditorSelectionBridge.GetSelectedPlayableClips(fallback).Count);
        }

        internal static bool TryStartGenerate(
            KimodoPlayableClip clip,
            out EditorGenerateSession session,
            out string error)
        {
            session = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "KimodoPlayableClip is null.";
                return false;
            }

            List<TimelineClip> selected = KimodoEditorSelectionBridge.GetSelectedPlayableClips(clip);
            if (selected.Count <= 1)
            {
                return StartSingle(clip, out session, out error);
            }

            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i]?.asset is KimodoPlayableClip selectedClip &&
                    EditorGenerateSessionRunner.TryGet(selectedClip, out EditorGenerateSession active) &&
                    active != null &&
                    active.IsRunning)
                {
                    error = $"A generation session is already running for '{selectedClip.name}'.";
                    session = active;
                    return false;
                }
            }

            var sorted = new List<TimelineClip>(selected);
            sorted.Sort(CompareTimelineClips);
            bool continuous = TryCreateContinuousPlan(sorted, out _, out _, out string fallbackReason);
            bool started = EditorGenerateSessionRunner.Start(
                clip,
                $"clip-batch:{clip.GetInstanceID()}",
                KimodoEditorCommandKind.GeneratePlayableClip,
                async (handle, token) => await GenerateSelectionAsync(
                    selected,
                    (stage, message) => EditorGenerateSessionRunner.UpdateProgress(clip, handle.RequestId, stage, message),
                token),
                out session,
                out error);
            if (started && !continuous)
            {
                error = $"Queued serial generation: {fallbackReason}";
            }
            return started;
        }

        private static bool StartSingle(
            KimodoPlayableClip clip,
            out EditorGenerateSession session,
            out string error)
        {
            return EditorGenerateSessionRunner.Start(
                clip,
                $"clip:{clip.GetInstanceID()}",
                KimodoEditorCommandKind.GeneratePlayableClip,
                async (handle, token) => await GenerateAndFinalizeAsync(
                    clip,
                    externalConstraint: null,
                    (stage, message) => EditorGenerateSessionRunner.UpdateProgress(clip, handle.RequestId, stage, message),
                    token),
                out session,
                out error);
        }

        private static async Task<KimodoEditorGenerateResult> GenerateSelectionAsync(
            List<TimelineClip> selected,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            selected.Sort(CompareTimelineClips);
            if (TryCreateContinuousPlan(selected, out List<ContinuousClipEntry> entries, out KimodoMotionModelProfile profile, out string fallbackReason))
            {
                Debug.Log($"[Kimodo][TimelineBatch] Generating {entries.Count} connected ARDY clips in one continuous Session.");
                return await GenerateContinuousArdyAsync(entries, profile, progress, token);
            }

            string warning = $"[Kimodo][TimelineBatch] Continuous ARDY generation is unavailable: {fallbackReason} Falling back to serial clip generation.";
            Debug.LogWarning(warning);
            progress?.Invoke(KimodoBridgeCommandStage.Validate, warning);

            KimodoEditorGenerateResult last = null;
            for (int i = 0; i < selected.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (selected[i]?.asset is not KimodoPlayableClip selectedClip)
                {
                    continue;
                }
                last = await GenerateAndFinalizeAsync(
                    selectedClip,
                    externalConstraint: null,
                    PrefixProgress(progress, i, selected.Count),
                    token);
            }
            return last ?? throw new InvalidOperationException("No selected KimodoPlayableClip could be generated.");
        }

        internal static bool TryValidateContinuousSelection(
            IReadOnlyList<TimelineClip> selected,
            out string reason)
        {
            var sorted = selected != null ? new List<TimelineClip>(selected) : new List<TimelineClip>();
            sorted.Sort(CompareTimelineClips);
            return TryCreateContinuousPlan(sorted, out _, out _, out reason);
        }

        private static bool TryCreateContinuousPlan(
            IReadOnlyList<TimelineClip> selected,
            out List<ContinuousClipEntry> entries,
            out KimodoMotionModelProfile profile,
            out string reason)
        {
            entries = new List<ContinuousClipEntry>();
            profile = null;
            reason = string.Empty;
            if (selected == null || selected.Count < 2)
            {
                reason = "Select at least two Timeline clips.";
                return false;
            }
            if (selected[0]?.asset is not KimodoPlayableClip firstClip ||
                !KimodoMotionModelProfiles.TryGetArdy(firstClip.bridgeModelName, out profile))
            {
                reason = "The selection is not entirely ARDY.";
                return false;
            }

            var differences = new List<string>();
            TrackAsset expectedTrack = selected[0].GetParentTrack();
            int expectedSteps = ResolveArdySteps(firstClip, profile);
            int cursor = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                TimelineClip timelineClip = selected[i];
                if (timelineClip?.asset is not KimodoPlayableClip playable)
                {
                    AddDifference(differences, $"item {i + 1} is not a KimodoPlayableClip");
                    continue;
                }

                if (!KimodoMotionModelProfiles.TryGetArdy(playable.bridgeModelName, out KimodoMotionModelProfile currentProfile) ||
                    !string.Equals(currentProfile.ModelName, profile.ModelName, StringComparison.Ordinal))
                {
                    AddDifference(differences, $"'{playable.name}' uses a different model/profile");
                }
                if (!ReferenceEquals(timelineClip.GetParentTrack(), expectedTrack) || expectedTrack == null)
                {
                    AddDifference(differences, "clips are not on the same Timeline track/binding");
                }
                if (i > 0 && Math.Abs(selected[i - 1].end - timelineClip.start) > TimelineEpsilonSeconds)
                {
                    AddDifference(differences, $"'{selected[i - 1].displayName}' and '{timelineClip.displayName}' have a gap or overlap");
                }
                if (playable.textEncoderMode != firstClip.textEncoderMode)
                {
                    AddDifference(differences, $"'{playable.name}' has a different Text Encoder mode");
                }
                if (ResolveArdySteps(playable, profile) != expectedSteps)
                {
                    AddDifference(differences, $"'{playable.name}' has different diffusion steps");
                }
                if (Mathf.Abs(playable.textWeight - firstClip.textWeight) > 1e-6f)
                {
                    AddDifference(differences, $"'{playable.name}' has a different text weight/CFG");
                }
                if (playable.randomSeed != firstClip.randomSeed || (!playable.randomSeed && playable.seed != firstClip.seed))
                {
                    AddDifference(differences, $"'{playable.name}' has a different seed strategy/value");
                }
                if (playable.normalizeConstraintOrigin != firstClip.normalizeConstraintOrigin)
                {
                    AddDifference(differences, $"'{playable.name}' has a different constraint-origin setting");
                }

                double exactFrames = timelineClip.duration * profile.SourceFps;
                int frameCount = (int)Math.Round(exactFrames, MidpointRounding.AwayFromZero);
                if (frameCount <= 0 || Math.Abs(exactFrames - frameCount) > 1e-4)
                {
                    AddDifference(differences, $"'{timelineClip.displayName}' duration is not aligned to {profile.SourceFps:g} FPS");
                    frameCount = Math.Max(1, frameCount);
                }
                if (i > 0 && cursor % profile.FramesPerToken != 0)
                {
                    AddDifference(differences, $"boundary before '{timelineClip.displayName}' is not aligned to the {profile.FramesPerToken}-frame motion token");
                }

                entries.Add(new ContinuousClipEntry
                {
                    TimelineClip = timelineClip,
                    Clip = playable,
                    StartFrame = cursor,
                    FrameCount = frameCount
                });
                cursor += frameCount;
            }

            if (differences.Count > 0)
            {
                reason = string.Join("; ", differences) + ".";
                entries.Clear();
                return false;
            }
            return true;
        }

        private static async Task<KimodoEditorGenerateResult> GenerateContinuousArdyAsync(
            List<ContinuousClipEntry> entries,
            KimodoMotionModelProfile profile,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            int groupSeed = entries[0].Clip.randomSeed
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : entries[0].Clip.seed;
            BuildContinuousRequests(entries, profile, groupSeed, progress, token);

            KimodoRawMotionData aggregate = null;
            KimodoBridgeService service = KimodoBridgeService.CreateOwned();
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ContinuousClipEntry entry = entries[i];
                    KimodoGenerationRequestDto generationRequest = i == 0
                        ? CreateInitialStreamRequest(entry.Request, profile)
                        : CreateStreamUpdateRequest(entry, profile, includePatch: true);
                    aggregate = MergeRange(
                        aggregate,
                        await SendArdyRequestAsync(service, generationRequest, profile, groupSeed, i, entries.Count, progress, token));

                    int requiredEndFrame = entry.StartFrame + entry.FrameCount;
                    while (aggregate.FrameCount < requiredEndFrame)
                    {
                        int previousFrameCount = aggregate.FrameCount;
                        aggregate = MergeRange(
                            aggregate,
                            await SendArdyRequestAsync(
                                service,
                                CreateStreamUpdateRequest(entry, profile, includePatch: false),
                                profile,
                                groupSeed,
                                i,
                                entries.Count,
                                progress,
                                token));
                        if (aggregate.FrameCount <= previousFrameCount)
                        {
                            throw new InvalidOperationException("ARDY stream did not advance while collecting the selected Timeline range.");
                        }
                    }
                }
            }
            finally
            {
                await service.DisposeAsync();
            }

            var baked = new List<KimodoEditorGenerateResult>(entries.Count);
            int finalized = 0;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ContinuousClipEntry entry = entries[i];
                    if (!KimodoRawMotionUtility.TrySlice(
                            aggregate,
                            entry.StartFrame,
                            entry.FrameCount,
                            out KimodoRawMotionData motion,
                            out string sliceError))
                    {
                        throw new InvalidOperationException(sliceError);
                    }

                    byte[] payload = KimodoRawMotionUtility.ToFlatBuffer(motion, profile.ModelName);
                    entry.Request.GeneratedArdySeeds.Clear();
                    entry.Request.GeneratedArdySeeds.Add(groupSeed);
                    entry.Request.GeneratedArdyFingerprint = profile.MotionRepFingerprint;
                    entry.Request.GeneratedArdyMotionCachePath = ArdyUnityMotionCache.Write(payload, $"timeline-batch-{i + 1}");
                    entry.Request.Progress?.Invoke(KimodoBridgeCommandStage.Bake, $"Baking selected clip {i + 1}/{entries.Count}...");
                    baked.Add(KimodoEditorGeneratePipeline.BakeRuntimeResult(
                        entry.Request,
                        entry.Request.Prompt?.Trim() ?? string.Empty,
                        profile.ModelName,
                        new KimodoBridgeCommandResult
                        {
                            MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(motion),
                            MotionData = motion,
                            MotionBytes = payload,
                            MotionFormat = "kmb_v1",
                            Message = "Continuous ARDY Timeline generation complete.",
                            RawStatus = "done",
                            MotionRepFingerprint = profile.MotionRepFingerprint,
                            ResolvedSeed = groupSeed,
                            StartFrame = entry.StartFrame,
                            EndFrameExclusive = entry.StartFrame + entry.FrameCount
                        }));
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    KimodoPlayableClipGenerationHostService.FinalizeGeneration(entries[i].Clip, entries[i].Request, baked[i]);
                    finalized++;
                }
            }
            catch
            {
                for (int i = finalized; i < entries.Count; i++)
                {
                    KimodoPlayableClipGenerationHostService.CleanupFailedGeneration(entries[i].Request);
                }
                throw;
            }

            return baked[baked.Count - 1];
        }

        private static void BuildContinuousRequests(
            List<ContinuousClipEntry> entries,
            KimodoMotionModelProfile profile,
            int groupSeed,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ContinuousClipEntry entry = entries[i];
                entry.Clip.seed = groupSeed;
                EditorUtility.SetDirty(entry.Clip);
                entry.Request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    entry.Clip,
                    entry.Clip.motionPrompt ?? string.Empty,
                    externalConstraint: null,
                    token,
                    normalizeConstraintOriginOverride: false,
                    effectiveSeedOverride: groupSeed,
                    disableTimelineInOut: true);
                entry.Request.Progress = PrefixProgress(progress, i, entries.Count);
                if (string.IsNullOrWhiteSpace(entry.Request.Prompt))
                {
                    throw new InvalidOperationException($"Prompt is empty on selected clip '{entry.Clip.name}'.");
                }
            }

            if (entries[0].Clip.normalizeConstraintOrigin)
            {
                var allSamples = new List<KimodoMarkerSampleResult>();
                for (int i = 0; i < entries.Count; i++)
                {
                    allSamples.AddRange(entries[i].Request.ConstraintSamples);
                }
                KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                    allSamples,
                    out KimodoConstraintNormalizationInfo normalization,
                    out string warning);
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    Debug.LogWarning($"[Kimodo][TimelineBatch] {warning}");
                }
                if (normalization != null && normalization.Applied && normalization.AnchorSample != null)
                {
                    Vector3 anchorPosition = new Vector3(
                        normalization.AnchorSample.unityRootPos.x,
                        0f,
                        normalization.AnchorSample.unityRootPos.z);
                    Quaternion anchorRotation = KimodoConstraintNormalizationUtility.ResolvePlanarRootRotation(
                        normalization.AnchorSample);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        KimodoEditorGenerateRequest request = entries[i].Request;
                        request.NormalizeConstraintOriginApplied = true;
                        request.NormalizationAnchorKind = normalization.AnchorKind;
                        request.NormalizationAnchorSample = normalization.AnchorSample.Clone();
                        if (i == 0 && request.InitialArdyHistorySource != null)
                        {
                            request.InitialArdyHistorySource.NormalizeRootToAnchor = true;
                            request.InitialArdyHistorySource.AnchorRootPosition = anchorPosition;
                            request.InitialArdyHistorySource.AnchorRootRotation = anchorRotation;
                        }
                    }
                }
            }

            for (int i = 0; i < entries.Count; i++)
            {
                KimodoEditorGenerateRequest request = entries[i].Request;
                request.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                    request.ConstraintSamples,
                    0.0,
                    request.TargetFrameCount / request.TargetFrameRate,
                    profile.SourceFps);
            }
        }

        private static KimodoGenerationRequestDto CreateInitialStreamRequest(
            KimodoEditorGenerateRequest request,
            KimodoMotionModelProfile profile)
        {
            KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                request,
                request.Prompt?.Trim() ?? string.Empty,
                profile.ModelName).GenerationRequest;
            generation.duration = null;
            generation.time_as_double = 0.0;
            generation.seed = request.EffectiveSeed;
            generation.steps = ResolveArdySteps(request, profile);
            generation.constraints_json = ExplicitConstraints(request.ConstraintsJson);
            generation.ardy_history_kmb = KimodoEditorGeneratePipeline.BuildInitialArdyHistoryPayload(request, profile);
            generation.ardy_playback_reserve_seconds = 0.0;
            generation.ardy_adaptive_playback_reserve = false;
            return generation;
        }

        private static KimodoGenerationRequestDto CreateStreamUpdateRequest(
            ContinuousClipEntry entry,
            KimodoMotionModelProfile profile,
            bool includePatch)
        {
            return new KimodoGenerationRequestDto
            {
                time_as_double = entry.StartFrame / (double)profile.SourceFps,
                prompt = includePatch ? entry.Request.Prompt?.Trim() ?? string.Empty : null,
                constraints_json = includePatch ? ExplicitConstraints(entry.Request.ConstraintsJson) : null,
                ardy_session_update_only = true
            };
        }

        private static async Task<KimodoBridgeCommandResult> SendArdyRequestAsync(
            KimodoBridgeService service,
            KimodoGenerationRequestDto generationRequest,
            KimodoMotionModelProfile profile,
            int groupSeed,
            int clipIndex,
            int clipCount,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            KimodoBridgeGenerationResult bridgeResult = await service.GenerateAsync(
                generationRequest,
                message => progress?.Invoke(
                    KimodoBridgeCommandStage.InvokeBackend,
                    $"[{clipIndex + 1}/{clipCount}] {message}"),
                token);
            var result = new KimodoBridgeCommandResult
            {
                MotionData = bridgeResult?.MotionData,
                MotionBytes = bridgeResult?.MotionBytes,
                MotionFormat = bridgeResult?.MotionFormat,
                Message = bridgeResult?.Message ?? string.Empty,
                RawStatus = bridgeResult?.RawStatus ?? string.Empty,
                MotionRepFingerprint = bridgeResult?.MotionRepFingerprint ?? string.Empty,
                ResolvedSeed = bridgeResult?.ResolvedSeed,
                StartFrame = bridgeResult?.StartFrame ?? 0,
                EndFrameExclusive = bridgeResult?.EndFrameExclusive ?? 0
            };
            KimodoEditorGeneratePipeline.ValidateArdyResult(result, profile, groupSeed);
            if (result.EndFrameExclusive - result.StartFrame != result.MotionData.FrameCount)
            {
                throw new InvalidOperationException(
                    $"ARDY response range [{result.StartFrame},{result.EndFrameExclusive}) does not match its {result.MotionData.FrameCount}-frame KMB payload.");
            }
            return result;
        }

        internal static KimodoRawMotionData MergeRange(
            KimodoRawMotionData aggregate,
            KimodoBridgeCommandResult segment)
        {
            if (segment?.MotionData == null)
            {
                throw new InvalidOperationException("ARDY stream returned an empty KMB range.");
            }
            if (aggregate == null)
            {
                if (segment.StartFrame != 0)
                {
                    throw new InvalidOperationException($"ARDY stream begins at frame {segment.StartFrame}, expected frame 0.");
                }
                return segment.MotionData;
            }
            if (segment.StartFrame > aggregate.FrameCount)
            {
                throw new InvalidOperationException(
                    $"ARDY stream has a gap: local range ends at {aggregate.FrameCount}, response begins at {segment.StartFrame}.");
            }
            if (segment.StartFrame == 0)
            {
                return segment.MotionData;
            }
            if (!KimodoRawMotionUtility.TrySlice(
                    aggregate,
                    0,
                    segment.StartFrame,
                    out KimodoRawMotionData prefix,
                    out string sliceError))
            {
                throw new InvalidOperationException(sliceError);
            }
            if (!KimodoRawMotionUtility.TryConcatenate(
                    new[] { prefix, segment.MotionData },
                    prefix.FrameCount + segment.MotionData.FrameCount,
                    out KimodoRawMotionData merged,
                    out string mergeError))
            {
                throw new InvalidOperationException(mergeError);
            }
            return merged;
        }

        private static int ResolveArdySteps(KimodoPlayableClip clip, KimodoMotionModelProfile profile)
        {
            return clip.diffusionSteps <= 0
                ? profile.MaxDiffusionSteps
                : Mathf.Clamp(clip.diffusionSteps, 1, profile.MaxDiffusionSteps);
        }

        private static int ResolveArdySteps(KimodoEditorGenerateRequest request, KimodoMotionModelProfile profile)
        {
            return request.DiffusionSteps <= 0
                ? profile.MaxDiffusionSteps
                : Mathf.Clamp(request.DiffusionSteps, 1, profile.MaxDiffusionSteps);
        }

        private static string ExplicitConstraints(string constraintsJson)
        {
            return string.IsNullOrWhiteSpace(constraintsJson) ? "[]" : constraintsJson;
        }

        private static Action<KimodoBridgeCommandStage, string> PrefixProgress(
            Action<KimodoBridgeCommandStage, string> progress,
            int index,
            int count)
        {
            return progress == null
                ? null
                : (stage, message) => progress(stage, $"[{index + 1}/{count}] {message}");
        }

        private static int CompareTimelineClips(TimelineClip left, TimelineClip right)
        {
            int byStart = (left?.start ?? 0.0).CompareTo(right?.start ?? 0.0);
            return byStart != 0 ? byStart : (left?.end ?? 0.0).CompareTo(right?.end ?? 0.0);
        }

        private static void AddDifference(List<string> differences, string message)
        {
            if (!differences.Contains(message))
            {
                differences.Add(message);
            }
        }

        internal static async Task<KimodoEditorGenerateResult> GenerateAndFinalizeAsync(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("KimodoPlayableClip is null.");
            }

            string prompt = clip.motionPrompt ?? string.Empty;
            KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                clip,
                prompt,
                externalConstraint,
                token);

            try
            {
                request.Progress = progress;
                KimodoEditorGenerateResult result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
                token.ThrowIfCancellationRequested();
                KimodoPlayableClipGenerationHostService.FinalizeGeneration(clip, request, result);
                return result;
            }
            catch
            {
                KimodoPlayableClipGenerationHostService.CleanupFailedGeneration(request);
                throw;
            }
        }
    }
}
