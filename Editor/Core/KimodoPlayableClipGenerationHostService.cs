using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using CharacterAnimationCli.Unity.Command;
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
            bool deferConstraintNormalization = false,
            bool enableAutoBeginAnchor = true,
            TimelineClip timelineClipOverride = null,
            bool? enableClipConstraintOverride = null,
            bool? generateLoopOverride = null)
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
            int targetFrameCount = Mathf.Max(
                KimodoMotionModelProfiles.MinGenerationFrames,
                KimodoFrameTimeUtility.SecondsToFrameCount(timelineClip.duration, targetFrameRate));
            int sourceSessionFrameCount = Mathf.Max(1, Mathf.RoundToInt((float)(timelineClip.duration * 60.0)));
            bool isLoopGeneration = (generateLoopOverride ?? clip.generateLoop) && sourceSessionFrameCount <= 300;
            int loopPaddingFrames = isLoopGeneration ? targetFrameCount / 2 : 0;
            bool useOutsideGuardFrame = ShouldUseOutsideGuardFrame(
                clip,
                externalConstraint,
                disableTimelineInOut);
            int runtimeFrameCount = (isLoopGeneration ? targetFrameCount * 2 : targetFrameCount) +
                (useOutsideGuardFrame ? 1 : 0);
            int runtimeTrimStartFrame = loopPaddingFrames + (useOutsideGuardFrame ? 1 : 0);
            double runtimeSampleOffsetSeconds = useOutsideGuardFrame ? 1.0 / targetFrameRate : 0.0;
            runtimeSampleOffsetSeconds += loopPaddingFrames / (double)targetFrameRate;
            float runtimeLengthSeconds = runtimeFrameCount / targetFrameRate;

            KimodoInOutConstraintResult constraintResult =
                ConstraintProvider.BuildGenerationConstraintsOrThrow(
                    clip,
                    externalConstraint,
                    runtimeFrameCount,
                    runtimeLengthSeconds,
                    targetFrameRate,
                    disableTimelineInOut,
                    deferConstraintNormalization,
                    enableAutoBeginAnchor,
                    runtimeSampleOffsetSeconds,
                    timelineClip);
            string constraintsJson = constraintResult.ConstraintsJson;
            List<KimodoMarkerSampleResult> constraintSamples = constraintResult.CombinedSamples;
            bool hasSyntheticAutoBeginConstraint = constraintResult.HasSyntheticAutoBeginConstraint;

            ArdyEditorHistorySource initialHistorySource = null;
            if (isArdy)
            {
                if (!disableTimelineInOut)
                {
                    ResolveArdyInitialHistory(
                        clip,
                        ardyProfile,
                        timelineClip,
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
                externalConstraint?.RetargetAvatar,
                resolvedModelName,
                outputBindingObject);
            List<KimodoClipConstraint> clipConstraints = KimodoTimelineClipConstraintBuilder.Build(
                clip,
                timelineClip,
                resolvedModelName,
                runtimeFrameCount,
                targetFrameRate,
                runtimeTrimStartFrame,
                !disableTimelineInOut &&
                    (externalConstraint?.Enabled != true || externalConstraint.IncludeTimelineConstraints),
                enableClipConstraintOverride,
                token);
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            return new KimodoEditorGenerateRequest(
                () => KimodoTimelineGenerationOutputPlanner.CreateTargetClip(clip),
                (generatedClip, modelName) => KimodoTimelineGenerationOutputPlanner.Resolve(
                    outputPlanSnapshot,
                    outputBindingObject,
                    generatedClip,
                    modelName),
                outputPlanSnapshot)
            {
                Prompt = settings.ResolvePrompt(prompt),
                ModelName = resolvedModelName,
                TextEncoderMode = clip.textEncoderMode,
                TargetFrameCount = targetFrameCount,
                TargetFrameRate = targetFrameRate,
                RuntimeFrameCount = runtimeFrameCount,
                RuntimeTrimStartFrame = runtimeTrimStartFrame,
                DiffusionSteps = KimodoMotionModelProfiles.ClampDiffusionSteps(
                    resolvedModelName,
                    clip.diffusionSteps),
                EffectiveSeed = effectiveSeed,
                Constraints = new KimodoConstraintPayload { json = constraintsJson, clips = clipConstraints },
                AnalysisOptionsJson = string.IsNullOrWhiteSpace(externalConstraint?.AnalysisOptionsJson)
                    ? clip.analysisOptionsJson ?? string.Empty
                    : externalConstraint.AnalysisOptionsJson,
                ModelsRoot = settings.LocalModelsPath?.Trim() ?? string.Empty,
                Token = token,
                HasSyntheticAutoBeginConstraint = hasSyntheticAutoBeginConstraint,
                ConstraintSamples = constraintSamples,
                TimelineClipSnapshot = timelineClip,
                ResetTimelineTimeScaleAfterGeneration =
                    !disableTimelineInOut &&
                    (externalConstraint == null ||
                        !externalConstraint.Enabled ||
                        externalConstraint.IncludeTimelineConstraints) &&
                    clip.inOutConstraintMode == KimodoInOutConstraintMode.Inside &&
                    (clip.enableInConstraint || clip.enableOutConstraint) &&
                    !Mathf.Approximately((float)timelineClip.timeScale, 1f),
                TimelineDirectorSnapshot = outputDirector,
                InitialArdyHistorySource = initialHistorySource,
                ArdyHistoryWeight = isArdy && !clip.ardyAutoHistory
                    ? Mathf.Clamp01(clip.ardyHistoryWeight)
                    : (double?)null,
                ArdyMaxSpeed = isArdy
                    ? Mathf.Max(0.01f, clip.ardyTargetMaxSpeed)
                    : (double?)null,
                ArdyMaxAcceleration = isArdy
                    ? Mathf.Max(0.01f, clip.ardyTargetMaxAcceleration)
                    : (double?)null
            };
        }

        private static bool ShouldUseOutsideGuardFrame(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            bool disableTimelineInOut)
        {
            return clip != null &&
                !disableTimelineInOut &&
                (externalConstraint?.Enabled != true || externalConstraint.IncludeTimelineConstraints) &&
                clip.inOutConstraintMode == KimodoInOutConstraintMode.Outside &&
                clip.enableInConstraint;
        }

        internal static bool IsLoopGenerationEnabled(
            KimodoPlayableClip clip,
            TimelineClip timelineClip = null)
        {
            if (clip == null || !clip.generateLoop)
            {
                return false;
            }

            TimelineClip resolved = timelineClip ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            return resolved != null &&
                resolved.duration > 0.0 &&
                Mathf.RoundToInt((float)(resolved.duration * 60.0)) <= 300;
        }

        public static void FinalizeGeneration(
            KimodoPlayableClip clip,
            KimodoEditorGenerateRequest request,
            command_generate_result result)
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
                ApplyGeneratedMetadata(clip, result.Prompt, result.MotionJsonCompact);
                EditorUtility.SetDirty(clip);
                EditorUtility.SetDirty(result.GeneratedClip);
                result.ConstraintsPath = request.Constraints.IsEmpty ? "(none)" : "(inline-json)";
                HandleGeneratedClipWritebackCompleted(clip);

                if (!KimodoEditorClipWritebackService.TryMaterializeGeneratedClipCache(
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

                if (generatedCacheClip != null)
                {
                    EditorUtility.SetDirty(generatedCacheClip);
                }

                ResetTimelineTimeScaleAfterGeneration(request);
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
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

        private static void HandleGeneratedClipWritebackCompleted(KimodoPlayableClip playableClip)
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

            source = new ArdyEditorHistorySource
            {
                TimelineContext = context,
                RangeStartSeconds = Math.Max(
                    0.0,
                    timelineClip.start - (profile.MaxContextFrames - profile.HorizonFrames) / profile.SourceFps),
                RangeEndSeconds = Math.Max(0.0, timelineClip.start)
            };
        }

    }
}
