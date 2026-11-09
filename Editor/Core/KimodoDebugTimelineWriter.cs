using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoDebugTimelineReservation
    {
        internal TimelineClip TimelineClip;
        internal TimelineClip SourceTimelineClip;
        internal bool IsOverview;
        internal KimodoDebugTimelineGroup Group;
    }

    internal sealed class KimodoDebugTimelineGroup
    {
        internal TimelineAsset Timeline;
        internal PlayableDirector Director;
        internal GameObject ProfileInstance;
        internal Animator Animator;
        internal AnimationTrack Track;
        internal string ModelName;
        internal double NextStart;
        internal List<KimodoDebugTimelineReservation> OverviewReservations =
            new List<KimodoDebugTimelineReservation>();
    }

    internal static class KimodoDebugTimelineWriter
    {
        internal const string DebugFolder = "Assets/KimodoGeneratedClips/Debug";

        internal sealed class RawClipInput
        {
            internal KimodoPlayableClip SourceClip;
            internal KimodoEditorGenerateRequest Request;
            internal KimodoEditorGenerationResult Result;
        }

        internal sealed class PreparationInput
        {
            internal KimodoPlayableClip SourceClip;
            internal KimodoEditorGenerateRequest Request;
            internal string PassName;
            internal bool HasTimelineTimingOverride;
            internal double TimelineDuration;
            internal double TimelineTimeScale;
        }

        internal static bool TryResolveWindowPlaybackTiming(
            TimelineClip source,
            int requestFrameCount,
            float frameRate,
            out double duration,
            out double timeScale)
        {
            duration = 0.0;
            timeScale = 1.0;
            if (source == null || requestFrameCount <= 0 || frameRate <= 0f ||
                float.IsNaN(frameRate) || float.IsInfinity(frameRate))
                return false;

            timeScale = source.timeScale;
            if (timeScale <= 1e-8 || double.IsNaN(timeScale) || double.IsInfinity(timeScale))
                return false;

            duration = requestFrameCount / (double)frameRate / timeScale;
            return duration > 0.0 && !double.IsNaN(duration) && !double.IsInfinity(duration);
        }

        internal static void TryPrepare(
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            string passName = null,
            bool hasTimelineTimingOverride = false,
            double timelineDuration = 0.0,
            double timelineTimeScale = 1.0)
        {
            TryPrepareGroup(new[]
            {
                new PreparationInput
                {
                    SourceClip = sourceClip,
                    Request = request,
                    PassName = passName,
                    HasTimelineTimingOverride = hasTimelineTimingOverride,
                    TimelineDuration = timelineDuration,
                    TimelineTimeScale = timelineTimeScale
                }
            });
        }

        internal static void BeginGeneration(TimelineClip sourceClip)
        {
            if (!KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips ||
                sourceClip?.GetParentTrack()?.timelineAsset == null)
                return;
            MuteExistingDebugTracks(sourceClip.GetParentTrack().timelineAsset, sourceClip.GetParentTrack());
        }

        internal static void TryPrepareGroup(IList<PreparationInput> inputs)
        {
            if (!KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips)
                return;

            try
            {
                List<PreparationInput> valid = (inputs ?? Array.Empty<PreparationInput>())
                    .Where(input => input?.SourceClip != null && input.Request != null &&
                                    input.Request.TimelineClipSnapshot != null &&
                                    input.Request.DebugTimelineReservation == null)
                    .ToList();
                if (valid.Count == 0) return;

                PreparationInput first = valid[0];
                TimelineClip sourceTimelineClip = first.Request.TimelineClipSnapshot;
                TrackAsset sourceTrack = sourceTimelineClip.GetParentTrack();
                TimelineAsset timeline = sourceTrack != null ? sourceTrack.timelineAsset : null;
                if (timeline == null)
                    throw new InvalidOperationException("The source Timeline is unavailable.");
                if (!KimodoDirectorResolver.TryResolve(timeline, out PlayableDirector director, out string directorError))
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(directorError)
                        ? "The source Timeline director is unavailable."
                        : directorError);

                string modelName = KimodoMotionModelProfiles.NormalizeName(first.Request.ModelName);
                for (int i = 0; i < valid.Count; i++)
                {
                    TimelineClip candidate = valid[i].Request.TimelineClipSnapshot;
                    TrackAsset candidateTrack = candidate?.GetParentTrack();
                    string candidateModel = KimodoMotionModelProfiles.NormalizeName(valid[i].Request.ModelName);
                    if (candidateTrack == null || candidateTrack.timelineAsset != timeline ||
                        !string.Equals(candidateModel, modelName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Debug reservations require clips from the same Timeline and profile model.");
                    }
                }

                string modelPath = AssetDatabase.GUIDToAssetPath(
                    ResolveDebugProfileModelGuid(modelName));
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (model == null)
                    throw new InvalidOperationException(
                        $"Profile model for '{modelName}' was not found at '{modelPath}'.");

                KimodoDebugTimelineGroup group = valid
                    .Select(input => input.Request.DebugTimelineGroup)
                    .FirstOrDefault(existing => existing != null);
                if (valid.Any(input => input.Request.DebugTimelineGroup != null &&
                                       input.Request.DebugTimelineGroup != group))
                {
                    throw new InvalidOperationException(
                        "All passes in one generation must share the same Debug Timeline group.");
                }
                if (group == null)
                {
                    if (!TryCreateDebugTimelineGroup(
                            timeline,
                            director,
                            model,
                            modelName,
                            out group,
                            out string groupError))
                    {
                        Debug.LogWarning(
                            $"[Kimodo][DebugTimeline] Skipped raw output for '{modelName}': {groupError}");
                        return;
                    }
                }
                else if (!IsUsableDebugTimelineGroup(group, timeline, director, modelName, out string groupError))
                {
                    Debug.LogWarning(
                        $"[Kimodo][DebugTimeline] Skipped raw output for '{modelName}': {groupError}");
                    return;
                }

                double frameRate = ResolveTimelineFrameRate(timeline);
                double gap = 5.0 / frameRate;
                foreach (PreparationInput input in valid)
                {
                    TimelineClip overviewSourceTimelineClip = input.Request.TimelineClipSnapshot;
                    if (group.OverviewReservations.Any(item => item.SourceTimelineClip == overviewSourceTimelineClip))
                        continue;

                    double overviewTimeScale = overviewSourceTimelineClip.timeScale;
                    if (overviewTimeScale <= 1e-8 || double.IsNaN(overviewTimeScale) ||
                        double.IsInfinity(overviewTimeScale))
                        overviewTimeScale = 1.0;
                    double overviewDuration = overviewSourceTimelineClip.duration;
                    if (overviewDuration <= 0.0 || double.IsNaN(overviewDuration) ||
                        double.IsInfinity(overviewDuration))
                        overviewDuration = input.Request.EffectiveRuntimeDurationSeconds / overviewTimeScale;
                    overviewDuration = Math.Max(1.0 / frameRate, overviewDuration);
                    TimelineClip overviewClip = CreateDebugPlaceholder(
                        group,
                        input.SourceClip,
                        input.Request,
                        "Raw Overview",
                        $"Raw Overview {input.SourceClip.name}",
                        overviewSourceTimelineClip.start,
                        overviewDuration,
                        overviewTimeScale);
                    group.OverviewReservations.Add(new KimodoDebugTimelineReservation
                    {
                        TimelineClip = overviewClip,
                        SourceTimelineClip = overviewSourceTimelineClip,
                        IsOverview = true,
                        Group = group
                    });
                    group.NextStart = Math.Max(group.NextStart, overviewClip.end + gap);
                }

                for (int i = 0; i < valid.Count; i++)
                {
                    PreparationInput input = valid[i];
                    TimelineClip inputSourceClip = input.Request.TimelineClipSnapshot;
                    double timeScale = input.HasTimelineTimingOverride
                        ? input.TimelineTimeScale
                        : inputSourceClip.timeScale;
                    if (timeScale <= 1e-8 || double.IsNaN(timeScale) || double.IsInfinity(timeScale))
                        timeScale = 1.0;
                    double duration = input.HasTimelineTimingOverride
                        ? input.TimelineDuration
                        : input.Request.EffectiveRuntimeDurationSeconds / timeScale;
                    if (duration <= 0.0 || double.IsNaN(duration) || double.IsInfinity(duration))
                        duration = input.Request.EffectiveRuntimeDurationSeconds / timeScale;
                    duration = Math.Max(1.0 / frameRate, duration);
                    double cursor = ResolveAvailableDebugStart(
                        group.Track,
                        inputSourceClip,
                        duration,
                        frameRate);
                    if (group.NextStart > cursor)
                        cursor = group.NextStart;
                    string passName = input.PassName ?? string.Empty;
                    string displayName = string.IsNullOrWhiteSpace(passName)
                        ? $"Raw {input.SourceClip.name}"
                        : $"{passName} {input.SourceClip.name}";
                    TimelineClip placeholder = CreateDebugPlaceholder(
                        group,
                        input.SourceClip,
                        input.Request,
                        passName,
                        displayName,
                        cursor,
                        duration,
                        timeScale);
                    input.Request.DebugTimelineReservation = new KimodoDebugTimelineReservation
                    {
                        TimelineClip = placeholder,
                        SourceTimelineClip = inputSourceClip,
                        Group = group
                    };
                    KimodoPlayableClip passClip = placeholder.asset as KimodoPlayableClip;
                    AttachPassTargets(input.Request.DebugConstraintPasses, passClip);
                    group.NextStart = placeholder.end + gap;
                    input.Request.DebugTimelineGroup = group;
                }

                EditorUtility.SetDirty(group.Track);
                EditorUtility.SetDirty(timeline);
                EditorUtility.SetDirty(director);
                EditorUtility.SetDirty(group.ProfileInstance);
                EditorUtility.SetDirty(group.Animator);
                EditorSceneManager.MarkSceneDirty(director.gameObject.scene);
                RefreshTimeline();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Kimodo][DebugTimeline] Could not reserve raw clip output: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static void TryWrite(
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerationResult result)
        {
            if (!KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips)
                return;
            if (!TryWriteBackendDebugSegments(sourceClip, request, result))
                TryFillReservedClip(sourceClip, request, result);
            TryFillOverviewClip(sourceClip, request, result);
            TryAttachConstraintMarkers(request, result?.DebugConstraintPasses);
            RefreshTimeline();
        }

        private static bool TryWriteBackendDebugSegments(
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerationResult result)
        {
            if (request?.DebugRuntimeMotion == null ||
                string.IsNullOrWhiteSpace(request.DebugSegmentsJson) ||
                request.DebugTimelineGroup?.Track == null)
                return false;

            try
            {
                JArray segments = JArray.Parse(request.DebugSegmentsJson);
                JArray constraintArtifacts = string.IsNullOrWhiteSpace(request.DebugConstraintsJson)
                    ? new JArray()
                    : JArray.Parse(request.DebugConstraintsJson);
                if (segments.Count == 0) return false;

                KimodoDebugTimelineGroup group = request.DebugTimelineGroup;
                float frameRate = request.DebugRuntimeMotion.FrameRate > 0f
                    ? request.DebugRuntimeMotion.FrameRate
                    : KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName);
                double timelineFrameRate = ResolveTimelineFrameRate(group.Timeline);
                double gap = 5.0 / timelineFrameRate;
                KimodoDebugTimelineReservation firstReservation = request.DebugTimelineReservation;

                foreach (JToken token in segments)
                {
                    if (token is not JObject segment) continue;
                    int start = segment.Value<int?>("debug_start_frame") ?? -1;
                    int end = segment.Value<int?>("debug_end_frame_exclusive") ?? -1;
                    if (start < 0 || end <= start || end > request.DebugRuntimeMotion.FrameCount)
                        throw new InvalidOperationException($"Debug segment range [{start},{end}) is outside the returned motion.");
                    if (!KimodoRawMotionUtility.TrySlice(
                            request.DebugRuntimeMotion, start, end - start,
                            out KimodoRawMotionData motion, out string sliceError))
                        throw new InvalidOperationException(sliceError);

                    KimodoDebugTimelineReservation reservation = firstReservation;
                    if (reservation == null)
                    {
                        double duration = Math.Max(1.0 / timelineFrameRate, motion.FrameCount / (double)frameRate);
                        double timelineStart = group.NextStart;
                        TimelineClip placeholder = CreateDebugPlaceholder(
                            group,
                            sourceClip,
                            request,
                            segment.Value<string>("name") ?? "Backend Stage",
                            $"{segment.Value<string>("name") ?? "Backend Stage"} {sourceClip?.name ?? request.ModelName}",
                            timelineStart,
                            duration,
                            1.0);
                        reservation = new KimodoDebugTimelineReservation
                        {
                            TimelineClip = placeholder,
                            SourceTimelineClip = request.TimelineClipSnapshot,
                            Group = group
                        };
                    }
                    firstReservation = null;
                    string passName = segment.Value<string>("name") ?? segment.Value<string>("purpose") ?? "Backend Stage";
                    KimodoPlayableClip debugTarget = reservation.TimelineClip?.asset as KimodoPlayableClip;
                    if (debugTarget != null) debugTarget.debugPassName = passName;
                    TryWriteRawMotion(sourceClip, request, reservation, motion);
                    if (reservation.TimelineClip != null)
                        group.NextStart = Math.Max(group.NextStart, reservation.TimelineClip.end + gap);

                    JObject constraintArtifact = constraintArtifacts.Children<JObject>()
                        .FirstOrDefault(item => string.Equals(
                            item.Value<string>("artifact"),
                            segment.Value<string>("name"),
                            StringComparison.Ordinal));
                    if (constraintArtifact?["constraints"] is JArray constraints && constraints.Count > 0)
                    {
                        result?.DebugConstraintPasses.Add(new KimodoDebugConstraintPass
                        {
                            Name = passName,
                            AddedConstraintsJson = constraints.ToString(Newtonsoft.Json.Formatting.None),
                            CombinedConstraintsJson = constraints.ToString(Newtonsoft.Json.Formatting.None),
                            ModelName = request.ModelName,
                            FrameRate = Mathf.RoundToInt(frameRate),
                            FrameCount = motion.FrameCount,
                            DebugClipTarget = debugTarget
                        });
                    }
                }
                request.DebugTimelineReservation = null;
                EditorUtility.SetDirty(group.Track);
                EditorUtility.SetDirty(group.Timeline);
                if (group.Director != null) EditorUtility.SetDirty(group.Director);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Kimodo][DebugTimeline] Could not split backend debug output: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static void TryWriteGroup(IList<RawClipInput> inputs)
        {
            if (!KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips)
                return;
            RawClipInput debugInput = (inputs ?? Array.Empty<RawClipInput>()).FirstOrDefault(input =>
                input?.Request?.DebugRuntimeMotion != null &&
                !string.IsNullOrWhiteSpace(input.Request.DebugSegmentsJson));
            if (debugInput != null)
            {
                KimodoDebugTimelineReservation reservation = debugInput.Request.DebugTimelineReservation;
                debugInput.Request.DebugTimelineReservation = null;
                try
                {
                    TryWriteBackendDebugSegments(debugInput.SourceClip, debugInput.Request, debugInput.Result);
                }
                finally
                {
                    debugInput.Request.DebugTimelineReservation = reservation;
                }
            }
            foreach (RawClipInput input in inputs ?? Array.Empty<RawClipInput>())
            {
                if (input == null) continue;
                TryFillReservedClip(input.SourceClip, input.Request, input.Result);
                TryFillOverviewClip(input.SourceClip, input.Request, input.Result);
            }
            foreach (RawClipInput input in inputs ?? Array.Empty<RawClipInput>())
            {
                if (input?.Request != null)
                    TryAttachConstraintMarkers(input.Request, input.Result?.DebugConstraintPasses);
            }
            RefreshTimeline();
        }

        internal static void TryWriteRawMotion(
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            KimodoDebugTimelineReservation reservation,
            KimodoRawMotionData motion)
        {
            if (!KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips ||
                reservation == null || motion == null)
                return;

            AnimationClip rawClip = new AnimationClip
            {
                name = $"Kimodo_DebugRaw_{sourceClip?.name ?? request?.ModelName ?? "Pass"}",
                frameRate = motion.FrameRate > 0f
                    ? motion.FrameRate
                    : KimodoMotionModelProfiles.ResolveGenerationFrameRate(request?.ModelName),
                legacy = false
            };
            try
            {
                if (!KimodoRetargetToolsEditor.BakeIntoClip(
                        rawClip,
                        KimodoRawMotionUtility.ToCompactJson(motion),
                        KimodoMotionModelProfiles.ResolveBakeSkeletonType(request.ModelName),
                        request.ModelName,
                        null,
                        out string bakeError))
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(bakeError) ? "Raw motion bake failed." : bakeError);
                }

                TryFillReservedClip(sourceClip, request, reservation, rawClip, motion.FrameCount);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Kimodo][DebugTimeline] Could not bake raw motion for '{sourceClip?.name ?? "unknown"}': " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (rawClip != null && string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(rawClip)))
                    UnityEngine.Object.DestroyImmediate(rawClip);
            }
        }

        private static void TryFillReservedClip(
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerationResult result)
        {
            KimodoDebugTimelineReservation reservation = request?.DebugTimelineReservation;
            if (reservation == null || reservation.TimelineClip == null)
                return;
            if (request.DebugRuntimeMotion != null)
            {
                TryWriteRawMotion(sourceClip, request, reservation, request.DebugRuntimeMotion);
                return;
            }
            TryFillReservedClip(sourceClip, request, reservation, result?.RawBoneClip);
        }

        private static void TryFillOverviewClip(
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerationResult result)
        {
            TimelineClip sourceTimelineClip = request?.TimelineClipSnapshot;
            if (sourceTimelineClip == null || result?.RawBoneClip == null)
                return;
            KimodoDebugTimelineReservation reservation = request.DebugTimelineGroup?.OverviewReservations
                .FirstOrDefault(item => item.SourceTimelineClip == sourceTimelineClip);
            if (reservation != null)
                TryFillReservedClip(sourceClip, request, reservation, result.RawBoneClip);
        }

        private static void TryFillReservedClip(
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            KimodoDebugTimelineReservation reservation,
            AnimationClip sourceRawClip,
            int sourceFrameCount = 0)
        {
            if (reservation == null || reservation.TimelineClip == null)
                return;

            try
            {
                if (sourceRawClip == null)
                    throw new InvalidOperationException("Generation did not return a raw bone clip.");
                EnsureDebugFolder();
                string assetName = KimodoRuntimeUtility.SanitizeName(
                    $"Kimodo_DebugRaw_{sourceClip?.name ?? request.ModelName}_{Guid.NewGuid():N}",
                    "Kimodo_DebugRaw");
                AnimationClip debugClip = new AnimationClip
                {
                    name = assetName,
                    frameRate = sourceRawClip.frameRate > 0f
                        ? sourceRawClip.frameRate
                        : KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName),
                    legacy = sourceRawClip.legacy
                };
                KimodoEditorClipUtility.CopyClipData(sourceRawClip, debugClip, forceNoLoopKeepY: false);
                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{DebugFolder}/{assetName}.anim");
                AssetDatabase.CreateAsset(debugClip, assetPath);
                AssetDatabase.SaveAssetIfDirty(debugClip);

                KimodoPlayableClip placeholder = reservation.TimelineClip.asset as KimodoPlayableClip;
                if (placeholder == null)
                    throw new InvalidOperationException("Debug reservation asset is not a KimodoPlayableClip.");
                placeholder.clip = debugClip;
                placeholder.bridgeModelName = request.ModelName;
                placeholder.autoRetargetOnBinding = false;
                placeholder.removeStartOffset = false;
                placeholder.isGenerated = true;
                placeholder.isDebugRawClip = true;
                int frameCount = sourceFrameCount > 0
                    ? sourceFrameCount
                    : Mathf.RoundToInt(debugClip.length * debugClip.frameRate);
                placeholder.frameCount = frameCount;
                placeholder.fps = Mathf.RoundToInt(debugClip.frameRate);
                placeholder.generatedAssetName = assetName;
                TimelineAsset timeline = reservation.Group?.Timeline;
                PlayableDirector director = reservation.Group?.Director;
                AnimationTrack track = reservation.Group?.Track;
                double playbackScale = reservation.TimelineClip.timeScale;
                if (playbackScale <= 1e-8 || double.IsNaN(playbackScale) || double.IsInfinity(playbackScale))
                    playbackScale = 1.0;
                reservation.TimelineClip.duration = Math.Max(
                    1.0 / ResolveTimelineFrameRate(timeline),
                    frameCount / (double)debugClip.frameRate / playbackScale);
                if (reservation.IsOverview && reservation.SourceTimelineClip != null)
                {
                    reservation.TimelineClip.start = reservation.SourceTimelineClip.start;
                    reservation.TimelineClip.duration = Math.Max(
                        1.0 / ResolveTimelineFrameRate(timeline),
                        reservation.SourceTimelineClip.duration);
                    reservation.TimelineClip.timeScale = reservation.SourceTimelineClip.timeScale;
                }
                if (reservation.Group != null)
                {
                    reservation.Group.NextStart = Math.Max(
                        reservation.Group.NextStart,
                        reservation.TimelineClip.end + 5.0 / ResolveTimelineFrameRate(timeline));
                }
                EditorUtility.SetDirty(placeholder);
                EditorUtility.SetDirty(track);
                EditorUtility.SetDirty(timeline);
                EditorUtility.SetDirty(director);
                if (director != null)
                    EditorSceneManager.MarkSceneDirty(director.gameObject.scene);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Kimodo][DebugTimeline] Could not fill raw clip for '{sourceClip?.name ?? "unknown"}': " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void MuteExistingDebugTracks(TimelineAsset timeline, TrackAsset sourceTrack)
        {
            if (timeline == null) return;
            foreach (TrackAsset track in timeline.GetOutputTracks())
            {
                if (track == sourceTrack || !IsDebugTrack(track)) continue;
                track.muted = true;
                EditorUtility.SetDirty(track);
            }
        }

        private static bool IsDebugTrack(TrackAsset track)
        {
            if (track == null || string.IsNullOrWhiteSpace(track.name)) return false;
            const string baseName = "debug track";
            string name = track.name.Trim();
            if (name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                (name.Length == baseName.Length || char.IsWhiteSpace(name[baseName.Length]) ||
                 name[baseName.Length] == '('))
                return true;
            return name.StartsWith("Debug_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DebugRoll_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("KimodoDebug_", StringComparison.OrdinalIgnoreCase);
        }

        private static TimelineClip CreateDebugPlaceholder(
            KimodoDebugTimelineGroup group,
            KimodoPlayableClip sourceClip,
            KimodoEditorGenerateRequest request,
            string passName,
            string displayName,
            double start,
            double duration,
            double timeScale)
        {
            TimelineClip placeholder = group.Track.CreateClip<KimodoPlayableClip>();
            placeholder.start = start;
            placeholder.duration = duration;
            placeholder.clipIn = 0.0;
            placeholder.timeScale = timeScale;
            placeholder.displayName = displayName;
            KimodoPlayableClip asset = (KimodoPlayableClip)placeholder.asset;
            asset.name = $"KimodoDebugRaw_{sourceClip.name}";
            asset.bridgeModelName = request.ModelName;
            asset.autoRetargetOnBinding = false;
            asset.generationOutputMode = KimodoGenerationOutputMode.ModelBone;
            asset.removeStartOffset = false;
            asset.isGenerated = false;
            asset.isDebugRawClip = true;
            asset.debugPassName = passName ?? string.Empty;
            return placeholder;
        }

        internal static void TryAttachConstraintMarkers(
            KimodoEditorGenerateRequest request,
            IList<KimodoDebugConstraintPass> passes)
        {
            if (request == null || passes == null || passes.Count == 0)
                return;

            try
            {
                KimodoPlayableClip fallbackTarget =
                    request.DebugTimelineReservation?.TimelineClip?.asset as KimodoPlayableClip;
                foreach (KimodoDebugConstraintPass pass in passes)
                {
                    if (pass == null)
                        continue;
                    KimodoPlayableClip target = pass.DebugClipTarget != null
                        ? pass.DebugClipTarget
                        : fallbackTarget;
                    if (target == null || !target.isDebugRawClip)
                    {
                        Debug.LogWarning(
                            $"[Kimodo][DebugTimeline] Could not attach constraint pass '{pass.Name}' to a raw clip.");
                        continue;
                    }

                    TimelineClip targetTimelineClip = FindDebugTimelineClip(request, target);
                    AnimationTrack targetTrack = request.DebugTimelineGroup?.Track;
                    if (targetTimelineClip == null || targetTrack == null)
                    {
                        Debug.LogWarning(
                            $"[Kimodo][DebugTimeline] Could not locate the raw Timeline clip for constraint pass '{pass.Name}'.");
                        continue;
                    }

                    int constraintOrdinal = 0;
                    foreach (JObject rawConstraint in SplitConstraintRecords(pass.AddedConstraintsJson))
                    {
                        if (!TryReadFirstFrame(rawConstraint, out int frameIndex))
                        {
                            Debug.LogWarning(
                                $"[Kimodo][DebugTimeline] Skipped constraint marker for pass '{pass.Name}' because no frame_indices value was found.");
                            continue;
                        }

                        double markerTime = ResolveDebugConstraintMarkerTime(
                            targetTimelineClip,
                            frameIndex,
                            pass.FrameRate,
                            pass.FrameCount);
                        KimodoDebugConstraintJsonMarker marker =
                            targetTrack.CreateMarker<KimodoDebugConstraintJsonMarker>(markerTime);
                        marker.name = BuildDebugConstraintMarkerName(
                            pass.Name,
                            rawConstraint,
                            frameIndex,
                            ++constraintOrdinal);
                        marker.SetData(
                            pass.Name,
                            new JArray(rawConstraint).ToString(Newtonsoft.Json.Formatting.None),
                            // A debug marker represents exactly one raw
                            // constraint at one model frame. Keep the marker
                            // payload atomic even when the generation pass
                            // carried a merged constraint array.
                            new JArray(rawConstraint).ToString(Newtonsoft.Json.Formatting.None),
                            pass.ModelName,
                            pass.FrameRate,
                            pass.FrameCount);
                    }
                }
                TimelineAsset timeline = request.DebugTimelineGroup?.Timeline;
                PlayableDirector director = request.DebugTimelineGroup?.Director;
                if (timeline != null) EditorUtility.SetDirty(timeline);
                if (director != null)
                {
                    EditorUtility.SetDirty(director);
                    if (director.gameObject.scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(director.gameObject.scene);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Kimodo][DebugTimeline] Could not attach constraint pass data: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static TimelineClip FindDebugTimelineClip(
            KimodoEditorGenerateRequest request,
            KimodoPlayableClip target)
        {
            AnimationTrack track = request?.DebugTimelineGroup?.Track;
            if (track == null || target == null)
                return null;
            return track.GetClips().FirstOrDefault(clip => clip.asset == target);
        }

        private static IEnumerable<JObject> SplitConstraintRecords(string constraintsJson)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson))
                yield break;

            JArray constraints;
            try
            {
                constraints = JArray.Parse(constraintsJson);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Kimodo][DebugTimeline] Could not parse debug constraint JSON: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                yield break;
            }

            foreach (JToken token in constraints)
            {
                if (token is not JObject source)
                    continue;
                if (source["frame_indices"] is not JArray frames || frames.Count == 0)
                {
                    yield return (JObject)source.DeepClone();
                    continue;
                }

                for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    JObject single = (JObject)source.DeepClone();
                    single["frame_indices"] = new JArray(frames[frameIndex].DeepClone());
                    foreach (JProperty property in single.Properties().ToList())
                    {
                        if (property.Name == "frame_indices" || property.Value is not JArray values ||
                            values.Count != frames.Count)
                            continue;
                        property.Value = new JArray(values[frameIndex].DeepClone());
                    }
                    yield return single;
                }
            }
        }

        private static bool TryReadFirstFrame(JObject constraint, out int frameIndex)
        {
            frameIndex = 0;
            if (constraint?["frame_indices"] is not JArray frames || frames.Count == 0)
                return false;
            return int.TryParse(frames[0]?.ToString(), out frameIndex);
        }

        private static double ResolveDebugConstraintMarkerTime(
            TimelineClip targetTimelineClip,
            int frameIndex,
            int passFrameRate,
            int passFrameCount)
        {
            double frameRate = passFrameRate > 0
                ? passFrameRate
                : (targetTimelineClip.asset as KimodoPlayableClip)?.fps ?? 30;
            double timeScale = targetTimelineClip.timeScale;
            if (timeScale <= 1e-8 || double.IsNaN(timeScale) || double.IsInfinity(timeScale))
                timeScale = 1.0;
            if (passFrameCount > 0)
                frameIndex = Math.Max(0, Math.Min(frameIndex, passFrameCount - 1));
            return targetTimelineClip.start + frameIndex / frameRate / timeScale;
        }

        private static string BuildDebugConstraintMarkerName(
            string passName,
            JObject constraint,
            int frameIndex,
            int ordinal)
        {
            string type = constraint?["type"]?.ToString();
            if (string.IsNullOrWhiteSpace(type))
                type = "Constraint";
            return $"{passName ?? "Debug"} {type} frame {frameIndex} #{ordinal}";
        }

        private static void AttachPassTargets(
            IList<KimodoDebugConstraintPass> passes,
            KimodoPlayableClip target)
        {
            if (passes == null || target == null)
                return;
            foreach (KimodoDebugConstraintPass pass in passes)
            {
                if (pass != null && pass.DebugClipTarget == null)
                    pass.DebugClipTarget = target;
            }
        }

        private static void EnsureDebugFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/KimodoGeneratedClips"))
                AssetDatabase.CreateFolder("Assets", "KimodoGeneratedClips");
            if (!AssetDatabase.IsValidFolder(DebugFolder))
                AssetDatabase.CreateFolder("Assets/KimodoGeneratedClips", "Debug");
        }

        internal static string ResolveDebugProfileModelGuid(string modelName)
        {
            return KimodoRuntimeProfileSkeletonPreview.ResolveModelGuid(modelName);
        }

        private static bool TryCreateDebugTimelineGroup(
            TimelineAsset timeline,
            PlayableDirector director,
            GameObject model,
            string modelName,
            out KimodoDebugTimelineGroup group,
            out string error)
        {
            group = null;
            error = string.Empty;
            if (timeline == null || director == null || model == null)
            {
                error = "The Timeline, director, or profile model is unavailable.";
                return false;
            }

            GameObject profileInstance = FindReusableDebugProfileInstance(
                director.gameObject.scene,
                modelName);
            if (profileInstance == null)
            {
                profileInstance = UnityEngine.Object.Instantiate(model);
                profileInstance.name = KimodoRuntimeUtility.SanitizeName(
                    BuildDebugProfileName(modelName),
                    "KimodoDebugProfile");
                if (director.gameObject.scene.IsValid() &&
                    profileInstance.scene != director.gameObject.scene)
                {
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(
                        profileInstance,
                        director.gameObject.scene);
                }
            }
            profileInstance.SetActive(true);
            profileInstance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            Animator animator = profileInstance.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                animator = profileInstance.AddComponent<Animator>();
            }
            foreach (Animator profileAnimator in profileInstance.GetComponentsInChildren<Animator>(true))
            {
                profileAnimator.avatar = null;
                profileAnimator.enabled = true;
            }

            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    modelName,
                    profileInstance.transform,
                    out _,
                    out _,
                    out _,
                    out error))
            {
                UnityEngine.Object.DestroyImmediate(profileInstance);
                return false;
            }

            AnimationTrack debugTrack = timeline.CreateTrack<AnimationTrack>(
                null,
                BuildDebugTrackName());
            debugTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
            director.SetGenericBinding(debugTrack, animator);
            group = new KimodoDebugTimelineGroup
            {
                Timeline = timeline,
                Director = director,
                ProfileInstance = profileInstance,
                Animator = animator,
                Track = debugTrack,
                ModelName = modelName,
                OverviewReservations = new List<KimodoDebugTimelineReservation>()
            };
            return true;
        }

        private static bool IsUsableDebugTimelineGroup(
            KimodoDebugTimelineGroup group,
            TimelineAsset timeline,
            PlayableDirector director,
            string modelName,
            out string error)
        {
            error = string.Empty;
            if (group == null || group.Timeline != timeline || group.Director != director ||
                group.Track == null ||
                group.ProfileInstance == null || group.Animator == null)
            {
                error = "The existing Debug Timeline group is incomplete or belongs to another Timeline.";
                return false;
            }
            if (!string.Equals(group.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
            {
                error = "The existing Debug Timeline group belongs to another profile model.";
                return false;
            }
            if (group.ProfileInstance.scene != director.gameObject.scene ||
                director.GetGenericBinding(group.Track) != group.Animator)
            {
                error = "The existing Debug Timeline group is no longer bound to its profile Animator.";
                return false;
            }
            foreach (Animator profileAnimator in group.ProfileInstance.GetComponentsInChildren<Animator>(true))
            {
                if (profileAnimator.avatar != null)
                {
                    error = "The Debug profile Animator has an Avatar; raw profile clips require avatar == null.";
                    return false;
                }
            }
            return true;
        }

        private static string BuildDebugTrackName()
        {
            return "debug track";
        }

        private static string BuildDebugProfileName(string modelName)
        {
            return $"KimodoDebugProfile_{modelName}";
        }

        private static GameObject FindReusableDebugProfileInstance(
            UnityEngine.SceneManagement.Scene scene,
            string modelName)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            string exactName = BuildDebugProfileName(modelName);
            string legacyPrefix = exactName + "_";
            GameObject[] candidates = scene.GetRootGameObjects()
                .Where(root => root != null &&
                    (string.Equals(root.name, exactName, StringComparison.OrdinalIgnoreCase) ||
                     root.name.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(root => string.Equals(
                    root.name, exactName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0)
                return null;

            GameObject selected = candidates[0];
            for (int i = 1; i < candidates.Length; i++)
                candidates[i].SetActive(false);
            return selected;
        }

        private static void RefreshTimeline()
        {
            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(
                RefreshReason.ContentsAddedOrRemoved);
        }

        private static double ResolveAvailableDebugStart(
            TrackAsset track,
            TimelineClip sourceClip,
            double duration,
            double frameRate)
        {
            double gap = 5.0 / Math.Max(1.0, frameRate);
            double cursor = (sourceClip?.end ?? 0.0) + gap;
            bool moved;
            do
            {
                moved = false;
                foreach (TimelineClip clip in track?.GetClips() ?? Array.Empty<TimelineClip>())
                {
                    KimodoPlayableClip asset = clip?.asset as KimodoPlayableClip;
                    if (asset == null || !asset.isDebugRawClip || clip == sourceClip)
                        continue;
                    bool overlaps = cursor < clip.end && cursor + duration > clip.start;
                    if (!overlaps) continue;
                    cursor = clip.end + gap;
                    moved = true;
                }
            } while (moved);
            return cursor;
        }

        private static double ResolveTimelineFrameRate(TimelineAsset timeline)
        {
            double frameRate = timeline != null ? timeline.editorSettings.frameRate : 0.0;
            return frameRate > 0.0 && !double.IsNaN(frameRate) && !double.IsInfinity(frameRate)
                ? frameRate
                : KimodoMotionModelProfiles.DefaultFrameRate;
        }

    }
}
