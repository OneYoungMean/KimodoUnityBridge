using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoPlayableClipGenerationHostService
    {
        private const string ReplaceTimelineAnimationUndoName = "Kimodo Replace Timeline Animation";
        private static readonly KimodoEditorConstraintProvider ConstraintProvider = new KimodoEditorConstraintProvider();
        public static KimodoEditorGenerateRequest BuildRequest(
            KimodoPlayableClip clip,
            string prompt,
            KimodoExternalConstraintRequest externalConstraint,
            CancellationToken token,
            int? effectiveSeedOverride = null,
            bool disableTimelineInOut = false,
            TimelineClip timelineClipOverride = null,
            bool requestEndpointSample = false,
            string rawGenerateInstructionJson = null)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(clip.bridgeModelName);
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(
                resolvedModelName,
                out KimodoMotionModelProfile ardyProfile);
            TimelineClip timelineClip = timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (timelineClip == null || timelineClip.duration <= 0.0)
            {
                throw new InvalidOperationException("Generation length requires a Timeline clip with positive duration.");
            }
            float targetFrameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(resolvedModelName);
            double targetDurationSeconds = timelineClip.duration;
            int targetFrameCount = Mathf.Max(
                KimodoMotionModelProfiles.MinGenerationFrames,
                KimodoFrameTimeUtility.SecondsToFrameCount(targetDurationSeconds, targetFrameRate));
            // Keep the backend request at the requested length. Short clips may
            // still receive context frames below, but are no longer expanded to
            // a separate minimum generation duration.
            int generationFrameCount = targetFrameCount;
            bool isLoopGeneration = clip.generateLoop;
            int contextBefore = 0;
            int contextAfter = 0;
            if (!disableTimelineInOut &&
                (externalConstraint?.Enabled != true || externalConstraint.IncludeTimelineConstraints))
            {
                KimodoInOutConstraintTools.ResolveOutsideContextFrames(timelineClip, out contextBefore, out contextAfter);
            }
            if (externalConstraint?.Enabled == true)
            {
                if (externalConstraint.ContextBeforeFrames < 0 || externalConstraint.ContextAfterFrames < 0)
                    throw new InvalidOperationException("External context frame counts must be non-negative.");
                contextBefore = Math.Max(contextBefore, externalConstraint.ContextBeforeFrames);
                contextAfter = Math.Max(contextAfter, externalConstraint.ContextAfterFrames);
            }
            int runtimeFrameCount = generationFrameCount + contextBefore + contextAfter;
            int runtimeTrimStartFrame = contextBefore;
            double runtimeSampleOffsetSeconds = runtimeTrimStartFrame / (double)targetFrameRate;
            float runtimeLengthSeconds = runtimeFrameCount / targetFrameRate;

            KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                timelineClip,
                out KimodoTimelineInOutConstraintContext timelineContextSnapshot,
                out _);
            if (timelineContextSnapshot != null)
            {
                KimodoTimelineTrackOffsetUtility.CaptureWorldOffset(
                    timelineContextSnapshot.Track,
                    timelineContextSnapshot.Animator,
                    out timelineContextSnapshot.TrackOffsetPosition,
                    out timelineContextSnapshot.TrackOffsetRotation,
                    out _);
                timelineContextSnapshot.HasTrackOffsetSnapshot = true;
            }

            var constraintResult = ConstraintProvider.BuildGenerationConstraintsOrThrow(
                clip, externalConstraint, runtimeFrameCount, runtimeLengthSeconds, targetFrameRate,
                runtimeSampleOffsetSeconds, timelineClip);
            string constraintsJson = constraintResult.Json;
            List<KimodoMarkerSampleResult> constraintSamples = constraintResult.Samples;
            ArdyEditorHistorySource initialHistorySource = null;
            if (isArdy)
            {
                if (!disableTimelineInOut)
                {
                    ResolveArdyInitialHistory(
                        clip,
                        ardyProfile,
                        timelineClip,
                        runtimeTrimStartFrame,
                        out initialHistorySource);
                }
            }
            int effectiveSeed = effectiveSeedOverride ?? ResolveEffectiveSeed(clip);
            if (effectiveSeedOverride.HasValue && clip.seed != effectiveSeed)
            {
                clip.seed = effectiveSeed;
                EditorUtility.SetDirty(clip);
            }
            GameObject outputBindingObject = ConstraintProvider.FindTimelineBindingObjectForAsset(clip, timelineClip);
            PlayableDirector outputDirector = null;
            TrackAsset outputTrack = timelineClip.GetParentTrack();
            if (outputTrack != null)
            {
                KimodoInOutConstraintAdapter.TryResolveDirector(
                    timelineClip,
                    outputTrack,
                    out outputDirector,
                    out _);
            }
            KimodoEditorGenerateOutputPlan outputPlanSnapshot = KimodoTimelineGenerationOutputPlanner.Capture(
                clip,
                explicitRetargetAvatar: null,
                resolvedModelName,
                outputBindingObject);
            List<KimodoClipConstraint> clipConstraints = KimodoTimelineClipConstraintBuilder.Build(
                clip,
                timelineClip,
                resolvedModelName,
                targetFrameRate,
                runtimeTrimStartFrame,
                targetFrameCount,
                !disableTimelineInOut &&
                    (clip.insideConstraintEnabled || clip.inOutConstraintMode == KimodoInOutConstraintMode.Outside) &&
                    (externalConstraint?.Enabled != true || externalConstraint.IncludeTimelineConstraints),
                token);
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            var request = new KimodoEditorGenerateRequest(
                () => KimodoTimelineGenerationOutputPlanner.CreateTargetClip(clip),
                (generatedClip, modelName) => KimodoTimelineGenerationOutputPlanner.Resolve(
                    outputPlanSnapshot,
                    outputBindingObject,
                    generatedClip,
                    modelName),
                outputPlanSnapshot)
            {
                Prompt = settings.ResolvePrompt(prompt),
                RawGenerateInstructionJson = rawGenerateInstructionJson,
                ModelName = resolvedModelName,
                TextEncoderMode = clip.textEncoderMode,
                TargetFrameCount = targetFrameCount,
                TargetFrameRate = targetFrameRate,
                RuntimeFrameCount = runtimeFrameCount,
                RuntimeTrimStartFrame = runtimeTrimStartFrame,
                RequestEndpointSample = requestEndpointSample,
                DiffusionSteps = KimodoMotionModelProfiles.ClampDiffusionSteps(
                    resolvedModelName,
                    clip.diffusionSteps),
                EffectiveSeed = effectiveSeed,
                Constraints = new KimodoConstraintPayload { json = constraintsJson, clips = clipConstraints },
                ModelsRoot = settings.LocalModelsPath?.Trim() ?? string.Empty,
                Token = token,
                ConstraintSamples = constraintSamples,
                TimelineClipSnapshot = timelineClip,
                TimelineDirectorSnapshot = outputDirector,
                TimelineContextSnapshot = timelineContextSnapshot,
                InitialArdyHistorySource = initialHistorySource,
                ArdyHistoryWeight = isArdy && !clip.ardyAutoHistory
                    ? Mathf.Clamp01(clip.ardyHistoryWeight)
                    : (double?)null
            };
            bool hasTrajectory = clip.overridePathAngle || clip.overridePathDistance || clip.overrideHeading || clip.splinePathEnabled;
            request.StagePlanJson = BuildBackendStagePlanJson(
                clip,
                request.EffectiveBackendRuntimeFrameCount,
                request.RuntimeTrimStartFrame,
                request.TargetFrameCount + (request.RequestEndpointSample ? 1 : 0),
                request.TargetFrameRate,
                isLoopGeneration,
                hasTrajectory);
            request.WindowConstraintsJson = BuildWindowConstraintsJson(clip, request.TargetFrameRate);
            request.DebugGeneration = KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips;
            return request;
        }

        private static string BuildBackendStagePlanJson(
            KimodoPlayableClip clip,
            int targetFrameCount,
            int outputStartFrame,
            int outputFrameCount,
            float frameRate,
            bool loop,
            bool trajectory)
        {
            var plan = new JObject
            {
                ["version"] = 1,
                ["target_frame_count"] = targetFrameCount,
                ["output_start_frame"] = outputStartFrame,
                ["output_frame_count"] = outputFrameCount,
                ["trajectory"] = new JObject
                {
                    ["enabled"] = trajectory,
                    ["root_trajectory"] = trajectory,
                    ["override_path_angle"] = clip.overridePathAngle,
                    ["override_distance"] = clip.overridePathDistance,
                    ["override_heading"] = clip.overrideHeading,
                    ["path_begin_angle_degrees"] = clip.pathBeginAngleDegrees,
                    ["path_end_angle_degrees"] = clip.pathEndAngleDegrees,
                    ["distance"] = clip.overridePathDistance ? (float?)Mathf.Max(0f, clip.pathDistance) : null,
                    ["heading_degrees"] = clip.headingDegrees
                },
                ["loop"] = new JObject
                {
                    ["enabled"] = loop,
                    ["window_frames"] = Mathf.Max(1, Mathf.CeilToInt(clip.loopWindowFrames60 * frameRate / 60f)),
                    ["sample_count"] = Mathf.Max(1, clip.loopWindowSampleCount),
                    ["lock_position_x"] = clip.loopLockPositionX,
                    ["lock_position_y"] = clip.loopLockPositionY,
                    ["lock_position_z"] = clip.loopLockPositionZ,
                    ["lock_rotation_x"] = clip.loopLockRotationX,
                    ["lock_rotation_y"] = clip.loopLockRotationY,
                    ["lock_rotation_z"] = clip.loopLockRotationZ
                }
            };
            return plan.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string BuildWindowConstraintsJson(KimodoPlayableClip clip, float frameRate)
        {
            int frames = Mathf.Max(1, Mathf.CeilToInt(clip.longClipWindowFrames60 * frameRate / 60f));
            int samples = Mathf.Clamp(clip.longClipWindowSampleCount, 1, frames);
            return new JObject
            {
                ["window_frames"] = frames,
                ["num_transition_frames"] = frames,
                ["sample_count"] = samples
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static void FinalizeGeneration(
            KimodoPlayableClip clip,
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerationResult result,
            bool writeDebugTimeline = true)
        {
            if (clip == null || request == null || result == null || result.GeneratedClip == null)
            {
                return;
            }

            TimelineClip timelineClip = request.TimelineClipSnapshot ??
                KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            int undoGroup = BeginReplaceTimelineAnimationUndo(clip, timelineClip);
            try
            {
                clip.clip = result.GeneratedClip;
                // The generation checkbox controls Unity's playback Loop Time as well
                // as the bounded tail preprocessing performed by the generator.
                KimodoEditorClipUtility.ApplyLoopTime(result.GeneratedClip, clip.generateLoop);
                ApplyGeneratedMetadata(clip, result.Prompt, result.MotionJsonCompact);
                EditorUtility.SetDirty(clip);
                EditorUtility.SetDirty(result.GeneratedClip);
                result.ConstraintsPath = request.Constraints.IsEmpty ? "(none)" : "(inline-json)";
                HandleGeneratedClipWritebackCompleted(clip, request);

                if (KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips &&
                    !KimodoEditorClipWritebackService.TryMaterializeGeneratedClipCache(
                        result.GeneratedClip,
                        request.OutputPlan != null && request.OutputPlan.ExportMuscleClip,
                        request.OutputPlan != null ? request.OutputPlan.TargetRetargetAvatar : null,
                        forceRefresh: false,
                        out AnimationClip generatedCacheClip,
                        out string cacheError))
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(cacheError)
                            ? "Materialize generated clip cache failed."
                            : cacheError);
                }

                ResetTimelineTimeScaleAfterGeneration(request);
                result.ConstraintLoss = KimodoGenerationConstraintHipsComparer.Compare(
                    result.GeneratedClip,
                    timelineClip,
                    request.ModelName,
                    request.ConstraintSamples,
                    request.RuntimeTrimStartFrame,
                    request.TargetFrameRate,
                    request.TimelineContextSnapshot);
                if (KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips &&
                    result.DebugConstraintPasses.Count == 0 &&
                    request.Constraints != null &&
                    !request.Constraints.IsEmpty)
                {
                    result.DebugConstraintPasses.Add(new KimodoDebugConstraintPass
                    {
                        Name = "Final Constraints",
                        AddedConstraintsJson = request.Constraints.json ?? string.Empty,
                        CombinedConstraintsJson = request.Constraints.json ?? string.Empty,
                        ModelName = request.ModelName,
                        FrameRate = Mathf.RoundToInt(request.TargetFrameRate),
                        FrameCount = request.EffectiveRuntimeFrameCount,
                        DebugClipTarget = request.DebugTimelineReservation?.TimelineClip?.asset as KimodoPlayableClip
                    });
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
            if (writeDebugTimeline)
            {
                KimodoDebugTimelineWriter.TryWrite(clip, request, result);
            }
        }

        internal static bool ResetTimelineTimeScaleAfterGeneration(KimodoEditorGenerateRequest request)
        {
            TimelineClip timelineClip = request?.TimelineClipSnapshot;
            if (request?.ResetTimelineTimeScaleAfterGeneration != true ||
                timelineClip == null ||
                Mathf.Approximately((float)timelineClip.timeScale, 1f))
            {
                return false;
            }

            timelineClip.timeScale = 1.0;
            TrackAsset track = timelineClip.GetParentTrack();
            if (track != null)
            {
                EditorUtility.SetDirty(track);
                if (track.timelineAsset != null)
                {
                    EditorUtility.SetDirty(track.timelineAsset);
                }
            }

            if (TimelineEditor.inspectedAsset != null)
            {
                KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsModified);
            }
            return true;
        }

        private static void ApplyGeneratedMetadata(KimodoPlayableClip clip, string prompt, string motionJson)
        {
            if (clip == null || string.IsNullOrWhiteSpace(motionJson))
            {
                return;
            }

            JObject obj = JObject.Parse(motionJson);
            clip.lastGeneratedPrompt = prompt ?? string.Empty;
            clip.isGenerated = true;
            clip.frameCount = obj.Value<int?>("num_frames") ?? 0;
            clip.jointCount = obj.Value<int?>("num_joints") ?? 0;
            clip.fps = Mathf.RoundToInt(obj.Value<float?>("fps") ?? KimodoMotionModelProfiles.DefaultFrameRate);
        }

        private static void HandleGeneratedClipWritebackCompleted(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request)
        {
            if (playableClip != null)
            {
                playableClip.position = Vector3.zero;
                playableClip.rotation = Quaternion.identity;
                EditorUtility.SetDirty(playableClip);
            }
            KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
        }

        private static int BeginReplaceTimelineAnimationUndo(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip)
        {
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(ReplaceTimelineAnimationUndoName);
            Undo.RecordObject(playableClip, ReplaceTimelineAnimationUndoName);

            if (timelineClip != null)
            {
                UndoExtensions.RegisterClip(timelineClip, L10n.Tr(ReplaceTimelineAnimationUndoName));

                TrackAsset parentTrack = timelineClip.GetParentTrack();
                if (parentTrack != null)
                {
                    Undo.RecordObject(parentTrack, ReplaceTimelineAnimationUndoName);
                }
            }

            if (TimelineEditor.inspectedAsset != null)
            {
                Undo.RecordObject(TimelineEditor.inspectedAsset, ReplaceTimelineAnimationUndoName);
            }

            return undoGroup;
        }

        private static int ResolveEffectiveSeed(KimodoPlayableClip clip)
        {
            int effectiveSeed = clip.randomSeed
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : clip.seed;

            if (clip.randomSeed || clip.seed != effectiveSeed)
            {
                clip.seed = effectiveSeed;
                EditorUtility.SetDirty(clip);
            }

            return effectiveSeed;
        }

        private static void ResolveArdyInitialHistory(
            KimodoPlayableClip clip,
            KimodoMotionModelProfile profile,
            TimelineClip timelineClipOverride,
            int contextBeforeFrames,
            out ArdyEditorHistorySource source)
        {
            source = null;
            if (clip.inOutConstraintMode != KimodoInOutConstraintMode.Outside || !clip.enableInConstraint)
            {
                return;
            }

            TimelineClip timelineClip = timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (timelineClip == null ||
                !KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    timelineClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out _))
            {
                return;
            }

            if (context.PreviousTimelineClip == null || context.PreviousTimelineClip.duration <= 0.0)
            {
                return;
            }

            double historyEnd = Math.Max(0.0, timelineClip.start - contextBeforeFrames / (double)profile.SourceFps);
            if (historyEnd <= 0.0) return;
            source = new ArdyEditorHistorySource
            {
                TimelineContext = context,
                RangeStartSeconds = Math.Max(
                    0.0,
                    historyEnd - (profile.MaxContextFrames - profile.HorizonFrames) / profile.SourceFps),
                RangeEndSeconds = historyEnd
            };
        }

    }
}
