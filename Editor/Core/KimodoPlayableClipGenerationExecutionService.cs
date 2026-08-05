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
        private sealed class ConnectedClipEntry
        {
            public TimelineClip TimelineClip;
            public KimodoPlayableClip Clip;
            public int StartFrame;
            public int FrameCount;
            public double DurationSeconds;
            public KimodoEditorGenerateRequest Request;
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
            selected.Sort(CompareTimelineClips);
            if (selected.Count <= 1)
            {
                return StartSingle(clip, out session, out error);
            }

            var selectedClips = new List<KimodoPlayableClip>(selected.Count);
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i]?.asset is not KimodoPlayableClip selectedClip)
                {
                    continue;
                }

                if (EditorGenerateSessionRunner.TryGet(selectedClip, out EditorGenerateSession active) &&
                    active != null &&
                    active.IsRunning)
                {
                    error = $"A generation session is already running for '{selectedClip.name}'.";
                    session = active;
                    return false;
                }

                selectedClips.Add(selectedClip);
            }

            return EditorGenerateSessionRunner.Start(
                clip,
                $"clip-selected:{KimodoUnityObjectIdUtility.NameKey(clip)}",
                KimodoEditorCommandKind.GeneratePlayableClip,
                async (handle, token) => await GenerateSelectedAndFinalizeAsync(
                    selectedClips,
                    (stage, message) => EditorGenerateSessionRunner.UpdateProgress(
                        clip,
                        handle.RequestId,
                        stage,
                        message),
                    token),
                out session,
                out error);
        }

        internal static int GetSelectedPlayableClipCount(KimodoPlayableClip clip)
        {
            return KimodoEditorSelectionBridge.GetSelectedPlayableClips(clip).Count;
        }

        internal static bool TryStartGenerateConnectedArdy(
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

            if (!TryCreateConnectedArdyPlan(
                    clip,
                    out List<ConnectedClipEntry> entries,
                    out KimodoMotionModelProfile profile,
                    out error))
            {
                return false;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (EditorGenerateSessionRunner.TryGet(entries[i].Clip, out EditorGenerateSession active) &&
                    active != null &&
                    active.IsRunning)
                {
                    error = $"A generation session is already running for '{entries[i].Clip.name}'.";
                    session = active;
                    return false;
                }
            }

            return EditorGenerateSessionRunner.Start(
                clip,
                $"clip-connected:{KimodoUnityObjectIdUtility.NameKey(clip)}",
                KimodoEditorCommandKind.GeneratePlayableClip,
                async (handle, token) => await GenerateConnectedArdyAsync(
                    entries,
                    profile,
                    (stage, message) => EditorGenerateSessionRunner.UpdateProgress(
                        clip,
                        handle.RequestId,
                        stage,
                        message),
                    token),
                out session,
                out error);
        }

        private static bool StartSingle(
            KimodoPlayableClip clip,
            out EditorGenerateSession session,
            out string error)
        {
            return EditorGenerateSessionRunner.Start(
                clip,
                $"clip:{KimodoUnityObjectIdUtility.NameKey(clip)}",
                KimodoEditorCommandKind.GeneratePlayableClip,
                async (handle, token) => await GenerateAndFinalizeAsync(
                    clip,
                    externalConstraint: null,
                    (stage, message) => EditorGenerateSessionRunner.UpdateProgress(clip, handle.RequestId, stage, message),
                    token),
                out session,
                out error);
        }

        internal static bool TryValidateConnectedSelection(
            IReadOnlyList<TimelineClip> selected,
            out string reason)
        {
            var sorted = selected != null ? new List<TimelineClip>(selected) : new List<TimelineClip>();
            sorted.Sort(CompareTimelineClips);
            return TryCreateConnectedPlan(sorted, out _, out _, out reason);
        }

        internal static bool TryGetConnectedArdyClipCount(
            KimodoPlayableClip clip,
            out int count,
            out string reason)
        {
            count = 0;
            if (!TryCreateConnectedArdyPlan(clip, out List<ConnectedClipEntry> entries, out _, out reason))
            {
                return false;
            }

            count = entries.Count;
            return true;
        }

        internal static bool TryGetSelectedArdyClipCount(
            KimodoPlayableClip clip,
            out int count)
        {
            count = 0;
            List<TimelineClip> selected = KimodoEditorSelectionBridge.GetSelectedPlayableClips(clip);
            if (selected.Count < 2)
            {
                return false;
            }

            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i]?.asset is not KimodoPlayableClip playable ||
                    !KimodoMotionModelProfiles.TryGetArdy(playable.bridgeModelName, out _))
                {
                    return false;
                }
            }

            count = selected.Count;
            return true;
        }

        private static bool TryCreateConnectedArdyPlan(
            KimodoPlayableClip clip,
            out List<ConnectedClipEntry> entries,
            out KimodoMotionModelProfile profile,
            out string reason)
        {
            entries = new List<ConnectedClipEntry>();
            profile = null;
            reason = string.Empty;
            List<TimelineClip> selected = KimodoEditorSelectionBridge.GetSelectedPlayableClips(clip);
            selected.Sort(CompareTimelineClips);
            return TryCreateConnectedPlan(selected, out entries, out profile, out reason);
        }

        private static bool TryCreateConnectedPlan(
            IReadOnlyList<TimelineClip> selected,
            out List<ConnectedClipEntry> entries,
            out KimodoMotionModelProfile profile,
            out string reason)
        {
            entries = new List<ConnectedClipEntry>();
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
                if (playable.textEncoderMode != firstClip.textEncoderMode)
                {
                    AddDifference(differences, $"'{playable.name}' has a different Text Encoder mode");
                }
                int frameCount = KimodoFrameTimeUtility.SecondsToFrameCount(
                    timelineClip.duration,
                    profile.SourceFps);
                if (frameCount <= 0)
                {
                    AddDifference(differences, $"'{timelineClip.displayName}' duration resolves to zero frames");
                    frameCount = 1;
                }

                entries.Add(new ConnectedClipEntry
                {
                    TimelineClip = timelineClip,
                    Clip = playable,
                    StartFrame = cursor,
                    FrameCount = frameCount,
                    DurationSeconds = Math.Max(0.0, timelineClip.duration)
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

        private static async Task<KimodoEditorGenerateResult> GenerateConnectedArdyAsync(
            List<ConnectedClipEntry> entries,
            KimodoMotionModelProfile profile,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            int groupSeed = entries[0].Clip.randomSeed
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : entries[0].Clip.seed;
            BuildConnectedRequests(entries, profile, groupSeed, progress, token);
            int totalFrameCount = entries[entries.Count - 1].StartFrame + entries[entries.Count - 1].FrameCount;
            KimodoEditorGenerateRequest firstRequest = entries[0].Request;
            KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                firstRequest,
                firstRequest.Prompt?.Trim() ?? string.Empty,
                profile.ModelName).GenerationRequest;
            generation.duration = totalFrameCount / profile.SourceFps;
            generation.time_as_double = 0.0;
            generation.seed = groupSeed;
            generation.steps = KimodoMotionModelProfiles.ResolveArdyProtocolSteps(firstRequest.DiffusionSteps, profile);
            generation.constraints_json = ExplicitConstraints(firstRequest.ConstraintsJson);
            generation.ardy_history_kmb = KimodoEditorGeneratePipeline.BuildInitialArdyHistoryPayload(firstRequest, profile);
            generation.ardy_playback_reserve_seconds = 0.0;
            generation.ardy_adaptive_playback_reserve = false;
            AddTimelineSegments(entries, profile, generation);

            firstRequest.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating connected ARDY KMB...");
            var pipeline = new KimodoBridgeCommand();
            KimodoBridgeCommandResult aggregate = await pipeline.ExecuteAsync(
                new KimodoBridgeCommandRequest { GenerationRequest = generation },
                (stage, message) => progress?.Invoke(stage, message),
                token);
            KimodoEditorGeneratePipeline.ValidateArdyResult(aggregate, profile, groupSeed);
            if (aggregate.MotionData.FrameCount != totalFrameCount)
            {
                throw new InvalidOperationException(
                    $"ARDY returned {aggregate.MotionData.FrameCount} frames; expected {totalFrameCount}.");
            }

            var baked = new List<KimodoEditorGenerateResult>(entries.Count);
            int finalized = 0;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ConnectedClipEntry entry = entries[i];
                    if (!KimodoRawMotionUtility.TrySlice(
                            aggregate.MotionData,
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
                    entry.Request.GeneratedArdyMotionCachePath = ArdyUnityMotionCache.Write(payload, $"timeline-connected-{i + 1}");
                    entry.Request.Progress?.Invoke(KimodoBridgeCommandStage.Bake, $"Baking connected clip {i + 1}/{entries.Count}...");
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
                            Message = "Connected ARDY Timeline generation complete.",
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

        private static void BuildConnectedRequests(
            List<ConnectedClipEntry> entries,
            KimodoMotionModelProfile profile,
            int groupSeed,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ConnectedClipEntry entry = entries[i];
                entry.Clip.seed = groupSeed;
                EditorUtility.SetDirty(entry.Clip);
                entry.Request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    entry.Clip,
                    entry.Clip.motionPrompt ?? string.Empty,
                    externalConstraint: null,
                    token,
                    effectiveSeedOverride: groupSeed,
                    disableTimelineInOut: true,
                    deferConstraintNormalization: true,
                    enableAutoBeginAnchor: i == 0);
                AppendConnectedBoundarySamples(entry, i, entries.Count);
                entry.Request.Progress = PrefixProgress(progress, i, entries.Count);
                if (string.IsNullOrWhiteSpace(entry.Request.Prompt))
                {
                    throw new InvalidOperationException($"Prompt is empty on selected clip '{entry.Clip.name}'.");
                }
            }

            var allSamples = new List<KimodoMarkerSampleResult>();
            var sampleTimeOffsets = new List<double>();
            for (int i = 0; i < entries.Count; i++)
            {
                double timeOffset = entries[i].StartFrame / (double)profile.SourceFps;
                List<KimodoMarkerSampleResult> samples = entries[i].Request.ConstraintSamples;
                for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
                {
                    KimodoMarkerSampleResult sample = samples[sampleIndex];
                    if (sample == null)
                    {
                        continue;
                    }
                    sample.sampleTime += timeOffset;
                    allSamples.Add(sample);
                    sampleTimeOffsets.Add(timeOffset);
                }
            }

            KimodoEditorGenerateRequest firstRequest = entries[0].Request;
            if (firstRequest.HasSyntheticAutoBeginConstraint && firstRequest.ConstraintSamples.Count > 0)
            {
                KimodoMarkerSampleResult syntheticAutoBegin = firstRequest.ConstraintSamples[0];
                if (KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                        allSamples,
                        1.0,
                        syntheticAutoBegin))
                {
                    int syntheticIndex = allSamples.IndexOf(syntheticAutoBegin);
                    if (syntheticIndex >= 0)
                    {
                        allSamples.RemoveAt(syntheticIndex);
                        sampleTimeOffsets.RemoveAt(syntheticIndex);
                        firstRequest.ConstraintSamples.RemoveAt(0);
                        firstRequest.HasSyntheticAutoBeginConstraint = false;
                    }
                }
            }

            try
            {
                int totalFrameCount = entries[entries.Count - 1].StartFrame + entries[entries.Count - 1].FrameCount;
                firstRequest.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                    allSamples,
                    0.0,
                    totalFrameCount / profile.SourceFps,
                    profile.SourceFps);
            }
            finally
            {
                for (int i = 0; i < allSamples.Count; i++)
                {
                    if (allSamples[i] != null)
                    {
                        allSamples[i].sampleTime -= sampleTimeOffsets[i];
                    }
                }
            }
        }

        private static void AddTimelineSegments(
            List<ConnectedClipEntry> entries,
            KimodoMotionModelProfile profile,
            KimodoGenerationRequestDto generation)
        {
            // Keep prompt boundaries explicit; Python performs the single long generation.
            var segments = new List<KimodoArdyTimelineSegmentDto>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                ConnectedClipEntry entry = entries[i];
                segments.Add(new KimodoArdyTimelineSegmentDto
                {
                    prompt = entry.Request.Prompt?.Trim() ?? string.Empty,
                    duration = (float)entry.DurationSeconds
                });
            }
            generation.ardy_timeline_segments = segments;
        }

        private static void AppendConnectedBoundarySamples(
            ConnectedClipEntry entry,
            int index,
            int count)
        {
            KimodoPlayableClip clip = entry.Clip;
            if (clip == null || clip.inOutConstraintMode == KimodoInOutConstraintMode.None)
            {
                return;
            }

            bool enableIn = clip.enableInConstraint;
            bool enableOut = index == count - 1 && clip.enableOutConstraint;
            if (!enableIn && !enableOut)
            {
                return;
            }

            if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    entry.TimelineClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out string error))
            {
                throw new InvalidOperationException($"Build connected clip constraints failed: {error}");
            }

            KimodoInOutConstraintRequest request = KimodoInOutConstraintAdapter.BuildTimelineRequest(
                context,
                clip.inOutConstraintMode,
                autoBeginAnchor: false,
                deferNormalization: true,
                enableIn,
                enableOut,
                entry.FrameCount,
                manualSamples: null);
            if (request == null)
            {
                return;
            }

            if (!KimodoInOutConstraintTools.TrySampleBoundaryPair(
                    request,
                    out KimodoMarkerSampleResult beginSample,
                    out KimodoMarkerSampleResult endSample,
                    out _,
                    out error))
            {
                throw new InvalidOperationException($"Build connected clip constraints failed: {error}");
            }

            if (beginSample != null)
            {
                entry.Request.ConstraintSamples.Add(beginSample);
            }
            if (endSample != null)
            {
                entry.Request.ConstraintSamples.Add(endSample);
            }
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

        internal static int CompareTimelineClips(TimelineClip left, TimelineClip right)
        {
            int byStart = (left?.start ?? 0.0).CompareTo(right?.start ?? 0.0);
            return byStart != 0 ? byStart : (left?.end ?? 0.0).CompareTo(right?.end ?? 0.0);
        }

        private static async Task<KimodoEditorGenerateResult> GenerateSelectedAndFinalizeAsync(
            IReadOnlyList<KimodoPlayableClip> clips,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            KimodoEditorGenerateResult result = null;
            for (int i = 0; i < clips.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                KimodoPlayableClip selectedClip = clips[i];
                string prefix = $"[{i + 1}/{clips.Count}] {selectedClip.name}";
                result = await GenerateAndFinalizeAsync(
                    selectedClip,
                    externalConstraint: null,
                    (stage, message) => progress?.Invoke(stage, $"{prefix}: {message}"),
                    token);
            }

            return result ?? throw new InvalidOperationException("No Timeline clips were selected for generation.");
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
