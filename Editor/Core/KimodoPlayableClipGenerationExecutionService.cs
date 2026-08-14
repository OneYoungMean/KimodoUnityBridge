using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CharacterAnimationCli.Unity.Command;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            out command_generation_session session,
            out string error)
        {
            session = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "KimodoPlayableClip is null.";
                return false;
            }

            List<TimelineClip> selected = command_selection.GetSelectedPlayableClips(clip);
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

                if (command_generation_runner.TryGet(selectedClip, out command_generation_session active) &&
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

            return command_generation_runner.Start(
                clip,
                $"clip-selected:{KimodoUnityObjectIdUtility.NameKey(clip)}",
                command_kind.GeneratePlayableClip,
                async (handle, token) => await GenerateSelectedAndFinalizeAsync(
                    selectedClips,
                    selectedTimelineClips,
                    (stage, message) => command_generation_runner.UpdateProgress(
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
            return command_selection.GetSelectedPlayableClips(clip).Count;
        }

        internal static bool TryStartGenerateConnected(
            KimodoPlayableClip clip,
            out command_generation_session session,
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
                if (command_generation_runner.TryGet(entries[i].Clip, out command_generation_session active) &&
                    active != null &&
                    active.IsRunning)
                {
                    error = $"A generation session is already running for '{entries[i].Clip.name}'.";
                    session = active;
                    return false;
                }
            }

            return command_generation_runner.Start(
                clip,
                $"clip-connected:{KimodoUnityObjectIdUtility.NameKey(clip)}",
                command_kind.GeneratePlayableClip,
                async (handle, token) => await GenerateConnectedAsync(
                    entries,
                    profile,
                    (stage, message) => command_generation_runner.UpdateProgress(
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
            out command_generation_session session,
            out string error)
        {
            return command_generation_runner.Start(
                clip,
                $"clip:{KimodoUnityObjectIdUtility.NameKey(clip)}",
                command_kind.GeneratePlayableClip,
                async (handle, token) => await GenerateAndFinalizeAsync(
                    clip,
                    externalConstraint: null,
                    (stage, message) => command_generation_runner.UpdateProgress(clip, handle.RequestId, stage, message),
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
            List<TimelineClip> selected = command_selection.GetSelectedPlayableClips(clip);
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
            List<TimelineClip> selected = command_selection.GetSelectedPlayableClips(clip);
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

        private static async Task<command_generate_result> GenerateConnectedAsync(
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
            if (aggregate.MotionData.FrameCount != totalFrameCount)
            {
                throw new InvalidOperationException(
                    $"Connected generation returned {aggregate.MotionData.FrameCount} frames; expected {totalFrameCount}.");
            }

            var baked = new List<command_generate_result>(entries.Count);
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
                    KimodoPlayableClipGenerationHostService.FinalizeGeneration(entries[i].Clip, entries[i].Request, baked[i]);
                    finalized++;
                }
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
                    enableAutoBeginAnchor: i == 0,
                    timelineClipOverride: entry.TimelineClip);
                AppendConnectedBoundarySamples(entry, i, entries.Count);
                entry.Request.Progress = PrefixProgress(progress, i, entries.Count);
                if (string.IsNullOrWhiteSpace(entry.Request.Prompt))
                {
                    throw new InvalidOperationException($"Prompt is empty on selected clip '{entry.Clip.name}'.");
                }
            }

            var allClipConstraints = new List<KimodoClipConstraint>();
            for (int i = 0; i < entries.Count; i++)
            {
                float timeOffset = entries[i].StartFrame / profile.SourceFps;
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
                firstRequest.Constraints.json = KimodoConstraintJsonExporter.ToConstraintsJson(
                    allSamples,
                    ResolveExportContext(entries[0].TimelineClip),
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

        private static KimodoConstraintExportContext ResolveExportContext(TimelineClip timelineClip)
        {
            if (timelineClip != null &&
                KimodoInOutConstraintAdapter.TryResolveTimelineContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out _) &&
                context?.SourceAvatar != null)
            {
                return new KimodoConstraintExportContext(
                    KimodoConstraintNormalizationUtility.ResolveHumanScale(context.SourceAvatar),
                    KimodoConstraintExportProjector.Create(context.ModelName));
            }
            return new KimodoConstraintExportContext();
        }

        internal static int CompareTimelineClips(TimelineClip left, TimelineClip right)
        {
            int byStart = (left?.start ?? 0.0).CompareTo(right?.start ?? 0.0);
            return byStart != 0 ? byStart : (left?.end ?? 0.0).CompareTo(right?.end ?? 0.0);
        }

        private static async Task<command_generate_result> GenerateSelectedAndFinalizeAsync(
            IReadOnlyList<KimodoPlayableClip> clips,
            IReadOnlyList<TimelineClip> timelineClips,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            command_generate_result result = null;
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
                    timelineClipOverride: timelineClips != null && i < timelineClips.Count ? timelineClips[i] : null);
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

        internal static async Task<command_generate_result> GenerateAndFinalizeAsync(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token,
            TimelineClip timelineClipOverride = null)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("KimodoPlayableClip is null.");
            }

            string prompt = clip.motionPrompt ?? string.Empty;
            if (clip.enableClipConstraint &&
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

            try
            {
                request.Progress = progress;
                command_generate_result result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
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

        private static async Task<command_generate_result> GenerateClipConstraintBakedAsync(
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
                    timelineClipOverride: timelineClipOverride,
                    enableClipConstraintOverride: false);
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
                    timelineClipOverride: timelineClipOverride,
                    enableClipConstraintOverride: true);

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
                KimodoRawMotionData merged;
                if (!KimodoClipConstraintBakeUtility.TryMergeHumanoidFootIkMotion(
                        baseline.MotionData,
                        alignedConstraint,
                        clipConstraint.mask,
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
                string fullBodyJson = KimodoClipConstraintBakeUtility.BuildFullBodyConstraintsJson(
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
                    timelineClipOverride: timelineClipOverride,
                    enableClipConstraintOverride: false);
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
                command_generate_result result = KimodoEditorGeneratePipeline.BakeRuntimeResult(
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
            JArray keyframes = analysis?["keyframes"] as JArray;
            if (keyframes != null)
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
