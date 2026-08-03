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
            float targetFrameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(resolvedModelName);
            int targetFrameCount = Mathf.Max(
                KimodoPlayableClip.MIN_FRAMES,
                KimodoFrameTimeUtility.SecondsToFrameCount(timelineClip.duration, targetFrameRate));
            bool useOutsideGuardFrame = ShouldUseOutsideGuardFrame(
                clip,
                externalConstraint,
                disableTimelineInOut);
            int runtimeFrameCount = targetFrameCount + (useOutsideGuardFrame ? 1 : 0);
            int runtimeTrimStartFrame = useOutsideGuardFrame ? 1 : 0;
            double runtimeSampleOffsetSeconds = useOutsideGuardFrame ? 1.0 / targetFrameRate : 0.0;
            float runtimeLengthSeconds = runtimeFrameCount / targetFrameRate;

            string constraintsJson;
            var normalizationInfo = new KimodoConstraintNormalizationInfo();
            bool hasSyntheticAutoBeginConstraint = false;
            var constraintSamples = new List<KimodoMarkerSampleResult>();
            if (externalConstraint != null && externalConstraint.Enabled)
            {
                constraintsJson = externalConstraint.ConstraintsJson ?? string.Empty;
                normalizationInfo = externalConstraint.BuildNormalizationInfo();
                KimodoInOutConstraintComposer.AppendSamples(externalConstraint.ConstraintSamples, constraintSamples);
            }
            else
            {
                KimodoInOutConstraintResult constraintResult = ConstraintProvider.BuildConstraintDataOrThrow(
                    clip,
                    runtimeFrameCount,
                    disableTimelineInOut,
                    deferConstraintNormalization,
                    enableAutoBeginAnchor,
                    runtimeSampleOffsetSeconds);
                constraintsJson = constraintResult.ConstraintsJson ?? string.Empty;
                KimodoInOutConstraintComposer.AppendSamples(constraintResult.CombinedSamples, constraintSamples);
                hasSyntheticAutoBeginConstraint = constraintResult.HasSyntheticAutoBeginConstraint;
                if (constraintResult.NormalizationInfo != null)
                {
                    normalizationInfo = constraintResult.NormalizationInfo.Clone();
                }
            }

            ArdyEditorHistorySource initialHistorySource = null;
            if (isArdy)
            {
                if (!disableTimelineInOut)
                {
                    ResolveArdyInitialHistory(
                        clip,
                        ardyProfile,
                        out initialHistorySource);
                }
                if (externalConstraint == null || !externalConstraint.Enabled)
                {
                    AppendArdyOutsideOutRootTarget(
                        clip,
                        constraintSamples);
                }
                if (constraintSamples.Count > 0)
                {
                    constraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                        constraintSamples,
                        0.0,
                        runtimeLengthSeconds,
                        ardyProfile.SourceFps);
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
                RuntimeFrameCount = runtimeFrameCount,
                RuntimeTrimStartFrame = runtimeTrimStartFrame,
                DiffusionSteps = KimodoMotionModelProfiles.ClampDiffusionSteps(
                    resolvedModelName,
                    clip.diffusionSteps),
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
                NormalizationInfo = normalizationInfo,
                HasSyntheticAutoBeginConstraint = hasSyntheticAutoBeginConstraint,
                ConstraintSamples = constraintSamples,
                TimelineClipSnapshot = timelineClip,
                ResetTimelineTimeScaleAfterGeneration =
                    !disableTimelineInOut &&
                    (externalConstraint == null || !externalConstraint.Enabled) &&
                    clip.inOutConstraintMode == KimodoInOutConstraintMode.Inside &&
                    (clip.enableInConstraint || clip.enableOutConstraint) &&
                    !Mathf.Approximately((float)timelineClip.timeScale, 1f),
                TimelineDirectorSnapshot = outputDirector,
                TimelinePoseContextSnapshot = outputPoseContext,
                InitialArdyHistorySource = initialHistorySource,
                DisableTimelineInOut = disableTimelineInOut
            };
        }

        internal static bool AppendArdyOutsideOutRootTarget(
            KimodoPlayableClip clip,
            List<KimodoMarkerSampleResult> constraintSamples)
        {
            if (clip == null ||
                constraintSamples == null ||
                clip.inOutConstraintMode != KimodoInOutConstraintMode.Outside ||
                !clip.enableOutConstraint ||
                !KimodoMotionModelProfiles.TryGetArdy(clip.bridgeModelName, out _))
            {
                return false;
            }

            for (int i = constraintSamples.Count - 1; i >= 0; i--)
            {
                KimodoMarkerSampleResult endSample = constraintSamples[i];
                if (endSample == null ||
                    !string.Equals(endSample.constraintType, "fullbody", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                KimodoMarkerSampleResult target = endSample.Clone();
                target.constraintType = "root2d_target";
                target.rootTargetMaxSpeed = Mathf.Max(0.01f, clip.ardyTargetMaxSpeed);
                target.rootTargetMaxAcceleration = Mathf.Max(0.01f, clip.ardyTargetMaxAcceleration);
                target.rootTargetArrivalThreshold = 0.1f;
                target.rootTargetIncludeHeading = true;
                constraintSamples.Add(target);
                return true;
            }

            return false;
        }

        private static bool ShouldUseOutsideGuardFrame(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            bool disableTimelineInOut)
        {
            return clip != null &&
                !disableTimelineInOut &&
                externalConstraint?.Enabled != true &&
                clip.inOutConstraintMode == KimodoInOutConstraintMode.Outside &&
                (clip.enableInConstraint || clip.enableOutConstraint);
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
                TimelineEditor.Refresh(
                    RefreshReason.ContentsModified |
                    RefreshReason.SceneNeedsUpdate |
                    RefreshReason.WindowNeedsRedraw);
            }
            return true;
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
            ApplyTimelineOffsets(playableClip, request);
            KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
        }

        private static void ApplyTimelineOffsets(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request)
        {
            if (playableClip == null || request == null)
            {
                return;
            }
            if (request.AnchorOffsetSourceClip != null)
            {
                CopyClipOffset(request.AnchorOffsetSourceClip, playableClip);
                KimodoPlayableClipGenerationSettings.DebugLog($"[Kimodo][TimelineOffset] reused connected-sequence Hips offset for '{playableClip.name}'.");
                return;
            }

            bool hasHistoryAnchor = request.InitialArdyHistorySource?.HasTimelineWorldAnchor == true;
            KimodoConstraintNormalizationInfo normalization = request.NormalizationInfo;
            bool hasNormalizationAnchor = normalization?.Applied == true && normalization.AnchorSample != null;
            if (!hasHistoryAnchor && !hasNormalizationAnchor)
            {
                return;
            }

            TrackAsset track = request.TimelineClipSnapshot?.GetParentTrack();
            Animator animator = ResolveTimelineBindingAnimator(request.TimelineDirectorSnapshot, track);
            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                track,
                animator,
                out Vector3 trackPosition,
                out Quaternion trackRotation);

            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][TimelineOffset] resolve clip='{playableClip.name}' " +
                $"hasNormalizationAnchor={hasNormalizationAnchor} anchorKind={normalization?.AnchorKind} " +
                $"hasArdyHistoryAnchor={hasHistoryAnchor} trackPosition={trackPosition:F6} " +
                $"trackRotation={KimodoConstraintNormalizationUtility.ResolvePlanarRotation(trackRotation).eulerAngles:F6}.");

            string hipsOffsetError = string.Empty;
            if (hasHistoryAnchor &&
                request.RuntimeTrimStartFrame > 0 &&
                request.HasRetargetedLeadingGuardHipsPose)
            {
                ApplyPlanarHipsAnchorOffset(
                    playableClip,
                    trackPosition,
                    trackRotation,
                    request.InitialArdyHistorySource.TimelineWorldAnchorPosition,
                    request.InitialArdyHistorySource.TimelineWorldAnchorRotation,
                    request.RetargetedLeadingGuardHipsPosition,
                    request.RetargetedLeadingGuardHipsRotation,
                    "ArdyHistoryGuard");
                return;
            }

            if (hasHistoryAnchor &&
                TryApplyGeneratedHipsAnchorOffset(
                    playableClip,
                    request,
                    trackPosition,
                    trackRotation,
                    request.InitialArdyHistorySource.TimelineWorldAnchorPosition,
                    request.InitialArdyHistorySource.TimelineWorldAnchorRotation,
                    0f,
                    "ArdyHistoryEnd",
                    out hipsOffsetError))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(hipsOffsetError))
            {
                KimodoPlayableClipGenerationSettings.DebugLogWarning($"[Kimodo][TimelineOffset] Hips-based ARDY offset failed, falling back to root anchor: {hipsOffsetError}");
            }

            if (hasNormalizationAnchor)
            {
                KimodoMarkerSampleResult anchor = normalization.AnchorSample;
                hipsOffsetError = string.Empty;
                if (anchor.hasUnityHipsPose &&
                    TryApplyGeneratedHipsAnchorOffset(
                        playableClip,
                        request,
                        trackPosition,
                        trackRotation,
                        anchor.unityHipsPos,
                        anchor.unityHipsRot,
                        Mathf.Max(0f, (float)anchor.sampleTime),
                        normalization.AnchorKind + " Hips",
                        out hipsOffsetError))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(hipsOffsetError))
                {
                    KimodoPlayableClipGenerationSettings.DebugLogWarning($"[Kimodo][TimelineOffset] Hips-based normalization offset failed, falling back to root anchor: {hipsOffsetError}");
                }

                KimodoPlayableClipGenerationSettings.DebugLogWarning(
                    $"[Kimodo][TimelineOffset] skipped {normalization.AnchorKind} alignment because its world Hips pose is unavailable.");
                return;
            }

            KimodoPlayableClipGenerationSettings.DebugLogWarning($"[Kimodo][TimelineOffset] skipped alignment: {hipsOffsetError}");
        }

        private static bool TryApplyGeneratedHipsAnchorOffset(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request,
            Vector3 trackPosition,
            Quaternion trackRotation,
            Vector3 sourceHipsPosition,
            Quaternion sourceHipsRotation,
            float generatedSampleTime,
            string anchorLabel,
            out string error)
        {
            error = string.Empty;
            AnimationClip generatedClip = playableClip != null && playableClip.clip != null
                ? playableClip.clip
                : request?.TargetClip;
            Avatar samplingAvatar = ResolveGeneratedClipSamplingAvatar(request);
            if (generatedClip == null)
            {
                error = "generated clip is null";
                return false;
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(samplingAvatar))
            {
                error = "generated clip sampling avatar is null/invalid/non-humanoid";
                return false;
            }

            if (!TrySampleGeneratedClipHipsPose(
                    generatedClip,
                    samplingAvatar,
                    generatedSampleTime,
                    out Vector3 generatedHipsPosition,
                    out Quaternion generatedHipsRotation,
                    out error))
            {
                return false;
            }

            ApplyPlanarHipsAnchorOffset(
                playableClip,
                trackPosition,
                trackRotation,
                sourceHipsPosition,
                sourceHipsRotation,
                generatedHipsPosition,
                generatedHipsRotation,
                anchorLabel);
            return true;
        }

        private static void ApplyPlanarHipsAnchorOffset(
            KimodoPlayableClip playableClip,
            Vector3 trackPosition,
            Quaternion trackRotation,
            Vector3 sourceHipsPosition,
            Quaternion sourceHipsRotation,
            Vector3 generatedHipsPosition,
            Quaternion generatedHipsRotation,
            string anchorLabel)
        {
            Quaternion trackYaw = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(trackRotation);
            Quaternion sourceHipsYaw = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(sourceHipsRotation);
            Quaternion generatedHipsYaw = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(generatedHipsRotation);
            Quaternion clipYaw = (Quaternion.Inverse(trackYaw) * sourceHipsYaw * Quaternion.Inverse(generatedHipsYaw)).normalized;
            Vector3 sourceHipsTrackLocal = Quaternion.Inverse(trackYaw) *
                (new Vector3(sourceHipsPosition.x, 0f, sourceHipsPosition.z) -
                 new Vector3(trackPosition.x, 0f, trackPosition.z));
            Vector3 generatedHipsPlanar = new Vector3(generatedHipsPosition.x, 0f, generatedHipsPosition.z);
            Vector3 clipPosition = sourceHipsTrackLocal - (clipYaw * generatedHipsPlanar);

            playableClip.position = new Vector3(clipPosition.x, playableClip.position.y, clipPosition.z);
            playableClip.rotation = clipYaw;
            playableClip.removeStartOffset = false;
            EditorUtility.SetDirty(playableClip);
            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][TimelineOffset] applied {anchorLabel} planar Hips anchor to '{playableClip.name}': " +
                $"sourceHips={sourceHipsPosition:F6}, generatedHips={generatedHipsPosition:F6}, " +
                $"sourceHipsTrackLocal={sourceHipsTrackLocal:F6}, generatedHipsPlanar={generatedHipsPlanar:F6}, " +
                $"computedPosition={clipPosition:F6}, position={playableClip.position}, " +
                $"rotation={playableClip.rotation.eulerAngles}.");
        }

        internal static Avatar ResolveGeneratedClipSamplingAvatar(KimodoEditorGenerateRequest request)
        {
            KimodoEditorGenerateOutputPlan plan = request?.OutputPlan;
            if (plan != null && plan.SkipRetarget && KimodoRetargetCoreUtility.IsValidHumanoid(plan.OriginRetargetAvatar))
            {
                return plan.OriginRetargetAvatar;
            }
            if (plan != null && KimodoRetargetCoreUtility.IsValidHumanoid(plan.TargetRetargetAvatar))
            {
                return plan.TargetRetargetAvatar;
            }
            if (plan != null && KimodoRetargetCoreUtility.IsValidHumanoid(plan.OriginRetargetAvatar))
            {
                return plan.OriginRetargetAvatar;
            }

            return null;
        }

        internal static bool TrySampleGeneratedClipHipsPose(
            AnimationClip generatedClip,
            Avatar samplingAvatar,
            float sampleTime,
            out Vector3 position,
            out Quaternion rotation,
            out string error)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            SkeletonCache cache = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        samplingAvatar,
                        "KimodoArdyGeneratedHipsOffsetSampler",
                        out cache,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.SampleBoneClipToBoneSample(
                        generatedClip,
                        cache,
                        Mathf.Clamp(sampleTime, 0f, Mathf.Max(0f, generatedClip.length)),
                        out BoneSample sample,
                        out error) ||
                    !KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(sample, cache, out error))
                {
                    return false;
                }

                if (cache.humanBoneTransforms == null ||
                    !cache.humanBoneTransforms.TryGetValue(HumanBodyBones.Hips, out Transform hips) ||
                    hips == null)
                {
                    error = "generated clip sampling avatar has no Hips transform";
                    return false;
                }

                position = hips.position;
                rotation = hips.rotation.normalized;
                return true;
            }
            finally
            {
                cache?.Dispose();
            }
        }

        private static Animator ResolveTimelineBindingAnimator(PlayableDirector director, TrackAsset track)
        {
            UnityEngine.Object binding = director != null && track != null
                ? director.GetGenericBinding(track)
                : null;
            return binding as Animator ??
                (binding as GameObject)?.GetComponentInChildren<Animator>(true);
        }

        private static void CopyClipOffset(KimodoPlayableClip source, KimodoPlayableClip destination)
        {
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

        internal static Avatar ResolveOriginRetargetAvatar(string modelName)
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
