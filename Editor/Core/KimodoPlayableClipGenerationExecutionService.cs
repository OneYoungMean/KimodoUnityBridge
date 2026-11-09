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


                KimodoGenerationResultDto response = KimodoBridgeService.Shared.GenerateAsync(
                    new KimodoGenerationRequestDto
                    {
                        prompt = string.Empty,
                        model = KimodoMotionModelProfiles.NormalizeName(input.ModelName),
                        text_encoder_mode = KimodoTextEncoderModeProtocol.ToProtocolValue(input.TextEncoderMode),
                        models_root = input.ModelsRoot ?? string.Empty,
                        output_format = "kmb_attachments_v1",
                        analysis_option_json = new JObject { ["analysis_only"] = true }.ToString(Formatting.None),
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
                if (string.IsNullOrWhiteSpace(response?.analysisJson))
                {
                    throw new InvalidOperationException("Analysis returned no data.");
                }
                if (response.motionBytes == null || response.motionBytes.Length == 0)
                {
                    throw new InvalidOperationException("Analysis returned no dense KMB motion.");
                }
                if (!KimodoRawMotionUtility.TryParseFlatBuffer(response.motionBytes, out KimodoRawMotionData motion, out string parseError) ||
                    !motion.HasFootContacts)
                {
                    throw new InvalidOperationException($"Analysis returned dense KMB without foot contacts: {parseError}");
                }

                analysisJson = response.analysisJson;
                motionBytes = response.motionBytes;
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

        // Shared entry for Inspector and command generation.
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
            out string error,
            string rawGenerateInstructionJson = null)
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
                            timelineClipOverride,
                            rawGenerateInstructionJson: rawGenerateInstructionJson);
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
                if (playable.overridePathAngle != firstClip.overridePathAngle ||
                    playable.overridePathDistance != firstClip.overridePathDistance ||
                    playable.overrideHeading != firstClip.overrideHeading ||
                    playable.splinePathEnabled != firstClip.splinePathEnabled ||
                    playable.pathBeginAngleDegrees != firstClip.pathBeginAngleDegrees ||
                    playable.pathEndAngleDegrees != firstClip.pathEndAngleDegrees ||
                    playable.pathDistance != firstClip.pathDistance ||
                    playable.headingDegrees != firstClip.headingDegrees ||
                    playable.diffusionSteps != firstClip.diffusionSteps ||
                    playable.longClipWindowFrames60 != firstClip.longClipWindowFrames60 ||
                    playable.longClipWindowSampleCount != firstClip.longClipWindowSampleCount)
                {
                    AddDifference(differences, $"'{playable.name}' has different shared trajectory, diffusion or transition settings");
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
            bool requestEndpointSample = !profile.IsArdy;
            int runtimeFrameCount = totalFrameCount + contextBefore + contextAfter +
                (requestEndpointSample ? 1 : 0);
            KimodoEditorGenerateRequest firstRequest = entries[0].Request;
            KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                firstRequest,
                firstRequest.Prompt?.Trim() ?? string.Empty,
                profile.ModelName);
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
            generation.timeline_segments[generation.timeline_segments.Count - 1].duration +=
                (contextAfter + (requestEndpointSample ? 1 : 0)) / profile.SourceFps;
            int outputFrameCount = totalFrameCount + (requestEndpointSample ? 1 : 0);
            SetBackendOutputRange(generation, runtimeFrameCount, contextBefore, outputFrameCount);

            firstRequest.Progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Generating connected Timeline KMB...");
            var pipeline = new KimodoBridgeCommand();
            KimodoGenerationResultDto aggregate = await pipeline.ExecuteAsync(
                generation,
                (stage, message) => progress?.Invoke(stage, message),
                token);
            if (profile.IsArdy)
            {
                KimodoEditorGeneratePipeline.ValidateArdyResult(aggregate, profile, groupSeed);
            }
            else if (aggregate?.motionData == null)
            {
                throw new InvalidOperationException("Connected Timeline generation returned no motion.");
            }
            if (aggregate.motionData.FrameCount != outputFrameCount)
            {
                throw new InvalidOperationException(
                    $"Connected generation returned {aggregate.motionData.FrameCount} frames; expected {outputFrameCount}.");
            }
            if (firstRequest.DebugGeneration)
            {
                firstRequest.DebugRuntimeMotion = aggregate.debugMotionData;
                firstRequest.DebugSegmentsJson = aggregate.debugSegmentsJson ?? string.Empty;
                firstRequest.DebugConstraintsJson = aggregate.debugConstraintsJson ?? string.Empty;
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
                            aggregate.motionData,
                            entry.StartFrame,
                            entry.FrameCount,
                            out KimodoRawMotionData motion,
                            out string sliceError))
                    {
                        throw new InvalidOperationException(sliceError);
                    }

                    if (requestEndpointSample)
                    {
                        if (!KimodoRawMotionUtility.TrySlice(
                                aggregate.motionData,
                                entry.StartFrame,
                                entry.FrameCount + 1,
                                out entry.Request.BakeMotionData,
                                out string endpointSliceError))
                        {
                            throw new InvalidOperationException(
                                $"Connected clip '{entry.Clip.name}' is missing its real endpoint sample: {endpointSliceError}");
                        }
                        entry.Request.RequestEndpointSample = true;
                        entry.Request.BakeMotionJsonCompact =
                            KimodoRawMotionUtility.ToCompactJson(entry.Request.BakeMotionData);
                    }

                    byte[] payload = KimodoRawMotionUtility.ToFlatBuffer(motion, profile.ModelName);
                    entry.Request.Progress?.Invoke(KimodoBridgeCommandStage.Bake, $"Baking connected clip {i + 1}/{entries.Count}...");
                    baked.Add(KimodoEditorGeneratePipeline.BakeRuntimeResult(
                        entry.Request,
                        entry.Request.Prompt?.Trim() ?? string.Empty,
                        profile.ModelName,
                        new KimodoGenerationResultDto
                        {
                            motionJsonCompact = KimodoRawMotionUtility.ToCompactJson(motion),
                            motionData = motion,
                            motionBytes = payload,
                            motionFormat = "kmb_v1",
                            message = "Connected Timeline generation complete.",
                            rawStatus = "done",
                            motionRepFingerprint = aggregate.motionRepFingerprint,
                            resolvedSeed = aggregate.resolvedSeed,
                            startFrame = entry.StartFrame,
                            endFrameExclusive = entry.StartFrame + entry.FrameCount
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
                    timelineClipOverride: entry.TimelineClip);
                entry.Request.Progress = PrefixProgress(progress, i, entries.Count);
                if (string.IsNullOrWhiteSpace(entry.Request.Prompt))
                {
                    throw new InvalidOperationException($"Prompt is empty on selected clip '{entry.Clip.name}'.");
                }
            }

            var allClipConstraints = new List<KimodoClipConstraint>();
            for (int i = 0; i < entries.Count; i++)
            {
                int frameOffset = entries[i].StartFrame + contextBefore - entries[i].Request.RuntimeTrimStartFrame;
                float timeOffset = frameOffset / profile.SourceFps;
                foreach (KimodoClipConstraint constraint in entries[i].Request.Constraints.clips)
                {
                    if (constraint == null)
                    {
                        continue;
                    }
                    if (i != entries.Count - 1 && constraint.purpose == "outside_out") continue;
                    allClipConstraints.Add(new KimodoClipConstraint
                    {
                        motionBytes = constraint.motionBytes,
                        startTime = constraint.startTime + timeOffset,
                        duration = constraint.duration,
                        mask = constraint.mask,
                        purpose = constraint.purpose,
                        samplingMode = constraint.samplingMode,
                        sampleCount = constraint.sampleCount,
                        postApply = constraint.postApply,
                        targetFrameIndices = constraint.targetFrameIndices == null
                            ? null
                            : constraint.targetFrameIndices
                                .Select(frame => frame + frameOffset)
                                .ToList()
                    });
                }
            }
            entries[0].Request.Constraints.clips = allClipConstraints;

            var allSamples = new List<KimodoMarkerSampleResult>();
            var sampleTimeOffsets = new List<double>();
            for (int i = 0; i < entries.Count; i++)
            {
                double timeOffset = (entries[i].StartFrame + contextBefore - entries[i].Request.RuntimeTrimStartFrame) / (double)profile.SourceFps;
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

        internal static void SetBackendOutputRange(
            KimodoGenerationRequestDto generation, int frameCount, int outputStartFrame, int outputFrameCount)
        {
            if (generation == null || string.IsNullOrWhiteSpace(generation.stage_plan_json))
                return;
            JObject plan = JObject.Parse(generation.stage_plan_json);
            plan["target_frame_count"] = frameCount;
            plan["output_start_frame"] = outputStartFrame;
            plan["output_frame_count"] = outputFrameCount;
            generation.stage_plan_json = plan.ToString(Formatting.None);
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
            bool muteDebugTracksBeforeStart = true,
            string rawGenerateInstructionJson = null)
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
            KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                clip,
                prompt,
                externalConstraint,
                token,
                timelineClipOverride: timelineClipOverride,
                requestEndpointSample: !KimodoMotionModelProfiles.TryGetArdy(clip.bridgeModelName, out _),
                rawGenerateInstructionJson: rawGenerateInstructionJson);
            string modelName = KimodoMotionModelProfiles.NormalizeName(clip.bridgeModelName);

            try
            {
                request.Progress = progress;
                KimodoEditorGenerationResult result;
                KimodoDebugTimelineWriter.TryPrepare(clip, request, "Generation");
                result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
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

    }
}
