using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
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
            bool deferConstraintNormalization = false,
            bool enableAutoBeginAnchor = true)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(clip.bridgeModelName);
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(
                resolvedModelName,
                out KimodoMotionModelProfile ardyProfile);
            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (timelineClip == null || timelineClip.duration <= 0.0)
            {
                throw new InvalidOperationException("Generation length requires a Timeline clip with positive duration.");
            }
            float targetFrameRate = isArdy ? ardyProfile.SourceFps : KimodoPlayableClip.FIXED_FRAME_RATE;
            int targetFrameCount = Mathf.Max(
                KimodoPlayableClip.MIN_FRAMES,
                KimodoFrameTimeUtility.SecondsToFrameCount(timelineClip.duration, targetFrameRate));
            int constraintFrames = Mathf.Max(
                KimodoPlayableClip.MIN_FRAMES,
                KimodoFrameTimeUtility.SecondsToFrameCount(
                    timelineClip.duration,
                    KimodoPlayableClip.FIXED_FRAME_RATE));
            float targetLengthSeconds = targetFrameCount / targetFrameRate;

            string constraintsJson;
            bool normalizeConstraintOriginApplied = false;
            KimodoConstraintNormalizationAnchorKind normalizationAnchorKind = KimodoConstraintNormalizationAnchorKind.None;
            KimodoMarkerSampleResult normalizationAnchorSample = null;
            KimodoMarkerSampleResult autoBeginAnchorSample = null;
            var constraintSamples = new List<KimodoMarkerSampleResult>();
            if (externalConstraint != null && externalConstraint.Enabled)
            {
                constraintsJson = externalConstraint.ConstraintsJson ?? string.Empty;
                normalizeConstraintOriginApplied = externalConstraint.NormalizeConstraintOriginApplied;
                normalizationAnchorKind = externalConstraint.NormalizationAnchorKind;
                normalizationAnchorSample = externalConstraint.NormalizationAnchorSample != null
                    ? externalConstraint.NormalizationAnchorSample.Clone()
                    : null;
                AppendSamples(externalConstraint.ConstraintSamples, constraintSamples);
            }
            else
            {
                KimodoInOutConstraintResult constraintResult = ConstraintProvider.BuildConstraintDataOrThrow(
                    clip,
                    constraintFrames,
                    disableTimelineInOut,
                    deferConstraintNormalization,
                    enableAutoBeginAnchor);
                constraintsJson = constraintResult.ConstraintsJson ?? string.Empty;
                AppendSamples(constraintResult.CombinedSamples, constraintSamples);
                autoBeginAnchorSample = constraintResult.AutoBeginAnchorSample != null
                    ? constraintResult.AutoBeginAnchorSample.Clone()
                    : null;
                if (constraintResult.NormalizationInfo != null)
                {
                    normalizeConstraintOriginApplied = constraintResult.NormalizationInfo.Applied;
                    normalizationAnchorKind = constraintResult.NormalizationInfo.AnchorKind;
                    normalizationAnchorSample = constraintResult.NormalizationInfo.AnchorSample != null
                        ? constraintResult.NormalizationInfo.AnchorSample.Clone()
                        : null;
                }
            }

            ArdyEditorHistorySource initialHistorySource = null;
            if (isArdy)
            {
                if (constraintSamples.Count > 0)
                {
                    constraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                        constraintSamples,
                        0.0,
                    targetLengthSeconds,
                    ardyProfile.SourceFps);
                }
                if (!disableTimelineInOut)
                {
                    ResolveArdyInitialHistory(
                        clip,
                        ardyProfile,
                        out initialHistorySource);
                }
            }
            int effectiveSeed = effectiveSeedOverride ?? ResolveEffectiveSeed(clip);
            if (effectiveSeedOverride.HasValue && clip.seed != effectiveSeed)
            {
                clip.seed = effectiveSeed;
                EditorUtility.SetDirty(clip);
            }
            GameObject outputBindingObject = ConstraintProvider.FindTimelineBindingObjectForAsset(clip);
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
            PoseCacheRenderContext? outputPoseContext =
                KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForPlayableClip(
                    clip,
                    out PoseCacheRenderContext capturedPoseContext,
                    out _,
                    out _)
                        ? capturedPoseContext
                        : null;
            KimodoEditorGenerateOutputPlan outputPlanSnapshot = CaptureTimelineOutputPlan(
                clip,
                externalConstraint?.RetargetAvatar,
                resolvedModelName,
                outputBindingObject);
            return new KimodoEditorGenerateRequest
            {
                Prompt = prompt,
                ModelName = resolvedModelName,
                TextEncoderMode = clip.textEncoderMode,
                TargetFrameCount = targetFrameCount,
                TargetFrameRate = targetFrameRate,
                DiffusionSteps = isArdy
                    ? Mathf.Clamp(clip.diffusionSteps, 0, ardyProfile.MaxDiffusionSteps)
                    : Mathf.Clamp(clip.diffusionSteps, 1, 1000),
                TextWeight = Mathf.Clamp(clip.textWeight, 0f, 4f),
                EffectiveSeed = effectiveSeed,
                ConstraintsJson = constraintsJson,
                CreateTargetClip = () => CreateTimelineTargetClip(clip),
                ResolveOutputPlan = (generatedClip, modelName) => ResolveTimelineOutputPlan(
                    outputPlanSnapshot,
                    outputBindingObject,
                    generatedClip,
                    modelName),
                OutputPlan = outputPlanSnapshot,
                ModelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim() ?? string.Empty,
                GenerationTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                Token = token,
                NormalizeConstraintOriginApplied = normalizeConstraintOriginApplied,
                NormalizationAnchorKind = normalizationAnchorKind,
                NormalizationAnchorSample = normalizationAnchorSample,
                AutoBeginAnchorSample = autoBeginAnchorSample,
                ConstraintSamples = constraintSamples,
                TimelineClipSnapshot = timelineClip,
                TimelineDirectorSnapshot = outputDirector,
                TimelinePoseContextSnapshot = outputPoseContext,
                InitialArdyHistorySource = initialHistorySource,
                DisableTimelineInOut = disableTimelineInOut
            };
        }

        public static void FinalizeGeneration(
            KimodoPlayableClip clip,
            KimodoEditorGenerateRequest request,
            KimodoEditorGenerateResult result)
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
                clip.ardyMotionCachePath = result.ArdyMotionCachePath ?? string.Empty;
                // Per-request KMB data is released after the final clip is materialized.
                clip.ardyMotionRepFingerprint = result.ArdyMotionRepFingerprint ?? string.Empty;
                clip.ardyResolvedSeeds = result.ArdyResolvedSeeds ?? new List<int>();
                EditorUtility.SetDirty(clip);
                EditorUtility.SetDirty(result.GeneratedClip);
                result.ConstraintsPath = string.IsNullOrWhiteSpace(request.ConstraintsJson) ? "(none)" : "(inline-json)";
                HandleGeneratedClipWritebackCompleted(clip, request);

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
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        public static void CleanupFailedGeneration(KimodoEditorGenerateRequest request)
        {
            if (request == null)
            {
                return;
            }

            TryCleanupGeneratedClip(request.TargetClip);
            if (!ReferenceEquals(request.RawBoneClip, request.TargetClip))
            {
                TryCleanupGeneratedClip(request.RawBoneClip);
            }
        }

        private static void TryCleanupGeneratedClip(AnimationClip clip)
        {
            if (clip == null)
            {
                return;
            }

            KimodoEditorClipWritebackService.TryDeleteGeneratedAnimationClipAsset(clip);
        }

        public static IReadOnlyList<KimodoConstraintMarkerBase> GetLatestConstraintMarkers()
        {
            return KimodoEditorConstraintProvider.LatestMarkers;
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
            clip.fps = Mathf.RoundToInt(obj.Value<float?>("fps") ?? KimodoPlayableClip.FIXED_FRAME_RATE);
        }

        private static void HandleGeneratedClipWritebackCompleted(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request)
        {
            KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
            ApplyTimelineOffsets(playableClip, request);
        }

        private static void ApplyTimelineOffsets(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request)
        {
            if (playableClip == null || request?.ContinuousOffsetSourceClip == null)
            {
                return;
            }

            CopyClipOffset(request.ContinuousOffsetSourceClip, playableClip);
            Debug.Log($"[Kimodo][TimelineOffset] copied continuous ARDY offset to '{playableClip.name}'.");
        }

        private static void CopyClipOffset(KimodoPlayableClip source, KimodoPlayableClip destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            destination.position = source.position;
            destination.rotation = source.rotation;
            destination.removeStartOffset = source.removeStartOffset;
            EditorUtility.SetDirty(destination);
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

        private static Avatar ResolveOriginRetargetAvatar(string modelName)
        {
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar avatar, out _))
            {
                return null;
            }

            return KimodoRetargetCoreUtility.IsValidHumanoid(avatar) ? avatar : null;
        }

        private static AnimationClip CreateTimelineTargetClip(KimodoPlayableClip clip)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            return KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                BuildTimelineTargetClipName(clip.bridgeModelName, DateTime.Now));
        }

        internal static string BuildTimelineTargetClipName(string modelName, DateTime timestamp)
        {
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out _);
            return $"{(isArdy ? "ARDY" : "Kimodo")}_Playable_{timestamp:yyyyMMdd_HHmmss_fff}";
        }

        internal static KimodoEditorGenerateOutputPlan CaptureTimelineOutputPlan(
            KimodoPlayableClip clip,
            Avatar explicitRetargetAvatar,
            string modelName,
            GameObject bindingObject)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            Avatar originRetargetAvatar = ResolveOriginRetargetAvatar(resolvedModelName);
            Avatar targetRetargetAvatar = ResolveTargetRetargetAvatar(
                clip,
                explicitRetargetAvatar,
                bindingObject,
                out bool hasBindingAvatar);
            bool hasValidRetargetAvatar =
                KimodoRetargetCoreUtility.IsValidHumanoid(originRetargetAvatar) &&
                hasBindingAvatar &&
                KimodoRetargetCoreUtility.IsValidHumanoid(targetRetargetAvatar);

            return new KimodoEditorGenerateOutputPlan
            {
                OriginRetargetAvatar = originRetargetAvatar,
                TargetRetargetAvatar = targetRetargetAvatar,
                ExportMuscleClip = hasValidRetargetAvatar && TryResolveBindingAnimatorAvatar(bindingObject, out _),
                CurveFilterOptions = CloneCurveFilterOptions(clip.curveFilterOptions),
                SkipRetarget = false
            };
        }

        internal static KimodoEditorGenerateOutputPlan ResolveTimelineOutputPlan(
            KimodoEditorGenerateOutputPlan snapshot,
            GameObject bindingObject,
            AnimationClip generatedClip,
            string modelName)
        {
            if (snapshot == null)
            {
                throw new InvalidOperationException("Timeline output plan snapshot is null.");
            }

            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            bool canSkipRetarget =
                bindingObject != null &&
                KimodoEditorClipUtility.CanApplyClipDirectlyToProfileSkeleton(generatedClip, bindingObject, resolvedModelName, out _);

            return new KimodoEditorGenerateOutputPlan
            {
                OriginRetargetAvatar = snapshot.OriginRetargetAvatar,
                TargetRetargetAvatar = snapshot.TargetRetargetAvatar,
                ExportMuscleClip = snapshot.ExportMuscleClip,
                CurveFilterOptions = snapshot.CurveFilterOptions,
                SkipRetarget = canSkipRetarget
            };
        }

        private static Avatar ResolveTargetRetargetAvatar(
            KimodoPlayableClip clip,
            Avatar explicitRetargetAvatar,
            GameObject bindingObject,
            out bool hasBindingAvatar)
        {
            hasBindingAvatar = false;
            if (explicitRetargetAvatar != null && explicitRetargetAvatar.isValid && explicitRetargetAvatar.isHuman)
            {
                hasBindingAvatar = true;
                return explicitRetargetAvatar;
            }

            if (bindingObject != null)
            {
                KimodoLocalAvatarUtility.AvatarResolveResult result = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(bindingObject);
                if (result.IsHumanoid && result.Avatar != null)
                {
                    Animator animator = bindingObject.GetComponent<Animator>();
                    hasBindingAvatar = animator != null && animator.avatar != null;
                    return result.Avatar;
                }
            }

            if (clip.CustomRetargetAvatar != null && clip.CustomRetargetAvatar.isValid && clip.CustomRetargetAvatar.isHuman)
            {
                return clip.CustomRetargetAvatar;
            }

            return null;
        }

        private static bool TryResolveBindingAnimatorAvatar(GameObject bindingObject, out Avatar avatar)
        {
            avatar = null;
            if (bindingObject == null)
            {
                return false;
            }

            KimodoLocalAvatarUtility.AvatarResolveResult result = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(bindingObject);
            if (!result.IsHumanoid || result.Avatar == null)
            {
                return false;
            }

            if (!string.Equals(result.Source, "Animator", StringComparison.Ordinal))
            {
                return false;
            }

            avatar = result.Avatar;
            return true;
        }

        private static KimodoCurveFilterOptions CloneCurveFilterOptions(KimodoCurveFilterOptions source)
        {
            source ??= new KimodoCurveFilterOptions();
            return new KimodoCurveFilterOptions
            {
                enabled = source.enabled,
                positionError = source.positionError,
                rotationError = source.rotationError,
                floatError = source.floatError,
                ensureQuaternionContinuity = source.ensureQuaternionContinuity
            };
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

        private static void AppendSamples(
            IReadOnlyList<KimodoMarkerSampleResult> source,
            List<KimodoMarkerSampleResult> destination)
        {
            if (source == null)
            {
                return;
            }
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    destination.Add(source[i].Clone());
                }
            }
        }

        private static void ResolveArdyInitialHistory(
            KimodoPlayableClip clip,
            KimodoMotionModelProfile profile,
            out ArdyEditorHistorySource source)
        {
            source = null;
            if (clip.inOutConstraintMode != KimodoInOutConstraintMode.Outside || !clip.enableInConstraint)
            {
                return;
            }

            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
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
