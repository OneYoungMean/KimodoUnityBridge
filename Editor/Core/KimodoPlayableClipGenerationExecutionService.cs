using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityBridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorAnalysisInput
    {
        public byte[] MotionBytes;
        public int StartFrame;
        public int EndFrameExclusive;
        public string ModelName;
        public KimodoTextEncoderMode TextEncoderMode;
        public string ModelsRoot;
        public string AnalysisOptionsJson;
    }

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
            out KimodoEditorGenerationJobSession session,
            out string error)
        {
            session = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "KimodoPlayableClip is null.";
                return false;
            }

            List<TimelineClip> selected = KimodoEditorTimelineSelection.GetSelectedPlayableClips(clip);
            selected.Sort(CompareTimelineClips);
            if (selected.Count <= 1)
            {
                return StartSingle(clip, out session, out error);
            }

            var selectedClips = new List<KimodoPlayableClip>(selected.Count);
            var selectedTimelineClips = new List<TimelineClip>(selected.Count);
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i]?.asset is not KimodoPlayableClip selectedClip)
                {
                    continue;
                }

                if (KimodoEditorGenerationJobService.TryGet(selectedClip, out KimodoEditorGenerationJobSession active) &&
                    active != null &&
                    active.IsRunning)
                {
                    error = $"A generation session is already running for '{selectedClip.name}'.";
                    session = active;
                    return false;
                }

                selectedClips.Add(selectedClip);
                selectedTimelineClips.Add(selected[i]);
            }

            return KimodoEditorGenerationJobService.Start(
                clip,
                async (handle, token) => await GenerateSelectedAndFinalizeAsync(
                    selectedClips,
                    selectedTimelineClips,
                    (stage, message) => KimodoEditorGenerationJobService.UpdateProgress(
                        clip,
                        handle.RequestId,
                        stage,
                        message),
                    token),
                null,
                out session,
                out error);
        }

        internal static bool Analysis(
            KimodoEditorAnalysisInput input,
            out string analysisJson,
            out byte[] motionBytes,
            out string error)
        {
            analysisJson = string.Empty;
            motionBytes = null;
            error = string.Empty;
            try
            {
                if (input == null)
                {
                    throw new InvalidOperationException("Analysis input is null.");
                }
                if (input.MotionBytes == null || input.MotionBytes.Length == 0)
                {
                    throw new InvalidOperationException("Analysis motion data is empty.");
                }
                if (input.StartFrame < 0 || input.EndFrameExclusive <= input.StartFrame)
                {
                    throw new InvalidOperationException("Analysis frame range is invalid.");
                }

                JObject options = string.IsNullOrWhiteSpace(input.AnalysisOptionsJson)
                    ? new JObject()
                    : JObject.Parse(input.AnalysisOptionsJson);
                options["analysis_only"] = true;
                KimodoBridgeGenerationResult response = KimodoBridgeService.Shared.GenerateAsync(
                    new KimodoGenerationRequestDto
                    {
                        prompt = string.Empty,
                        model = KimodoMotionModelProfiles.NormalizeName(input.ModelName),
                        text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(input.TextEncoderMode),
                        models_root = input.ModelsRoot ?? string.Empty,
                        output_format = "kmb_attachments_v1",
                        analysis_option_json = options.ToString(Formatting.None),
                        analysis_clip_constraints = new List<KimodoKmbClipConstraint>
                        {
                            new KimodoKmbClipConstraint
                            {
                                motionBytes = input.MotionBytes,
                                startFrame = input.StartFrame,
                                endFrameExclusive = input.EndFrameExclusive
                            }
                        }
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(response?.AnalysisJson))
                {
                    throw new InvalidOperationException("Analysis returned no data.");
                }
                if (response.MotionBytes == null || response.MotionBytes.Length == 0)
                {
                    throw new InvalidOperationException("Analysis returned no dense KMB motion.");
                }
                if (!KimodoRawMotionUtility.TryParseFlatBuffer(response.MotionBytes, out KimodoRawMotionData motion, out string parseError) ||
                    !motion.HasFootContacts)
                {
                    throw new InvalidOperationException($"Analysis returned dense KMB without foot contacts: {parseError}");
                }

                analysisJson = response.AnalysisJson;
                motionBytes = response.MotionBytes;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static int GetSelectedPlayableClipCount(KimodoPlayableClip clip)
        {
            return KimodoEditorTimelineSelection.GetSelectedPlayableClips(clip).Count;
        }

        internal static bool TryStartGenerateConnected(
            KimodoPlayableClip clip,
            out KimodoEditorGenerationJobSession session,
            out string error)
        {
            session = null;
            error = string.Empty;
            if (clip == null)
            {
                error = "KimodoPlayableClip is null.";
                return false;
            }

            if (!TryCreateConnectedPlan(
                    clip,
                    out List<ConnectedClipEntry> entries,
                    out KimodoMotionModelProfile profile,
                    out error))
            {
                return false;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (KimodoEditorGenerationJobService.TryGet(entries[i].Clip, out KimodoEditorGenerationJobSession active) &&
                    active != null &&
                    active.IsRunning)
                {
                    error = $"A generation session is already running for '{entries[i].Clip.name}'.";
                    session = active;
                    return false;
                }
            }

            return KimodoEditorGenerationJobService.Start(
                clip,
                async (handle, token) => await GenerateConnectedAsync(
                    entries,
                    profile,
                    (stage, message) => KimodoEditorGenerationJobService.UpdateProgress(
                        clip,
                        handle.RequestId,
                        stage,
                        message),
                    token),
                null,
                out session,
                out error);
        }

        private static bool StartSingle(
            KimodoPlayableClip clip,
            out KimodoEditorGenerationJobSession session,
            out string error)
        {
            return TryStartConfiguredGenerate(
                clip,
                clip,
                null,
                null,
                (handle, result, token) => RunPostGenerationAnalysisAsync(
                    clip,
                    result,
                    (stage, message) => KimodoEditorGenerationJobService.UpdateProgress(
                        clip,
                        handle.RequestId,
                        stage,
                        message)),
                null,
                null,
                out session,
                out error);
        }

        private static Task<KimodoEditorGenerationResult> RunPostGenerationAnalysisAsync(
            KimodoPlayableClip clip,
            KimodoEditorGenerationResult result,
            Action<KimodoBridgeCommandStage, string> progress)
        {
            if (clip == null || !clip.analyzeAfterGeneration)
            {
                if (result != null)
                    result.PostAnalysisSkipped = true;
                return Task.FromResult(result);
            }

            progress?.Invoke(KimodoBridgeCommandStage.Finalize, "Running post-generation animation analysis.");
            if (KimodoEditorFullAnalysisBridge.TryAnalyze(
                    clip,
                    out string analysisJson,
                    out string analysisError))
            {
                result.PostAnalysisJson = analysisJson;
                result.PostAnalysisError = string.Empty;
            }
            else
            {
                if (analysisError?.StartsWith(
                        "Post-generation analysis skipped",
                        System.StringComparison.Ordinal) == true)
                {
                    result.PostAnalysisSkipped = true;
                    result.PostAnalysisError = string.Empty;
                    return Task.FromResult(result);
                }
                result.PostAnalysisError = analysisError;
                Debug.LogWarning($"[Kimodo][PostGenerationAnalysis] {analysisError}");
            }
            return Task.FromResult(result);
        }

        // Shared entry for Inspector and command generation.  Callers configure
        // the KimodoPlayableClip first; this method deliberately supplies no
        // external constraint payload so BuildRequest reads the same serialized
        // UI fields and resolves Timeline neighbors through the common path.
        internal static bool TryStartConfiguredGenerate(
            KimodoPlayableClip clip,
            UnityEngine.Object jobTarget,
            TimelineClip timelineClipOverride,
            Action<KimodoEditorGenerationJobSession> beforeGenerate,
            Func<KimodoEditorGenerationJobSession, KimodoEditorGenerationResult, CancellationToken,
                Task<KimodoEditorGenerationResult>> afterGenerate,
            Action<KimodoEditorGenerationJobSession> finallyGenerate,
            Action<KimodoEditorGenerationJobSession> statusChanged,
            out KimodoEditorGenerationJobSession session,
            out string error)
        {
            session = null;
            error = string.Empty;
            if (clip == null)
            {
                error = "KimodoPlayableClip is null.";
                return false;
            }

            UnityEngine.Object resolvedTarget = jobTarget != null ? jobTarget : clip;
            return KimodoEditorGenerationJobService.Start(
                resolvedTarget,
                async (handle, token) =>
                {
                    try
                    {
                        beforeGenerate?.Invoke(handle);
                        KimodoEditorGenerationResult result = await GenerateAndFinalizeAsync(
                            clip,
                            externalConstraint: null,
                            (stage, message) => KimodoEditorGenerationJobService.UpdateProgress(
                                resolvedTarget,
                                handle.RequestId,
                                stage,
                                message),
                            token,
                            timelineClipOverride);
                        return afterGenerate != null
                            ? await afterGenerate(handle, result, token)
                            : result;
                    }
                    finally
                    {
                        finallyGenerate?.Invoke(handle);
                    }
                },
                statusChanged,
                out session,
                out error);
        }

        internal static bool TryValidateConnectedSelection(
            IReadOnlyList<TimelineClip> selected,
            out string reason)
        {
            var sorted = selected != null ? new List<TimelineClip>(selected) : new List<TimelineClip>();
            sorted.Sort(CompareTimelineClips);
            return TryCreateConnectedPlanEntries(sorted, out _, out _, out reason);
        }

        internal static bool TryGetConnectedClipCount(
            KimodoPlayableClip clip,
            out int count,
            out string reason)
        {
            count = 0;
            if (!TryCreateConnectedPlan(clip, out List<ConnectedClipEntry> entries, out _, out reason))
            {
                return false;
            }

            count = entries.Count;
            return true;
        }

        internal static bool TryGetSelectedCompatibleClipCount(
            KimodoPlayableClip clip,
            out int count)
        {
            count = 0;
            List<TimelineClip> selected = KimodoEditorTimelineSelection.GetSelectedPlayableClips(clip);
            if (selected.Count < 2)
            {
                return false;
            }

            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i]?.asset is not KimodoPlayableClip playable ||
                    !KimodoMotionModelProfiles.TryGet(playable.bridgeModelName, out _))
                {
                    return false;
                }
            }

            count = selected.Count;
            return true;
        }

        private static bool TryCreateConnectedPlan(
            KimodoPlayableClip clip,
            out List<ConnectedClipEntry> entries,
            out KimodoMotionModelProfile profile,
            out string reason)
        {
            entries = new List<ConnectedClipEntry>();
            profile = null;
            reason = string.Empty;
            List<TimelineClip> selected = KimodoEditorTimelineSelection.GetSelectedPlayableClips(clip);
            selected.Sort(CompareTimelineClips);
            return TryCreateConnectedPlanEntries(selected, out entries, out profile, out reason);
        }

        private static bool TryCreateConnectedPlanEntries(
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
                !KimodoMotionModelProfiles.TryGet(firstClip.bridgeModelName, out profile))
            {
                reason = "The selection does not use a supported motion model.";
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

                if (!KimodoMotionModelProfiles.TryGet(playable.bridgeModelName, out KimodoMotionModelProfile currentProfile) ||
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
                if (playable.generateLoop)
                {
                    AddDifference(differences, $"'{playable.name}' uses Generate Loop and must be generated separately");
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

        private static async Task<KimodoEditorGenerationResult> GenerateConnectedAsync(
            List<ConnectedClipEntry> entries,
            KimodoMotionModelProfile profile,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            int groupSeed = entries[0].Clip.randomSeed
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : entries[0].Clip.seed;
            KimodoDebugTimelineWriter.BeginGeneration(entries[0].TimelineClip);
            BuildConnectedRequests(entries, profile, groupSeed, progress, token, out int contextBefore, out int contextAfter);
            KimodoDebugTimelineWriter.TryPrepareGroup(
                entries.Select(entry => new KimodoDebugTimelineWriter.PreparationInput
                {
                    SourceClip = entry.Clip,
                    Request = entry.Request,
                    PassName = "Connected"
                }).ToList());
            int totalFrameCount = entries[entries.Count - 1].StartFrame + entries[entries.Count - 1].FrameCount;
            int runtimeFrameCount = totalFrameCount + contextBefore + contextAfter;
            KimodoEditorGenerateRequest firstRequest = entries[0].Request;
            KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                firstRequest,
                firstRequest.Prompt?.Trim() ?? string.Empty,
                profile.ModelName).GenerationRequest;
            generation.duration = runtimeFrameCount / profile.SourceFps;
            generation.time_as_double = 0.0;
            generation.seed = groupSeed;
            generation.steps = profile.IsArdy
                ? KimodoMotionModelProfiles.ResolveArdyProtocolSteps(firstRequest.DiffusionSteps, profile)
                : KimodoMotionModelProfiles.ClampDiffusionSteps(profile.ModelName, firstRequest.DiffusionSteps);
            generation.constraints.json = ExplicitConstraints(firstRequest.Constraints.json);
            if (profile.IsArdy)
            {
                KimodoEditorGeneratePipeline.PrependArdyHistoryConstraint(
                    generation.constraints.clips,
                    KimodoEditorGeneratePipeline.BuildInitialArdyHistoryPayload(firstRequest, profile),
                    profile);
                generation.ardy_playback_reserve_seconds = 0.0;
            }
            AddTimelineSegments(entries, profile, generation);
            generation.timeline_segments[0].duration += contextBefore / profile.SourceFps;
            generation.timeline_segments[generation.timeline_segments.Count - 1].duration += contextAfter / profile.SourceFps;

            firstRequest.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating connected Timeline KMB...");
            var pipeline = new KimodoBridgeCommand();
            KimodoBridgeCommandResult aggregate = await pipeline.ExecuteAsync(
                new KimodoBridgeCommandRequest { GenerationRequest = generation },
                (stage, message) => progress?.Invoke(stage, message),
                token);
            if (profile.IsArdy)
            {
                KimodoEditorGeneratePipeline.ValidateArdyResult(aggregate, profile, groupSeed);
            }
            else if (aggregate?.MotionData == null)
            {
                throw new InvalidOperationException("Connected Timeline generation returned no motion.");
            }
            if (aggregate.MotionData.FrameCount != runtimeFrameCount)
            {
                throw new InvalidOperationException(
                    $"Connected generation returned {aggregate.MotionData.FrameCount} frames; expected {runtimeFrameCount}.");
            }

            var baked = new List<KimodoEditorGenerationResult>(entries.Count);
            int finalized = 0;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ConnectedClipEntry entry = entries[i];
                    if (!KimodoRawMotionUtility.TrySlice(
                            aggregate.MotionData,
                            entry.StartFrame + contextBefore,
                            entry.FrameCount,
                            out KimodoRawMotionData motion,
                            out string sliceError))
                    {
                        throw new InvalidOperationException(sliceError);
                    }

                    byte[] payload = KimodoRawMotionUtility.ToFlatBuffer(motion, profile.ModelName);
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
                            Message = "Connected Timeline generation complete.",
                            RawStatus = "done",
                            MotionRepFingerprint = aggregate.MotionRepFingerprint,
                            ResolvedSeed = aggregate.ResolvedSeed,
                            StartFrame = entry.StartFrame,
                            EndFrameExclusive = entry.StartFrame + entry.FrameCount
                        }));
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    KimodoPlayableClipGenerationHostService.FinalizeGeneration(
                        entries[i].Clip, entries[i].Request, baked[i], writeDebugTimeline: false);
                    baked[i] = await RunPostGenerationAnalysisAsync(
                        entries[i].Clip,
                        baked[i],
                        (stage, message) => progress?.Invoke(stage, $"[{i + 1}/{entries.Count}] {entries[i].Clip.name}: {message}"));
                    finalized++;
                }

                KimodoDebugTimelineWriter.TryWriteGroup(
                    entries.Select((entry, index) => new KimodoDebugTimelineWriter.RawClipInput
                    {
                        SourceClip = entry.Clip,
                        Request = entry.Request,
                        Result = baked[index]
                    }).ToList());
            }
            catch
            {
                for (int i = finalized; i < entries.Count; i++)
                {
                    entries[i].Request?.CleanupGeneratedClips();
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
            CancellationToken token,
            out int contextBefore,
            out int contextAfter)
        {
            KimodoInOutConstraintTools.ResolveOutsideContextFrames(entries[0].TimelineClip, out contextBefore, out _);
            KimodoInOutConstraintTools.ResolveOutsideContextFrames(entries[entries.Count - 1].TimelineClip, out _, out contextAfter);
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
                    enableAutoBeginAnchor: i == 0,
                    timelineClipOverride: entry.TimelineClip);
                AppendConnectedBoundarySamples(entry, i, entries.Count, contextBefore);
                entry.Request.Progress = PrefixProgress(progress, i, entries.Count);
                if (string.IsNullOrWhiteSpace(entry.Request.Prompt))
                {
                    throw new InvalidOperationException($"Prompt is empty on selected clip '{entry.Clip.name}'.");
                }
            }

            var allClipConstraints = new List<KimodoClipConstraint>();
            for (int i = 0; i < entries.Count; i++)
            {
                float timeOffset = (entries[i].StartFrame + contextBefore) / profile.SourceFps;
                foreach (KimodoClipConstraint constraint in entries[i].Request.Constraints.clips)
                {
                    if (constraint == null)
                    {
                        continue;
                    }
                    allClipConstraints.Add(new KimodoClipConstraint
                    {
                        motionBytes = constraint.motionBytes,
                        startTime = constraint.startTime + timeOffset,
                        duration = constraint.duration,
                        mask = constraint.mask
                    });
                }
            }
            entries[0].Request.Constraints.clips = allClipConstraints;

            var allSamples = new List<KimodoMarkerSampleResult>();
            var sampleTimeOffsets = new List<double>();
            for (int i = 0; i < entries.Count; i++)
            {
                double timeOffset = (entries[i].StartFrame + contextBefore) / (double)profile.SourceFps;
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
                int totalFrameCount = entries[entries.Count - 1].StartFrame + entries[entries.Count - 1].FrameCount +
                    contextBefore + contextAfter;
                firstRequest.Constraints.json = KimodoConstraintJsonExporter.ToConstraintsJson(
                    allSamples,
                    ResolveExportContext(
                        entries[0].TimelineClip,
                        entries[0].Request?.TimelineContextSnapshot),
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
            var segments = new List<KimodoTimelineSegmentDto>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                ConnectedClipEntry entry = entries[i];
                segments.Add(new KimodoTimelineSegmentDto
                {
                    prompt = entry.Request.Prompt?.Trim() ?? string.Empty,
                    duration = (float)entry.DurationSeconds
                });
            }
            generation.timeline_segments = segments;
        }

        private static void AppendConnectedBoundarySamples(
            ConnectedClipEntry entry,
            int index,
            int count,
            int groupContextBefore)
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

            if (!KimodoMotionModelProfiles.TryGetArdy(entry.Request.ModelName, out _))
            {
                // Each boundary is sampled in local generation time. Outside history belongs
                // before this clip; only the outermost group windows extend the generated result.
                KimodoInOutConstraintTools.ResolveOutsideContextFrames(entry.TimelineClip, out int before, out int after);
                before = Math.Min(before, entry.StartFrame + groupContextBefore);
                if (request.Mode == KimodoInOutConstraintMode.Outside)
                {
                    request.EnableBegin &= before > 0;
                    request.BeginWindowFrames = before;
                    request.OutputStartFrame = before;
                    request.OutputFrameCount = entry.FrameCount;
                }
                if (!enableOut) after = 0;
                request.GenerationFrames += before + after;
                if (!KimodoInOutConstraintTools.TrySampleBoundaries(request,
                    out var begins, out var ends, out _, out error))
                    throw new InvalidOperationException($"Build connected clip constraints failed: {error}");
                begins.AddRange(ends);
                foreach (var sample in begins)
                {
                    sample.sampleTime -= before / (double)entry.Request.TargetFrameRate;
                    entry.Request.ConstraintSamples.Add(sample);
                }
                return;
            }

            bool hasBegin = request.EnableBegin;
            request.EnableBegin = false;
            if (!KimodoInOutConstraintTools.TrySampleBoundaryPair(
                    request,
                    out _,
                    out KimodoMarkerSampleResult endSample,
                    out _,
                    out error))
            {
                throw new InvalidOperationException($"Build connected clip constraints failed: {error}");
            }

            if (hasBegin)
            {
                entry.Request.Constraints.clips.Insert(0, KimodoTimelineClipConstraintBuilder.BuildBegin(
                    clip,
                    entry.TimelineClip,
                    entry.Request.ModelName,
                    entry.Request.TargetFrameRate,
                    0f,
                    entry.Request.Token));
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

        private static KimodoConstraintExportContext ResolveExportContext(
            TimelineClip timelineClip,
            KimodoTimelineInOutConstraintContext resolvedContext = null)
        {
            KimodoTimelineInOutConstraintContext context = resolvedContext;
            if (context == null && timelineClip != null)
            {
                KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip,
                    out context,
                    out _);
            }
            if (context != null)
            {
                return new KimodoConstraintExportContext
                {
                    projectedPoseProjector = KimodoConstraintExportProjector.Create(context)
                };
            }
            return new KimodoConstraintExportContext();
        }

        internal static int CompareTimelineClips(TimelineClip left, TimelineClip right)
        {
            int byStart = (left?.start ?? 0.0).CompareTo(right?.start ?? 0.0);
            return byStart != 0 ? byStart : (left?.end ?? 0.0).CompareTo(right?.end ?? 0.0);
        }

        private static async Task<KimodoEditorGenerationResult> GenerateSelectedAndFinalizeAsync(
            IReadOnlyList<KimodoPlayableClip> clips,
            IReadOnlyList<TimelineClip> timelineClips,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            KimodoEditorGenerationResult result = null;
            for (int i = 0; i < clips.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                KimodoPlayableClip selectedClip = clips[i];
                string prefix = $"[{i + 1}/{clips.Count}] {selectedClip.name}";
                result = await GenerateAndFinalizeAsync(
                    selectedClip,
                    externalConstraint: null,
                    (stage, message) => progress?.Invoke(stage, $"{prefix}: {message}"),
                    token,
                    timelineClipOverride: timelineClips != null && i < timelineClips.Count ? timelineClips[i] : null,
                    muteDebugTracksBeforeStart: true);
                token.ThrowIfCancellationRequested();
                result = await RunPostGenerationAnalysisAsync(
                    selectedClip,
                    result,
                    (stage, message) => progress?.Invoke(stage, $"{prefix}: {message}"));
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

        internal static async Task<KimodoEditorGenerationResult> GenerateAndFinalizeAsync(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token,
            TimelineClip timelineClipOverride = null,
            bool muteDebugTracksBeforeStart = true)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("KimodoPlayableClip is null.");
            }

            if (muteDebugTracksBeforeStart)
            {
                KimodoDebugTimelineWriter.BeginGeneration(
                    timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(clip));
            }

            string prompt = clip.motionPrompt ?? string.Empty;
            if (RequiresStage2Generation(clip, externalConstraint, timelineClipOverride))
            {
                return await GenerateStagedAndFinalizeAsync(
                    clip,
                    prompt,
                    externalConstraint,
                    progress,
                    token,
                    timelineClipOverride);
            }
            if (KimodoPlayableClipGenerationHostService.TryGetClipConstraintAvatarMask(
                    clip,
                    out _) &&
                !KimodoMotionModelProfiles.TryGetArdy(clip.bridgeModelName, out _))
            {
                return await GenerateClipConstraintBakedAsync(
                    clip,
                    prompt,
                    externalConstraint,
                    progress,
                    token,
                    timelineClipOverride);
            }

            KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                clip,
                prompt,
                externalConstraint,
                token,
                timelineClipOverride: timelineClipOverride);
            string modelName = KimodoMotionModelProfiles.NormalizeName(clip.bridgeModelName);

            try
            {
                request.Progress = progress;
                KimodoEditorGenerationResult result;
                if (request.EffectiveRuntimeFrameCount > KimodoMotionModelProfiles.MaxGenerationFrames)
                {
                    if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _) ||
                        (request.Constraints?.clips?.Count ?? 0) > 0)
                    {
                        throw new InvalidOperationException(
                            "Long-clip window generation currently supports standard models and raw constraints only.");
                    }

                    KimodoBridgeCommandResult combinedMotion = await GenerateWindowedRuntimeAsync(
                        request,
                        prompt,
                        modelName);
                    result = KimodoEditorGeneratePipeline.BakeRuntimeResult(
                        request,
                        prompt,
                        modelName,
                        combinedMotion);
                }
                else
                {
                    KimodoDebugTimelineWriter.TryPrepare(clip, request, "Generation");
                    result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
                }
                token.ThrowIfCancellationRequested();
                KimodoPlayableClipGenerationHostService.FinalizeGeneration(clip, request, result);
                return result;
            }
            catch
            {
                request.CleanupGeneratedClips();
                throw;
            }
        }

        internal static bool RequiresStage2Generation(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            TimelineClip timelineClipOverride = null)
        {
            bool hasDeferredTimelineBoundary = clip.inOutConstraintMode != KimodoInOutConstraintMode.None &&
                ((clip.postApplyInConstraint && clip.enableInConstraint) ||
                 (clip.postApplyOutConstraint && clip.enableOutConstraint));
            bool hasDeferredExternalBoundary = externalConstraint?.Enabled == true &&
                ((clip.postApplyInConstraint && externalConstraint.DeferredInConstraintSamples?.Count > 0) ||
                 (clip.postApplyOutConstraint && externalConstraint.DeferredOutConstraintSamples?.Count > 0));
            return KimodoPlayableClipGenerationHostService.IsLoopGenerationEnabled(clip, timelineClipOverride) ||
                clip.overridePathAngle || clip.overridePathDistance || clip.overrideHeading ||
                hasDeferredTimelineBoundary || hasDeferredExternalBoundary;
        }

        internal static Vector3? ResolveStage1PathRootOrigin(KimodoEditorGenerateRequest stage1Request)
        {
            if (stage1Request == null)
                return null;
            if (stage1Request.UseRelativeOffset)
                return stage1Request.RelativeOffset;

            if (stage1Request.HasSyntheticAutoBeginConstraint && stage1Request.ConstraintSamples != null)
            {
                for (int i = 0; i < stage1Request.ConstraintSamples.Count; i++)
                {
                    KimodoMarkerSampleResult sample = stage1Request.ConstraintSamples[i];
                    if (sample != null && string.Equals(
                            sample.constraintMode, "root2d", StringComparison.OrdinalIgnoreCase))
                    {
                        return sample.rootOverride.t;
                    }
                }
            }

            KimodoTimelineInOutConstraintContext context = stage1Request.TimelineContextSnapshot;
            if (context == null)
                return null;
            if (!context.HasOriginalRootSnapshot)
            {
                context.HasOriginalRootSnapshot = KimodoInOutConstraintTools.TryCaptureOriginalRoot2D(
                    context.Animator,
                    context.Director,
                    out context.OriginalRootPosition,
                    out context.OriginalRootRotation);
            }
            return context.HasOriginalRootSnapshot ? context.OriginalRootPosition : (Vector3?)null;
        }

        private static bool HasFullBodyOrEffectorConstraint(string constraintsJson)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson))
                return false;
            JToken parsed = JToken.Parse(constraintsJson);
            if (parsed is not JArray constraints)
                return false;
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                string type = constraint.Value<string>("type")?.Trim().ToLowerInvariant();
                if (type == "fullbody" || type == "effector" || type == "end-effector" ||
                    type == "left-hand" || type == "right-hand" ||
                    type == "left-foot" || type == "right-foot")
                    return true;
            }
            return false;
        }

        internal static bool RequiresStage3Generation(
            bool generateLoop,
            bool hasStage3BoundaryWindow,
            bool hasStage3BodyComposition)
        {
            return generateLoop || hasStage3BoundaryWindow || hasStage3BodyComposition;
        }

        internal static int ResolveConstraintFrameSpan(
            string constraintsJson,
            out int firstFrame,
            out int lastFrame)
        {
            firstFrame = int.MaxValue;
            lastFrame = -1;
            if (string.IsNullOrWhiteSpace(constraintsJson))
                return 0;
            JToken parsed = JToken.Parse(constraintsJson);
            if (parsed is not JArray constraints)
                return 0;
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                if (constraint["frame_indices"] is not JArray frames)
                    continue;
                foreach (JToken frame in frames)
                {
                    if (frame.Type != JTokenType.Integer)
                        continue;
                    int value = frame.Value<int>();
                    firstFrame = Math.Min(firstFrame, value);
                    lastFrame = Math.Max(lastFrame, value);
                }
            }
            return lastFrame < firstFrame ? 0 : lastFrame - firstFrame + 1;
        }

        private static async Task<KimodoBridgeCommandResult> GenerateWindowedRuntimeAsync(
            KimodoEditorGenerateRequest request,
            string prompt,
            string modelName)
        {
            int totalFrameCount = request.EffectiveRuntimeFrameCount;
            float frameRate = request.TargetFrameRate;
            int minimumCoreFrames = Mathf.Max(1, Mathf.CeilToInt(3f * frameRate));
            int maximumCoreFrames = Mathf.Max(1, Mathf.FloorToInt(8f * frameRate));
            int preferredCoreFrames = Mathf.Max(1, Mathf.RoundToInt(6f * frameRate));
            ResolveLongClipWindowConfiguration(
                request,
                KimodoMotionModelProfiles.MaxGenerationFrames,
                minimumCoreFrames,
                out int overlapFrames,
                out int overlapSampleCount);
            List<KimodoGlobalRawConstraint> anchors = KimodoGenerationWindowPlanner.ReadRootAnchors(
                request.Constraints?.json);
            List<KimodoGenerationWindow> windows = KimodoGenerationWindowPlanner.Plan(
                totalFrameCount,
                frameRate,
                KimodoMotionModelProfiles.MaxGenerationFrames,
                overlapFrames,
                minimumCoreFrames,
                maximumCoreFrames,
                preferredCoreFrames,
                anchors,
                Mathf.Max(0, Mathf.RoundToInt(frameRate * 0.5f)));

            int originalTargetFrameCount = request.TargetFrameCount;
            int originalRuntimeFrameCount = request.RuntimeFrameCount;
            int originalRuntimeTrimStartFrame = request.RuntimeTrimStartFrame;
            string originalConstraintsJson = request.Constraints?.json ?? string.Empty;
            string originalAnalysisOptionsJson = request.AnalysisOptionsJson ?? string.Empty;
            var coreMotions = new List<KimodoRawMotionData>(windows.Count);
            var generatedWindows = new List<KimodoRawMotionData>(windows.Count);
            bool captureDebugPasses =
                KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips;
            if (captureDebugPasses)
            {
                request.DebugConstraintPasses.Clear();
            }
            try
            {
                for (int index = 0; index < windows.Count; index++)
                {
                    request.Token.ThrowIfCancellationRequested();
                    KimodoGenerationWindow window = windows[index];
                    int windowConstraintPassStartIndex = request.DebugConstraintPasses.Count;
                    request.TargetFrameCount = window.RequestFrameCount;
                    request.RuntimeFrameCount = window.RequestFrameCount;
                    request.RuntimeTrimStartFrame = 0;
                    request.AnalysisOptionsJson = string.Empty;
                    string windowConstraintsJson = KimodoGenerationWindowPlanner.BuildWindowConstraintsJson(
                        window,
                        originalConstraintsJson);
                    request.Constraints.json = windowConstraintsJson;
                    CaptureWindowConstraintPass(
                        request,
                        captureDebugPasses,
                        $"Window {index + 1} Base Constraints",
                        windowConstraintsJson,
                        windowConstraintsJson,
                        modelName,
                        window,
                        frameRate);

                    int overlapStartFrame = -1;
                    int overlapEndFrame = -1;
                    KimodoGenerationWindow previousWindow = null;
                    if (index > 0)
                    {
                        previousWindow = windows[index - 1];
                        KimodoRawMotionData previousMotion = generatedWindows[index - 1];
                        if (!KimodoGenerationWindowPlanner.TryGetOverlapBoundaryFrames(
                                previousWindow,
                                window,
                                out overlapStartFrame,
                                out overlapEndFrame))
                        {
                            throw new InvalidOperationException("Generation windows do not contain a valid overlap.");
                        }

                        string fullBodyJson = BuildPreviousWindowOverlapFullBodyConstraintsJson(
                            previousMotion,
                            modelName,
                            previousWindow,
                            window,
                            overlapStartFrame,
                            overlapEndFrame,
                            frameRate,
                            overlapSampleCount);
                        request.Constraints.json = KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                            request.Constraints.json,
                            fullBodyJson);
                        CaptureWindowConstraintPass(
                            request,
                            captureDebugPasses,
                            $"Window {index + 1} Previous-Window Overlap FullBody",
                            fullBodyJson,
                            request.Constraints.json,
                            modelName,
                            window,
                            frameRate);
                    }

                    if (index + 1 < windows.Count)
                    {
                        KimodoGenerationWindow nextWindow = windows[index + 1];
                        if (KimodoGenerationWindowPlanner.TryGetOverlapBoundaryFrames(
                                window,
                                nextWindow,
                                out int nextOverlapStartFrame,
                                out int nextOverlapEndFrame))
                        {
                            AppendInterpolatedOverlapRootConstraints(
                                request,
                                captureDebugPasses,
                                window,
                                nextOverlapStartFrame,
                                nextOverlapEndFrame,
                                anchors,
                                modelName,
                                frameRate,
                                index + 1);
                        }
                    }

                    request.Progress?.Invoke(
                        KimodoBridgeCommandStage.Constraint,
                        $"Generating window {index + 1}/{windows.Count} ({window.RequestFrameCount} frames)...");
                    string windowPassName = $"Long Clip Window {index + 1}/{windows.Count}";
                    KimodoDebugTimelineReservation windowReservation = null;
                    if (captureDebugPasses &&
                        KimodoDebugTimelineWriter.TryResolveWindowPlaybackTiming(
                            request.TimelineClipSnapshot,
                            window.RequestFrameCount,
                            frameRate,
                            out double reservedTimelineDuration,
                            out double reservedTimelineTimeScale))
                    {
                        KimodoDebugTimelineWriter.TryPrepare(
                            request.TimelineClipSnapshot.asset as KimodoPlayableClip,
                            request,
                            windowPassName,
                            hasTimelineTimingOverride: true,
                            timelineDuration: reservedTimelineDuration,
                            timelineTimeScale: reservedTimelineTimeScale);
                        windowReservation = request.DebugTimelineReservation;
                        if (windowReservation != null)
                        {
                            for (int passIndex = windowConstraintPassStartIndex;
                                 passIndex < request.DebugConstraintPasses.Count;
                                 passIndex++)
                            {
                                if (request.DebugConstraintPasses[passIndex] != null)
                                {
                                    request.DebugConstraintPasses[passIndex].DebugClipTarget =
                                        windowReservation.TimelineClip.asset as KimodoPlayableClip;
                                }
                            }
                        }
                    }
                    else if (captureDebugPasses)
                    {
                        Debug.LogWarning(
                            $"[Kimodo][DebugTimeline] Could not reserve long-clip window {index + 1} on the Timeline.");
                    }
                    KimodoBridgeCommandResult result = await KimodoEditorGeneratePipeline.ExecuteRuntimePipelineAsync(
                        request,
                        prompt,
                        modelName);
                    if (result?.MotionData == null || result.MotionData.FrameCount != window.RequestFrameCount)
                    {
                        throw new InvalidOperationException(
                            $"Window {index + 1} returned {result?.MotionData?.FrameCount ?? 0} frames; " +
                            $"expected {window.RequestFrameCount}.");
                    }

                    generatedWindows.Add(result.MotionData);
                    int coreStartInRequest = window.CoreStartFrame - window.RequestStartFrame;
                    if (!KimodoRawMotionUtility.TrySlice(
                            result.MotionData,
                            coreStartInRequest,
                            window.CoreFrameCount,
                            out KimodoRawMotionData coreMotion,
                            out string sliceError))
                    {
                        throw new InvalidOperationException($"Window {index + 1} core trim failed: {sliceError}");
                    }

                    if (windowReservation != null)
                    {
                        KimodoDebugTimelineWriter.TryWriteRawMotion(
                            request.TimelineClipSnapshot.asset as KimodoPlayableClip,
                            request,
                            windowReservation,
                            result.MotionData);
                    }
                    request.DebugTimelineReservation = null;

                    coreMotions.Add(coreMotion);
                }

                if (!KimodoRawMotionUtility.TryConcatenate(
                        coreMotions,
                        totalFrameCount,
                        out KimodoRawMotionData combined,
                        out string concatenateError))
                {
                    throw new InvalidOperationException($"Generated windows could not be joined: {concatenateError}");
                }

                request.TargetFrameCount = originalTargetFrameCount;
                request.RuntimeFrameCount = originalRuntimeFrameCount;
                request.RuntimeTrimStartFrame = originalRuntimeTrimStartFrame;
                request.Constraints.json = originalConstraintsJson;
                request.AnalysisOptionsJson = originalAnalysisOptionsJson;
                var combinedResult = new KimodoBridgeCommandResult
                {
                    MotionData = combined,
                    MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(combined),
                    MotionFormat = "json",
                    RawStatus = "done"
                };
                combinedResult = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                    request,
                    combinedResult,
                    modelName);
                if (!string.IsNullOrWhiteSpace(originalAnalysisOptionsJson))
                {
                    JObject analysis = await AnalyzeMergedMotionAsync(
                        request,
                        modelName,
                        combinedResult.MotionData,
                        originalAnalysisOptionsJson,
                        request.Progress,
                        request.Token);
                    combinedResult.AnalysisJson = analysis.ToString(Formatting.None);
                }
                return combinedResult;
            }
            finally
            {
                request.TargetFrameCount = originalTargetFrameCount;
                request.RuntimeFrameCount = originalRuntimeFrameCount;
                request.RuntimeTrimStartFrame = originalRuntimeTrimStartFrame;
                request.AnalysisOptionsJson = originalAnalysisOptionsJson;
                if (request.Constraints != null)
                    request.Constraints.json = originalConstraintsJson;
            }
        }

        internal static string BuildPreviousWindowOverlapFullBodyConstraintsJson(
            KimodoRawMotionData previousMotion,
            string modelName,
            KimodoGenerationWindow previousWindow,
            KimodoGenerationWindow currentWindow,
            int overlapStartFrame,
            int overlapEndFrame,
            float frameRate,
            int sampleCount = 2)
        {
            if (previousMotion == null || previousWindow == null || currentWindow == null ||
                overlapEndFrame < overlapStartFrame || frameRate <= 0f)
            {
                throw new InvalidOperationException("The previous-window overlap is invalid.");
            }

            int previousStart = overlapStartFrame - previousWindow.RequestStartFrame;
            int previousEnd = overlapEndFrame - previousWindow.RequestStartFrame;
            int currentStart = overlapStartFrame - currentWindow.RequestStartFrame;
            int currentEnd = overlapEndFrame - currentWindow.RequestStartFrame;
            if (previousStart < 0 || previousEnd >= previousMotion.FrameCount ||
                currentStart < 0 || currentEnd >= currentWindow.RequestFrameCount)
            {
                throw new InvalidOperationException("The overlap boundary is outside a generation window.");
            }

            int overlapFrameCount = overlapEndFrame - overlapStartFrame + 1;
            int resolvedSampleCount = Mathf.Clamp(sampleCount, 1, overlapFrameCount);
            int[] previousFrames = BuildEvenlySpacedInclusiveFrames(
                previousStart,
                previousEnd,
                resolvedSampleCount);
            int[] currentFrames = BuildEvenlySpacedInclusiveFrames(
                currentStart,
                currentEnd,
                resolvedSampleCount);
            string fullBodyJson = KimodoRawMotionConstraintBuilder.BuildFullBodyConstraintsJson(
                previousMotion,
                modelName,
                previousFrames,
                clipDurationSeconds: currentWindow.RequestFrameCount / (double)frameRate);
            JArray fullBody = JArray.Parse(fullBodyJson);
            if (fullBody.Count == 0 || !(fullBody[0] is JObject constraint))
            {
                throw new InvalidOperationException("The previous-window overlap produced no FullBody constraint.");
            }

            constraint["frame_indices"] = new JArray(currentFrames);
            return fullBody.ToString(Formatting.None);
        }

        private static int[] BuildEvenlySpacedInclusiveFrames(int startFrame, int endFrame, int sampleCount)
        {
            if (endFrame < startFrame)
                throw new InvalidOperationException("The overlap frame range is reversed.");
            int count = Mathf.Clamp(sampleCount, 1, endFrame - startFrame + 1);
            if (count == 1)
                return new[] { startFrame };

            var frames = new int[count];
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                frames[i] = Mathf.RoundToInt(Mathf.Lerp(startFrame, endFrame, t));
            }
            return frames;
        }

        private static void AppendInterpolatedOverlapRootConstraints(
            KimodoEditorGenerateRequest request,
            bool capture,
            KimodoGenerationWindow window,
            int overlapStartFrame,
            int overlapEndFrame,
            IReadOnlyList<KimodoGlobalRawConstraint> anchors,
            string modelName,
            float frameRate,
            int windowIndex)
        {
            if (request?.Constraints == null || window == null)
                return;
            if (!KimodoGenerationWindowPlanner.TryInterpolateRoot2DForOverlap(
                    anchors,
                    overlapStartFrame,
                    overlapEndFrame,
                    out KimodoGlobalRawConstraint startRoot,
                    out KimodoGlobalRawConstraint endRoot))
            {
                return;
            }

            var roots = new JArray();
            if (startRoot != null)
            {
                roots.Add(KimodoGenerationWindowPlanner.CreateRoot2DConstraint(
                    overlapStartFrame - window.RequestStartFrame,
                    startRoot.RootPosition,
                    startRoot.Heading));
            }
            if (endRoot != null)
            {
                roots.Add(KimodoGenerationWindowPlanner.CreateRoot2DConstraint(
                    overlapEndFrame - window.RequestStartFrame,
                    endRoot.RootPosition,
                    endRoot.Heading));
            }
            if (roots.Count == 0)
                return;

            string rootConstraintsJson = roots.ToString(Formatting.None);
            request.Constraints.json = KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                request.Constraints.json,
                rootConstraintsJson);
            CaptureWindowConstraintPass(
                request,
                capture,
                $"Window {windowIndex} Overlap Root2D",
                rootConstraintsJson,
                request.Constraints.json,
                modelName,
                window,
                frameRate);
        }

        private static void CaptureWindowConstraintPass(
            KimodoEditorGenerateRequest request,
            bool capture,
            string name,
            string addedConstraintsJson,
            string combinedConstraintsJson,
            string modelName,
            KimodoGenerationWindow window,
            float frameRate)
        {
            if (!capture || string.IsNullOrWhiteSpace(addedConstraintsJson) || window == null) return;
            var pass = new KimodoDebugConstraintPass
            {
                Name = name,
                AddedConstraintsJson = addedConstraintsJson,
                CombinedConstraintsJson = combinedConstraintsJson ?? string.Empty,
                ModelName = modelName,
                FrameRate = Mathf.RoundToInt(frameRate),
                FrameCount = window.RequestFrameCount
            };
            request.DebugConstraintPasses.Add(pass);
        }

        private static async Task<KimodoEditorGenerationResult> GenerateStagedAndFinalizeAsync(
            KimodoPlayableClip clip,
            string prompt,
            KimodoExternalConstraintRequest externalConstraint,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token,
            TimelineClip timelineClipOverride)
        {
            KimodoEditorGenerateRequest firstRequest = null;
            KimodoEditorGenerateRequest stage2Request = null;
            KimodoEditorGenerateRequest finalRequest = null;
            string modelName = KimodoMotionModelProfiles.NormalizeName(clip.bridgeModelName);
            bool loopRequested = KimodoPlayableClipGenerationHostService.IsLoopGenerationEnabled(
                clip, timelineClipOverride);
            bool timelineOutEnabled = clip.inOutConstraintMode != KimodoInOutConstraintMode.None &&
                clip.enableOutConstraint &&
                (externalConstraint?.Enabled != true || externalConstraint.IncludeTimelineConstraints);
            bool externalOutEnabled = externalConstraint?.Enabled == true &&
                externalConstraint.DeferredOutConstraintSamples?.Count > 0;
            bool hasOutPriority = timelineOutEnabled || externalOutEnabled;
            bool generateLoop = loopRequested && !hasOutPriority;
            if (loopRequested && hasOutPriority)
                Debug.LogWarning("[Kimodo][Stage 3] Loop window skipped because the Out constraint has priority.", clip);
            try
            {
                firstRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    prompt,
                    externalConstraint,
                    token,
                    timelineClipOverride: timelineClipOverride,
                    generateLoopOverride: false,
                    enableAutoBeginAnchor: !generateLoop,
                    deferOutConstraint: clip.postApplyOutConstraint,
                    deferInConstraint: clip.postApplyInConstraint);
                Vector3? stage1PathRootOrigin = clip.overridePathAngle
                    ? ResolveStage1PathRootOrigin(firstRequest)
                    : (Vector3?)null;
                firstRequest.AnalysisOptionsJson = string.Empty;
                firstRequest.Progress = (stage, message) =>
                    progress?.Invoke(stage, $"Stage 1 baseline: {message}");
                KimodoDebugTimelineWriter.TryPrepare(clip, firstRequest, "Stage 1 Baseline");
                string firstPassPrompt = firstRequest.Prompt?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(firstPassPrompt))
                {
                    throw new InvalidOperationException("Prompt is empty.");
                }
                token.ThrowIfCancellationRequested();
                KimodoBridgeCommandResult firstResult = await KimodoEditorGeneratePipeline.ExecuteRuntimePipelineAsync(
                    firstRequest,
                    firstPassPrompt,
                    modelName);
                if (KimodoMotionModelProfiles.TryGetArdy(modelName, out _) &&
                    firstRequest.RuntimeTrimStartFrame > 0)
                {
                    firstResult = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                        firstRequest,
                        firstResult,
                        modelName);
                }
                token.ThrowIfCancellationRequested();

                int effectiveSeed = firstRequest.EffectiveSeed;
                finalRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    prompt,
                    externalConstraint,
                    token,
                    effectiveSeedOverride: effectiveSeed,
                    timelineClipOverride: timelineClipOverride,
                    enableAutoBeginAnchor: false,
                    generateLoopOverride: false,
                    deferOutConstraint: clip.postApplyOutConstraint,
                    deferInConstraint: clip.postApplyInConstraint);
                stage2Request = finalRequest;
                finalRequest.DebugTimelineGroup = firstRequest.DebugTimelineGroup;
                KimodoRawMotionData motion = ParseFirstPassMotion(firstRequest, firstResult, "Trajectory");
                var stage1 = new KimodoEditorGenerationStage(1, "Baseline", firstRequest)
                {
                    ResultMotion = motion
                };
                var stage2 = new KimodoEditorGenerationStage(2, "Path and heading", finalRequest, stage1.ResultMotion);
                KimodoPlayableClip firstDebugClip = firstRequest.DebugTimelineReservation?.TimelineClip?.asset as KimodoPlayableClip;
                KimodoDebugTimelineWriter.TryWriteRawMotion(
                    clip,
                    firstRequest,
                    firstRequest.DebugTimelineReservation,
                    firstRequest.DebugRuntimeMotion ?? motion);
                firstRequest.DebugTimelineReservation = null;
                finalRequest.DebugTimelineReservation = null;
                List<KimodoDebugConstraintPass> debugConstraintPasses =
                    KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips
                        ? new List<KimodoDebugConstraintPass>()
                        : null;
                if (KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips)
                {
                    debugConstraintPasses.Add(new KimodoDebugConstraintPass
                    {
                        Name = "Stage 1 Initial Constraints",
                        AddedConstraintsJson = firstRequest.Constraints?.json ?? string.Empty,
                        CombinedConstraintsJson = firstRequest.Constraints?.json ?? string.Empty,
                        ModelName = modelName,
                        FrameRate = Mathf.RoundToInt(firstRequest.TargetFrameRate),
                        FrameCount = firstRequest.EffectiveRuntimeFrameCount,
                    DebugClipTarget = firstDebugClip
                    });
                }
                string inheritedStage1Constraints = ShiftConstraintFrames(
                    firstRequest.Constraints.json,
                    finalRequest.RuntimeTrimStartFrame - firstRequest.RuntimeTrimStartFrame,
                    finalRequest.EffectiveRuntimeFrameCount);
                stage2.AddPass(new KimodoEditorGenerationPass(
                    "Stage 1 constraint inheritance",
                    _ => inheritedStage1Constraints));
                if (clip.overridePathAngle)
                {
                    stage2.AddPass(new KimodoEditorGenerationPass("Path Angle", currentConstraints =>
                    {
                        string pathConstraintJson = KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                            motion,
                            modelName,
                            clip.pathBeginAngleDegrees,
                            clip.pathEndAngleDegrees,
                            finalRequest.RuntimeTrimStartFrame,
                            finalRequest.TargetFrameCount,
                            finalRequest.EffectiveRuntimeFrameCount,
                            finalRequest.TargetFrameRate,
                            currentConstraints,
                            clip.overrideHeading
                                ? KimodoRawMotionConstraintBuilder.HeadingOverrideFrameInterval
                                : 0,
                            clip.overridePathDistance ? Mathf.Max(0f, clip.pathDistance) : -1f,
                            stage1PathRootOrigin);
                        if (clip.overrideHeading)
                        {
                            pathConstraintJson = KimodoRawMotionConstraintBuilder.OverrideRoot2DHeadingsJson(
                                pathConstraintJson,
                                clip.headingDegrees);
                        }
                        string combined = KimodoClipConstraintBakeUtility.ReplaceRoot2DConstraintsJson(
                            currentConstraints,
                            pathConstraintJson);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 2 Path Angle",
                            AddedConstraintsJson = pathConstraintJson,
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                else if (clip.overridePathDistance)
                {
                    stage2.AddPass(new KimodoEditorGenerationPass("Path Distance", currentConstraints =>
                    {
                        string pathConstraintJson = KimodoRawMotionConstraintBuilder.BuildPathDistanceConstraintJson(
                            motion,
                            modelName,
                            Mathf.Max(0f, clip.pathDistance),
                            finalRequest.RuntimeTrimStartFrame,
                            finalRequest.TargetFrameCount,
                            finalRequest.EffectiveRuntimeFrameCount,
                            finalRequest.TargetFrameRate,
                            currentConstraints);
                        if (clip.overrideHeading)
                        {
                            pathConstraintJson = KimodoRawMotionConstraintBuilder.OverrideRoot2DHeadingsJson(
                                pathConstraintJson,
                                clip.headingDegrees);
                        }
                        string combined = KimodoClipConstraintBakeUtility.ReplaceRoot2DConstraintsJson(
                            currentConstraints,
                            pathConstraintJson);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 2 Path Distance",
                            AddedConstraintsJson = pathConstraintJson,
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                if (clip.overrideHeading && !clip.overridePathAngle && !clip.overridePathDistance)
                {
                    stage2.AddPass(new KimodoEditorGenerationPass("Heading", currentConstraints =>
                    {
                        string headingConstraintJson = KimodoRawMotionConstraintBuilder.BuildHeadingOverrideConstraintJson(
                            motion,
                            clip.headingDegrees,
                            finalRequest.RuntimeTrimStartFrame,
                            finalRequest.TargetFrameCount,
                            finalRequest.EffectiveRuntimeFrameCount,
                            finalRequest.TargetFrameRate,
                            currentConstraints);
                        string combined = KimodoClipConstraintBakeUtility.ReplaceRoot2DConstraintsJson(
                            currentConstraints,
                            headingConstraintJson);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 2 Heading",
                            AddedConstraintsJson = headingConstraintJson,
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                string combinedConstraints = stage2.ApplyPasses();
                finalRequest.Constraints.json = combinedConstraints;
                firstRequest = null;

                finalRequest.Progress = (stage, message) =>
                    progress?.Invoke(stage, $"Stage 2 refinements: {message}");
                KimodoDebugTimelineWriter.TryPrepare(clip, finalRequest, "Stage 2 Refinements");
                KimodoPlayableClip finalDebugClip = finalRequest.DebugTimelineReservation?.TimelineClip?.asset as KimodoPlayableClip;
                if (debugConstraintPasses != null)
                {
                    if (finalDebugClip != null &&
                        !string.IsNullOrWhiteSpace(finalRequest.Constraints?.json))
                    {
                        debugConstraintPasses.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 2 Constraints",
                            AddedConstraintsJson = finalRequest.Constraints.json,
                            CombinedConstraintsJson = finalRequest.Constraints.json,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount,
                            DebugClipTarget = finalDebugClip
                        });
                    }
                    for (int i = 0; i < debugConstraintPasses.Count; i++)
                    {
                        if (debugConstraintPasses[i] != null && debugConstraintPasses[i].DebugClipTarget == null)
                            debugConstraintPasses[i].DebugClipTarget = finalDebugClip;
                    }
                }
                KimodoEditorGenerationResult stage2Result = await KimodoEditorGeneratePipeline.ExecuteAsync(finalRequest);
                token.ThrowIfCancellationRequested();
                stage2.ResultMotion = ParseFirstPassMotion(finalRequest, stage2Result, "Stage 2");
                // Stage 2 is an intermediate generation and is not finalized through
                // the host service. Fill its reserved debug clip explicitly before
                // the request is reused for Stage 3 or finalized as the output.
                KimodoDebugTimelineWriter.TryWriteRawMotion(
                    clip,
                    stage2Request,
                    stage2Request.DebugTimelineReservation,
                    stage2.ResultMotion);
                stage2Request.DebugTimelineReservation = null;
                bool hasStage3BoundaryWindow =
                    !string.IsNullOrWhiteSpace(stage2Request.InConstraintWindowJson) ||
                    !string.IsNullOrWhiteSpace(stage2Request.OutConstraintWindowJson);
                bool hasStage3BodyComposition = HasFullBodyOrEffectorConstraint(stage2Request.Constraints?.json);
                bool generateStage3 = RequiresStage3Generation(
                    generateLoop,
                    hasStage3BoundaryWindow,
                    hasStage3BodyComposition);
                if (!generateStage3)
                {
                    if (debugConstraintPasses != null)
                    {
                        stage2Result.DebugConstraintPasses.Clear();
                        stage2Result.DebugConstraintPasses.AddRange(debugConstraintPasses);
                    }
                    KimodoPlayableClipGenerationHostService.FinalizeGeneration(clip, finalRequest, stage2Result);
                    return stage2Result;
                }

                KimodoRawMotionData stage2Motion = stage2.ResultMotion;
                int loopSourceStartFrame = 0;
                int loopWindowFrames = 0;
                int loopWindowSampleCount = 0;
                if (generateLoop)
                {
                    ResolveLoopWindowConfiguration(
                        clip,
                        stage2Request,
                        stage2Motion,
                        out loopSourceStartFrame,
                        out loopWindowFrames,
                        out loopWindowSampleCount);
                }
                string stage2InWindowJson = stage2Request.InConstraintWindowJson;
                string stage2OutWindowJson = stage2Request.OutConstraintWindowJson;
                int inWindowFrames = ResolveConstraintFrameSpan(
                    stage2InWindowJson, out int firstInFrame, out _);
                int outWindowFrames = ResolveConstraintFrameSpan(
                    stage2OutWindowJson, out int firstOutFrame, out _);
                int stage3TargetFrameCount = stage2Request.TargetFrameCount +
                    inWindowFrames + outWindowFrames + loopWindowFrames;
                double stage3DurationSeconds =
                    stage3TargetFrameCount /
                    (double)stage2Request.TargetFrameRate;
                finalRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    prompt,
                    externalConstraint,
                    token,
                    effectiveSeedOverride: effectiveSeed,
                    timelineClipOverride: timelineClipOverride,
                    targetDurationSecondsOverride: stage3DurationSeconds,
                    enableAutoBeginAnchor: false,
                    generateLoopOverride: false,
                    deferOutConstraint: clip.postApplyOutConstraint,
                    deferInConstraint: clip.postApplyInConstraint);
                finalRequest.DebugTimelineGroup = stage2Request.DebugTimelineGroup;
                int stage2BaseStartFrame = finalRequest.RuntimeTrimStartFrame + inWindowFrames;
                finalRequest.InConstraintWindowJson = inWindowFrames > 0
                    ? ShiftConstraintFrames(
                        stage2InWindowJson,
                        finalRequest.RuntimeTrimStartFrame - firstInFrame,
                        finalRequest.EffectiveRuntimeFrameCount)
                    : string.Empty;
                finalRequest.DeferredInConstraintJson = clip.postApplyInConstraint
                    ? finalRequest.InConstraintWindowJson
                    : string.Empty;
                int deferredOutStartFrame = stage2BaseStartFrame + stage2Request.TargetFrameCount + loopWindowFrames;
                finalRequest.OutConstraintWindowJson = outWindowFrames > 0
                    ? ShiftConstraintFrames(
                        stage2OutWindowJson,
                        deferredOutStartFrame - firstOutFrame,
                        finalRequest.EffectiveRuntimeFrameCount)
                    : string.Empty;
                finalRequest.DeferredOutConstraintJson = clip.postApplyOutConstraint
                    ? finalRequest.OutConstraintWindowJson
                    : string.Empty;
                finalRequest.Constraints.json = ShiftConstraintFrames(
                    stage2Request.Constraints.json,
                    stage2BaseStartFrame - stage2Request.RuntimeTrimStartFrame,
                    finalRequest.EffectiveRuntimeFrameCount);
                var stage3 = new KimodoEditorGenerationStage(3, generateLoop ? "Loop window" : "Constraint composition", finalRequest, stage2Motion);
                Vector3? loopTargetAnchorPosition = null;
                Quaternion? loopTargetAnchorHeading = null;
                if (clip.overridePathAngle && hasStage3BodyComposition)
                {
                    stage3.AddPass(new KimodoEditorGenerationPass("Stage 3 trajectory roots", currentConstraints =>
                    {
                        string combined = KimodoRawMotionConstraintBuilder.InterpolateFullBodyRootPositionsJson(
                            currentConstraints,
                            stage2Motion,
                            stage2BaseStartFrame);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 3 FullBody/Effector roots",
                            AddedConstraintsJson = combined,
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                if (generateLoop)
                {
                    stage3.AddPass(new KimodoEditorGenerationPass("Loop Root2D headings", currentConstraints =>
                    {
                        int pathAngleTerminalFrame = stage2BaseStartFrame + stage2Request.TargetFrameCount - 1;
                        if (clip.overridePathAngle &&
                            KimodoRawMotionConstraintBuilder.TryReadRoot2DAnchorJson(
                                currentConstraints,
                                pathAngleTerminalFrame,
                                out Vector3 pathAngleAnchorPosition,
                                out Quaternion pathAngleAnchorHeading))
                        {
                            loopTargetAnchorPosition = pathAngleAnchorPosition;
                            loopTargetAnchorHeading = pathAngleAnchorHeading;
                            currentConstraints = KimodoRawMotionConstraintBuilder.RemoveRoot2DFrameJson(
                                currentConstraints,
                                pathAngleTerminalFrame);
                        }
                        string combined = KimodoRawMotionConstraintBuilder.ApplyLoopRoot2DHeadingOverridesJson(
                            stage2Motion,
                            modelName,
                            currentConstraints,
                            loopSourceStartFrame,
                            loopWindowFrames,
                            stage2BaseStartFrame,
                            stage2BaseStartFrame + stage2Request.TargetFrameCount,
                            finalRequest.EffectiveRuntimeFrameCount,
                            clip.loopLockPositionX,
                            clip.loopLockPositionY,
                            clip.loopLockPositionZ,
                            clip.loopLockRotationX,
                            clip.loopLockRotationY,
                            clip.loopLockRotationZ,
                            loopTargetAnchorPosition,
                            loopTargetAnchorHeading);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 3 Loop Root2D headings",
                            AddedConstraintsJson = combined,
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                if (clip.overrideHeading)
                {
                    stage3.AddPass(new KimodoEditorGenerationPass("Stage 3 heading override", currentConstraints =>
                    {
                        string combined = KimodoRawMotionConstraintBuilder.OverrideEffectiveConstraintHeadingsJson(
                            currentConstraints,
                            clip.headingDegrees);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 3 Heading Override",
                            AddedConstraintsJson = combined,
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                if (generateLoop)
                {
                    stage3.AddPass(new KimodoEditorGenerationPass("Loop In window", currentConstraints =>
                    {
                        string loopWindowConstraintJson = KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                            stage2Motion,
                            modelName,
                            loopSourceStartFrame,
                            loopWindowFrames,
                            loopWindowSampleCount,
                            stage2BaseStartFrame,
                            stage2BaseStartFrame + stage2Request.TargetFrameCount,
                            finalRequest.EffectiveRuntimeFrameCount,
                            finalRequest.TargetFrameRate,
                            clip.loopLockPositionX,
                            clip.loopLockPositionY,
                            clip.loopLockPositionZ,
                            clip.loopLockRotationX,
                            clip.loopLockRotationY,
                            clip.loopLockRotationZ,
                            loopTargetAnchorPosition,
                            loopTargetAnchorHeading);
                        string combined = KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                            currentConstraints,
                            loopWindowConstraintJson);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 3 Loop In window",
                            AddedConstraintsJson = loopWindowConstraintJson,
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                if (hasStage3BoundaryWindow)
                {
                    stage3.AddPass(new KimodoEditorGenerationPass("Stage 3 In/Out windows", currentConstraints =>
                    {
                        string combined = KimodoPostApplyOutConstraintUtility.ApplyInOut(
                            currentConstraints,
                            finalRequest.InConstraintWindowJson,
                            finalRequest.OutConstraintWindowJson,
                            stage2Motion,
                            modelName,
                            finalRequest.TargetFrameRate,
                            stage2BaseStartFrame,
                            clip.postApplyInConstraint,
                            clip.postApplyOutConstraint);
                        debugConstraintPasses?.Add(new KimodoDebugConstraintPass
                        {
                            Name = "Stage 3 In/Out windows",
                            AddedConstraintsJson = KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                                finalRequest.InConstraintWindowJson,
                                finalRequest.OutConstraintWindowJson),
                            CombinedConstraintsJson = combined,
                            ModelName = modelName,
                            FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                            FrameCount = finalRequest.EffectiveRuntimeFrameCount
                        });
                        return combined;
                    }));
                }
                finalRequest.Constraints.json = stage3.ApplyPasses();
                finalRequest.Progress = (stage, message) =>
                    progress?.Invoke(stage, generateLoop ? $"Stage 3 loop: {message}" : $"Stage 3 composition: {message}");
                KimodoDebugTimelineWriter.TryPrepare(clip, finalRequest, generateLoop ? "Stage 3 Loop" : "Stage 3 Composition");
                KimodoPlayableClip stage3DebugClip = finalRequest.DebugTimelineReservation?.TimelineClip?.asset as KimodoPlayableClip;
                if (debugConstraintPasses != null && !string.IsNullOrWhiteSpace(finalRequest.Constraints?.json))
                {
                    debugConstraintPasses.Add(new KimodoDebugConstraintPass
                    {
                        Name = "Stage 3 Constraints",
                        AddedConstraintsJson = finalRequest.Constraints.json,
                        CombinedConstraintsJson = finalRequest.Constraints.json,
                        ModelName = modelName,
                        FrameRate = Mathf.RoundToInt(finalRequest.TargetFrameRate),
                        FrameCount = finalRequest.EffectiveRuntimeFrameCount,
                        DebugClipTarget = stage3DebugClip
                    });
                    for (int i = 0; i < debugConstraintPasses.Count; i++)
                    {
                        if (debugConstraintPasses[i] != null && debugConstraintPasses[i].DebugClipTarget == null)
                            debugConstraintPasses[i].DebugClipTarget = stage3DebugClip;
                    }
                }
                KimodoEditorGenerationResult result = await KimodoEditorGeneratePipeline.ExecuteAsync(finalRequest);
                token.ThrowIfCancellationRequested();
                stage3.ResultMotion = ParseFirstPassMotion(finalRequest, result, "Stage 3");
                if (debugConstraintPasses != null)
                {
                    result.DebugConstraintPasses.Clear();
                    result.DebugConstraintPasses.AddRange(debugConstraintPasses);
                }
                KimodoPlayableClipGenerationHostService.FinalizeGeneration(clip, finalRequest, result);
                return result;
            }
            catch
            {
                finalRequest?.CleanupGeneratedClips();
                if (stage2Request != null && !ReferenceEquals(stage2Request, finalRequest))
                    stage2Request.CleanupGeneratedClips();
                firstRequest?.CleanupGeneratedClips();
                throw;
            }
        }

        private static KimodoRawMotionData ParseFirstPassMotion(
            KimodoEditorGenerateRequest firstRequest,
            KimodoBridgeCommandResult firstResult,
            string operation)
        {
            if (firstResult == null)
            {
                throw new InvalidOperationException($"{operation} pass 1 did not produce a result.");
            }

            KimodoRawMotionData motion;
            string motionError;
            if (firstResult.MotionBytes != null && firstResult.MotionBytes.Length > 0)
            {
                if (!KimodoRawMotionUtility.TryParseFlatBuffer(firstResult.MotionBytes, out motion, out motionError))
                {
                    throw new InvalidOperationException($"{operation} pass 1 raw motion parsing failed: {motionError}");
                }
            }
            else if (!KimodoRawMotionUtility.TryParse(firstResult.MotionJsonCompact, out motion, out motionError))
            {
                throw new InvalidOperationException($"{operation} pass 1 raw motion parsing failed: {motionError}");
            }

            if (motion.FrameCount != firstRequest.TargetFrameCount || motion.FrameRate <= 0f)
            {
                throw new InvalidOperationException(
                    $"{operation} pass 1 raw motion is invalid: frames={motion.FrameCount}, " +
                    $"expected={firstRequest.TargetFrameCount}, frameRate={motion.FrameRate}.");
            }

            return motion;
        }

        private static KimodoRawMotionData ParseFirstPassMotion(
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerationResult result,
            string operation)
        {
            if (result == null)
                throw new InvalidOperationException($"{operation} did not produce a result.");
            KimodoRawMotionData motion;
            string motionError;
            if (result.MotionBytes != null && result.MotionBytes.Length > 0)
            {
                if (!KimodoRawMotionUtility.TryParseFlatBuffer(result.MotionBytes, out motion, out motionError))
                    throw new InvalidOperationException($"{operation} raw motion parsing failed: {motionError}");
            }
            else if (!KimodoRawMotionUtility.TryParse(result.MotionJsonCompact, out motion, out motionError))
            {
                throw new InvalidOperationException($"{operation} raw motion parsing failed: {motionError}");
            }
            if (motion.FrameCount != request.TargetFrameCount || motion.FrameRate <= 0f)
            {
                throw new InvalidOperationException(
                    $"{operation} raw motion is invalid: frames={motion.FrameCount}, " +
                    $"expected={request.TargetFrameCount}, frameRate={motion.FrameRate}.");
            }
            return motion;
        }

        private static void ResolveLoopWindowConfiguration(
            KimodoPlayableClip clip,
            KimodoEditorGenerateRequest sourceRequest,
            KimodoRawMotionData sourceMotion,
            out int sourceStartFrame,
            out int windowFrameCount,
            out int sampleCount)
        {
            int requestedCommandWindow = Mathf.Max(1, clip?.loopWindowFrames ?? 30);
            double modelFrameRate = sourceRequest != null && sourceRequest.TargetFrameRate > 0f
                ? sourceRequest.TargetFrameRate
                : sourceMotion != null && sourceMotion.FrameRate > 0f
                    ? sourceMotion.FrameRate
                    : KimodoMotionModelProfiles.DefaultFrameRate;
            int requestedWindow = Mathf.Max(
                1,
                KimodoFrameTimeUtility.CommandFramesToModelFrameCount(
                    requestedCommandWindow,
                    modelFrameRate));
            int requestedSamples = Mathf.Max(1, clip?.loopWindowSampleCount ?? 2);
            sourceStartFrame = KimodoRawMotionConstraintBuilder.ResolveLoopAnchorFrameForGeneration(sourceMotion);
            int animationFrameCount = Mathf.Max(1, sourceMotion?.FrameCount ?? sourceRequest?.TargetFrameCount ?? 1);
            int usableWindowFrames = Mathf.Max(1, Mathf.FloorToInt(
                animationFrameCount * (requestedSamples - 1f) / requestedSamples));
            int availableFromAnchor = Mathf.Max(1, (sourceMotion?.FrameCount ?? 1) - sourceStartFrame);
            usableWindowFrames = Mathf.Min(usableWindowFrames, availableFromAnchor);
            windowFrameCount = requestedWindow;
            if (windowFrameCount > usableWindowFrames)
            {
                Debug.LogWarning(
                    $"[Kimodo][Loop] Window of {requestedCommandWindow} command frames " +
                    $"({windowFrameCount} model frames) exceeds the usable " +
                    $"animation span from loop anchor {sourceStartFrame} for sampleCount={requestedSamples}; " +
                    $"clamped to {usableWindowFrames}.",
                    clip);
                windowFrameCount = usableWindowFrames;
            }
            sampleCount = Mathf.Clamp(requestedSamples, 1, windowFrameCount);
            if (sampleCount != requestedSamples)
            {
                Debug.LogWarning(
                    $"[Kimodo][Loop] sampleCount={requestedSamples} does not fit the " +
                    $"{windowFrameCount}-frame window; clamped to {sampleCount}.",
                    clip);
            }
        }

        private static void ResolveLongClipWindowConfiguration(
            KimodoEditorGenerateRequest request,
            int maxRequestFrames,
            int minimumCoreFrames,
            out int overlapFrames,
            out int sampleCount)
        {
            int requestedFrames = Mathf.Max(2, request?.LongClipWindowFrames ?? 30);
            int maximumOverlap = Mathf.Max(2, maxRequestFrames - Mathf.Max(1, minimumCoreFrames));
            if (maximumOverlap % 2 != 0)
                maximumOverlap--;

            if ((requestedFrames & 1) != 0)
            {
                int normalized = Mathf.Max(2, requestedFrames - 1);
                Debug.LogWarning(
                    $"[Kimodo][Long Clip] Window frames={requestedFrames} must be even for symmetric overlap; using {normalized}.",
                    request?.TimelineClipSnapshot?.asset);
                requestedFrames = normalized;
            }

            overlapFrames = Mathf.Clamp(requestedFrames, 2, maximumOverlap);
            if (overlapFrames != requestedFrames)
            {
                Debug.LogWarning(
                    $"[Kimodo][Long Clip] Window frames={requestedFrames} exceed the available request range; clamped to {overlapFrames}.",
                    request?.TimelineClipSnapshot?.asset);
            }

            int requestedSamples = Mathf.Max(1, request?.LongClipWindowSampleCount ?? 2);
            sampleCount = Mathf.Clamp(requestedSamples, 1, overlapFrames);
            if (sampleCount != requestedSamples)
            {
                Debug.LogWarning(
                    $"[Kimodo][Long Clip] Sample count={requestedSamples} does not fit the {overlapFrames}-frame overlap; clamped to {sampleCount}.",
                    request?.TimelineClipSnapshot?.asset);
            }
        }

        private static string ShiftConstraintFrames(string constraintsJson, int frameOffset, int runtimeFrameCount)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || frameOffset == 0)
            {
                return constraintsJson ?? string.Empty;
            }
            JArray constraints = JArray.Parse(constraintsJson);
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                if (constraint["frame_indices"] is not JArray frames) continue;
                for (int i = 0; i < frames.Count; i++)
                {
                    if (frames[i].Type != JTokenType.Integer) continue;
                    int shifted = frames[i].Value<int>() + frameOffset;
                    if (shifted < 0 || shifted >= runtimeFrameCount)
                    {
                        throw new InvalidOperationException(
                            $"Reused constraint frame {shifted} is outside runtime range [0,{runtimeFrameCount}).");
                    }
                    frames[i] = shifted;
                }
            }
            return constraints.ToString(Formatting.None);
        }

        private static async Task<KimodoEditorGenerationResult> GenerateClipConstraintBakedAsync(
            KimodoPlayableClip clip,
            string prompt,
            KimodoExternalConstraintRequest externalConstraint,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token,
            TimelineClip timelineClipOverride)
        {
            KimodoEditorGenerateRequest baselineRequest = null;
            KimodoEditorGenerateRequest constraintRequest = null;
            KimodoEditorGenerateRequest finalRequest = null;
            string modelName = KimodoMotionModelProfiles.NormalizeName(clip.bridgeModelName);
            try
            {
                baselineRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    prompt,
                    externalConstraint,
                    token,
                    timelineClipOverride: timelineClipOverride);
                baselineRequest.Progress = progress;
                string bakeAnalysisOptionsJson = baselineRequest.AnalysisOptionsJson;
                baselineRequest.AnalysisOptionsJson = string.Empty;

                progress?.Invoke(KimodoBridgeCommandStage.Constraint, "ClipConstraint bake: generating baseline motion...");
                KimodoBridgeCommandResult baseline = await KimodoEditorGeneratePipeline.ExecuteRuntimePipelineAsync(
                    baselineRequest,
                    prompt,
                    modelName);

                // Build the ClipConstraint payload, but do not send a second
                // generation request. Its KMB is the motion that gets merged
                // into the first generation result under the AvatarMask.
                constraintRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    prompt,
                    externalConstraint,
                    token,
                    effectiveSeedOverride: baselineRequest.EffectiveSeed,
                    timelineClipOverride: timelineClipOverride);

                KimodoClipConstraint clipConstraint = null;
                // BuildRequest may prepend a one-frame synthetic begin constraint.
                // The user-authored ClipConstraint is appended after it, so search
                // backwards to avoid accidentally merging with that boundary mask.
                for (int index = constraintRequest.Constraints.clips.Count - 1; index >= 0; index--)
                {
                    KimodoClipConstraint candidate = constraintRequest.Constraints.clips[index];
                    if (candidate?.mask != null)
                    {
                        clipConstraint = candidate;
                        break;
                    }
                }
                if (clipConstraint == null)
                {
                    throw new InvalidOperationException("ClipConstraint bake could not find the generated clip mask.");
                }
                if (!KimodoRawMotionUtility.TryParseFlatBuffer(
                        clipConstraint.motionBytes,
                        out KimodoRawMotionData constrainedMotion,
                        out string constrainedMotionError))
                {
                    throw new InvalidOperationException(
                        $"ClipConstraint bake could not parse its motion: {constrainedMotionError}");
                }

                KimodoRawMotionData alignedConstraint = KimodoClipConstraintBakeUtility.AlignConstraintMotion(
                    baseline.MotionData,
                    constrainedMotion,
                    baselineRequest.RuntimeTrimStartFrame);
                Avatar characterAvatar = null;
                if (baselineRequest.TimelineClipSnapshot != null &&
                    KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                        baselineRequest.TimelineClipSnapshot,
                        out KimodoTimelineInOutConstraintContext timelineContext,
                        out _))
                {
                    characterAvatar = timelineContext.Animator != null
                        ? timelineContext.Animator.avatar
                        : null;
                }
                KimodoRawMotionData merged;
                if (!KimodoClipConstraintBakeUtility.TryMergeHumanoidFootEffectorMotion(
                        baseline.MotionData,
                        alignedConstraint,
                        clipConstraint.mask,
                        characterAvatar,
                        modelName,
                        out merged,
                        out string footMergeError))
                {
                    if (!string.IsNullOrWhiteSpace(footMergeError))
                    {
                        throw new InvalidOperationException(footMergeError);
                    }
                    merged = KimodoClipConstraintBakeUtility.MergeMaskedMotion(
                        baseline.MotionData,
                        alignedConstraint,
                        clipConstraint.mask);
                }

                progress?.Invoke(KimodoBridgeCommandStage.Constraint, "ClipConstraint bake: applying mask and analyzing keyframes...");
                JObject analysis = await AnalyzeMergedMotionAsync(
                    baselineRequest,
                    modelName,
                    merged,
                    bakeAnalysisOptionsJson,
                    progress,
                    token);
                List<int> keyframes = ExtractAnalysisKeyframes(
                    analysis,
                    merged.FrameCount,
                    merged.FrameRate);
                string fullBodyJson = KimodoRawMotionConstraintBuilder.BuildFullBodyConstraintsJson(
                    merged,
                    modelName,
                    keyframes,
                    baselineRequest.RuntimeTrimStartFrame > 0
                        ? baselineRequest.RuntimeTrimStartFrame / (double)baselineRequest.TargetFrameRate
                        : 0.0,
                    baselineRequest.EffectiveRuntimeDurationSeconds);

                finalRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    prompt,
                    externalConstraint,
                    token,
                    effectiveSeedOverride: baselineRequest.EffectiveSeed,
                    timelineClipOverride: timelineClipOverride);
                finalRequest.Progress = progress;
                finalRequest.Constraints.json = KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                    baselineRequest.Constraints.json,
                    fullBodyJson);
                finalRequest.Constraints.clips.Clear();

                progress?.Invoke(KimodoBridgeCommandStage.Constraint, $"ClipConstraint bake: regenerating with {keyframes.Count} FullBody keyframes...");
                KimodoBridgeCommandResult finalRuntime = await KimodoEditorGeneratePipeline.ExecuteRuntimePipelineAsync(
                    finalRequest,
                    prompt,
                    modelName);
                KimodoEditorGenerationResult result = KimodoEditorGeneratePipeline.BakeRuntimeResult(
                    finalRequest,
                    prompt,
                    modelName,
                    finalRuntime);
                token.ThrowIfCancellationRequested();
                KimodoPlayableClipGenerationHostService.FinalizeGeneration(clip, finalRequest, result);
                return result;
            }
            catch
            {
                (finalRequest ?? baselineRequest)?.CleanupGeneratedClips();
                throw;
            }
        }

        private static async Task<JObject> AnalyzeMergedMotionAsync(
            KimodoEditorGenerateRequest request,
            string modelName,
            KimodoRawMotionData motion,
            string analysisOptionsJson,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            JObject options = string.IsNullOrWhiteSpace(analysisOptionsJson)
                ? new JObject()
                : JObject.Parse(analysisOptionsJson);
            options["analysis_only"] = true;
            JObject keyframeOptions = options["keyframes"] as JObject ?? new JObject();
            keyframeOptions["enabled"] = true;
            options["keyframes"] = keyframeOptions;

            var analysisRequest = new KimodoGenerationRequestDto
            {
                prompt = string.Empty,
                model = KimodoMotionModelProfiles.NormalizeName(modelName),
                text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(request.TextEncoderMode),
                models_root = request.ModelsRoot ?? string.Empty,
                output_format = "kmb_attachments_v1",
                analysis_option_json = options.ToString(Formatting.None),
                analysis_clip_constraints = new List<KimodoKmbClipConstraint>
                {
                    new KimodoKmbClipConstraint
                    {
                        motionBytes = KimodoRawMotionUtility.ToFlatBuffer(motion, modelName),
                        startFrame = 0,
                        endFrameExclusive = motion.FrameCount
                    }
                }
            };
            KimodoBridgeGenerationResult analysisResult = await KimodoBridgeService.Shared.GenerateAsync(
                analysisRequest,
                message => progress?.Invoke(KimodoBridgeCommandStage.Constraint, message),
                token);
            if (string.IsNullOrWhiteSpace(analysisResult?.AnalysisJson))
            {
                throw new InvalidOperationException("ClipConstraint bake analysis returned no keyframe data.");
            }
            return JObject.Parse(analysisResult.AnalysisJson);
        }

        private static List<int> ExtractAnalysisKeyframes(
            JObject analysis,
            int frameCount,
            float frameRate)
        {
            var frames = new List<int>();
            JArray keyframes = analysis?["phase_track"] as JArray;
            bool phaseTrack = keyframes != null;
            if (phaseTrack)
            {
                foreach (JObject phase in keyframes.OfType<JObject>())
                {
                    int frame = Mathf.Clamp(phase.Value<int?>("anchor_frame") ?? 0, 0, Mathf.Max(0, frameCount - 1));
                    if (!frames.Contains(frame)) frames.Add(frame);
                }
            }
            if (!phaseTrack) keyframes = analysis?["keyframes"] as JArray;
            if (!phaseTrack && keyframes != null)
            {
                foreach (JObject keyframe in keyframes.OfType<JObject>())
                {
                    int frame;
                    int? explicitFrame = keyframe.Value<int?>("frame");
                    if (explicitFrame.HasValue)
                    {
                        frame = explicitFrame.Value;
                    }
                    else
                    {
                        double time = keyframe.Value<double?>("time") ?? 0.0;
                        frame = KimodoFrameTimeUtility.SecondsToFrameIndex(time, frameRate);
                    }
                    frame = Mathf.Clamp(frame, 0, Mathf.Max(0, frameCount - 1));
                    if (!frames.Contains(frame))
                    {
                        frames.Add(frame);
                    }
                }
            }

            int lastFrame = Mathf.Max(0, frameCount - 1);
            if (!frames.Contains(0)) frames.Add(0);
            if (!frames.Contains(lastFrame)) frames.Add(lastFrame);
            frames.Sort();
            return frames;
        }
    }
}
