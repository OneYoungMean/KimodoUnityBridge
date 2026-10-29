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
            bool deferConstraintNormalization = false,
            bool enableAutoBeginAnchor = true,
            TimelineClip timelineClipOverride = null,
            bool? generateLoopOverride = null,
            bool deferOutConstraint = false,
            bool deferInConstraint = false,
            double? targetDurationSecondsOverride = null,
            bool disableOutsideContextExpansion = false)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            // Ensure legacy model-rate window fields are converted before any
            // request snapshot captures the new 60 FPS command-time values.
            clip.MigrateLegacyWindowFrameCounts();

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
            double targetDurationSeconds = targetDurationSecondsOverride.HasValue
                ? Math.Max(0.0, targetDurationSecondsOverride.Value)
                : timelineClip.duration;
            int targetFrameCount = Mathf.Max(
                KimodoMotionModelProfiles.MinGenerationFrames,
                KimodoFrameTimeUtility.SecondsToFrameCount(targetDurationSeconds, targetFrameRate));
            // Keep the backend request at the requested length. Short clips may
            // still receive context frames below, but are no longer expanded to
            // a separate minimum generation duration.
            int generationFrameCount = targetFrameCount;
            int sourceSessionFrameCount = Mathf.Max(1, Mathf.RoundToInt((float)(timelineClip.duration * 60.0)));
            bool isLoopGeneration = (generateLoopOverride ?? clip.generateLoop) && sourceSessionFrameCount <= 300;
            int loopPaddingFrames = isLoopGeneration ? generationFrameCount / 2 : 0;
            bool useOutsideGuardFrame = !disableOutsideContextExpansion && ShouldUseOutsideGuardFrame(
                clip,
                externalConstraint,
                disableTimelineInOut);
            int contextBefore = useOutsideGuardFrame ? 1 : 0;
            int contextAfter = 0;
            if (!disableOutsideContextExpansion && !isArdy && !disableTimelineInOut &&
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
            int runtimeFrameCount = (isLoopGeneration ? generationFrameCount * 2 : generationFrameCount) +
                contextBefore + contextAfter;
            int runtimeTrimStartFrame = loopPaddingFrames + contextBefore;
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
                    timelineClip,
                    deferOutConstraint,
                    deferInConstraint,
                    runtimeTrimStartFrame,
                    targetFrameCount);
            string constraintsJson = constraintResult.ConstraintsJson;
            string deferredInConstraintJson = constraintResult.DeferredInConstraintJson;
            string deferredOutConstraintJson = constraintResult.DeferredOutConstraintJson;
            if (deferInConstraint)
            {
                KimodoInOutConstraintResult deferredIn =
                    ConstraintProvider.BuildDeferredInConstraintOrThrow(
                        clip,
                        runtimeFrameCount,
                        disableTimelineInOut,
                        runtimeSampleOffsetSeconds,
                        timelineClip,
                        runtimeTrimStartFrame,
                        targetFrameCount);
                if (!string.IsNullOrWhiteSpace(deferredIn.ConstraintsJson))
                {
                    deferredInConstraintJson = CombineConstraintJson(
                        deferredInConstraintJson,
                        deferredIn.ConstraintsJson);
                }
            }
            if (deferOutConstraint)
            {
                KimodoInOutConstraintResult deferredOut =
                    ConstraintProvider.BuildDeferredOutConstraintOrThrow(
                        clip,
                        runtimeFrameCount,
                        disableTimelineInOut,
                        runtimeSampleOffsetSeconds,
                        timelineClip,
                        runtimeTrimStartFrame,
                        targetFrameCount);
                if (!string.IsNullOrWhiteSpace(deferredOut.ConstraintsJson))
                {
                    deferredOutConstraintJson = CombineConstraintJson(
                        deferredOutConstraintJson,
                        deferredOut.ConstraintsJson);
                }
            }
            string inConstraintWindowJson = !string.IsNullOrWhiteSpace(deferredInConstraintJson)
                ? deferredInConstraintJson
                : constraintResult.InConstraintWindowJson;
            string outConstraintWindowJson = !string.IsNullOrWhiteSpace(deferredOutConstraintJson)
                ? deferredOutConstraintJson
                : constraintResult.OutConstraintWindowJson;
            List<KimodoMarkerSampleResult> constraintSamples = constraintResult.CombinedSamples;
            if ((deferInConstraint || deferOutConstraint) && externalConstraint?.Enabled == true &&
                ((deferInConstraint && externalConstraint.DeferredInConstraintSamples?.Count > 0) ||
                 (deferOutConstraint && externalConstraint.DeferredOutConstraintSamples?.Count > 0)))
            {
                constraintSamples = new List<KimodoMarkerSampleResult>(constraintSamples);
                if (deferInConstraint)
                    KimodoInOutConstraintComposer.AppendSamples(externalConstraint.DeferredInConstraintSamples, constraintSamples);
                if (deferOutConstraint)
                    KimodoInOutConstraintComposer.AppendSamples(externalConstraint.DeferredOutConstraintSamples, constraintSamples);
                int immediateCount = constraintResult.CombinedSamples.Count;
                for (int i = immediateCount; i < constraintSamples.Count; i++)
                {
                    constraintSamples[i].sampleTime += runtimeSampleOffsetSeconds;
                }
            }
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
                explicitRetargetAvatar: null,
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
                LongClipWindowFrames60 = Mathf.Max(1, clip.longClipWindowFrames60),
                LongClipWindowSampleCount = Mathf.Max(1, clip.longClipWindowSampleCount),
                DiffusionSteps = KimodoMotionModelProfiles.ClampDiffusionSteps(
                    resolvedModelName,
                    clip.diffusionSteps),
                EffectiveSeed = effectiveSeed,
                Constraints = new KimodoConstraintPayload { json = constraintsJson, clips = clipConstraints },
                DeferredInConstraintJson = deferredInConstraintJson,
                DeferredOutConstraintJson = deferredOutConstraintJson,
                InConstraintWindowJson = inConstraintWindowJson ?? string.Empty,
                OutConstraintWindowJson = outConstraintWindowJson ?? string.Empty,
                InConstraintWindowModelFrameCount = constraintResult.BeginBoundaryWindowModelFrameCount,
                OutConstraintWindowModelFrameCount = constraintResult.EndBoundaryWindowModelFrameCount,
                // Legacy per-clip lightweight analysis settings remain serialized
                // for existing assets, but normal Inspector generation now uses
                // the full animation.analysis path after baking.
                AnalysisOptionsJson = externalConstraint?.AnalysisOptionsJson ?? string.Empty,
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
                UseRelativeOffset = clip.useRelativeOffset,
                RelativeOffset = clip.relativeOffset,
                TimelineDirectorSnapshot = outputDirector,
                TimelineContextSnapshot = timelineContextSnapshot,
                InitialArdyHistorySource = initialHistorySource,
                ArdyHistoryWeight = isArdy && !clip.ardyAutoHistory
                    ? Mathf.Clamp01(clip.ardyHistoryWeight)
                    : (double?)null
            };
        }

        private static string CombineConstraintJson(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second ?? string.Empty;
            if (string.IsNullOrWhiteSpace(second)) return first;
            var combined = JArray.Parse(first);
            combined.Merge(JArray.Parse(second));
            return combined.ToString(Newtonsoft.Json.Formatting.None);
        }

        // TODO: expose an internal generation entry that supplies an AvatarMask when Clip Constraint is ready.
        internal static bool TryGetClipConstraintAvatarMask(
            KimodoPlayableClip clip,
            out AvatarMask avatarMask)
        {
            avatarMask = null;
            return false;
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
                ApplyGeneratedMetadata(clip, result.Prompt, result.MotionJsonCompact);
                EditorUtility.SetDirty(clip);
                EditorUtility.SetDirty(result.GeneratedClip);
                result.ConstraintsPath = request.Constraints.IsEmpty ? "(none)" : "(inline-json)";
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
                if (playableClip.useRelativeOffset && playableClip.clip != null)
                {
                    playableClip.position = ResolveRelativeOffsetInTrackSpace(playableClip, request);
                }
                else
                {
                    playableClip.position = Vector3.zero;
                }
                playableClip.rotation = playableClip.useRelativeOffset
                    ? request?.RelativeRootOriginRotation ?? Quaternion.identity
                    : Quaternion.identity;
                EditorUtility.SetDirty(playableClip);
            }
            KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
        }

        internal static Vector3 ResolveRelativeOffsetInTrackSpace(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request)
        {
            Vector3 worldOffset = playableClip != null ? playableClip.relativeOffset : Vector3.zero;
            Vector3 rootOrigin = Vector3.up * (request?.RelativeRootOriginY ?? 0f);
            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(playableClip);
            if (!(timelineClip?.GetParentTrack() is AnimationTrack track)) return worldOffset;

            Vector3 trackPosition;
            Quaternion trackRotation;
            KimodoTimelineInOutConstraintContext context = request?.TimelineContextSnapshot;
            if (context != null && ReferenceEquals(context.Track, track) && context.HasTrackOffsetSnapshot)
            {
                trackPosition = context.TrackOffsetPosition;
                trackRotation = context.TrackOffsetRotation;
            }
            else
            {
                if (!KimodoInOutConstraintAdapter.TryResolveTimelineContext(timelineClip, out context, out _) || context == null)
                    return worldOffset;
                KimodoTimelineTrackOffsetUtility.CaptureWorldOffset(
                    context.Track,
                    context.Animator,
                    out trackPosition,
                    out trackRotation,
                    out _);
            }

            KimodoTimelineTrackOffsetUtility.WorldToTrackPose(
                worldOffset,
                Quaternion.identity,
                trackPosition,
                trackRotation,
                out Vector3 trackLocalOffset,
                out _);
            return trackLocalOffset - rootOrigin;
        }

        internal static void NormalizeRootPathToFrameZero(
            AnimationClip clip,
            out float rootOriginY,
            out Quaternion rootOriginRotation)
        {
            rootOriginY = 0f;
            rootOriginRotation = Quaternion.identity;
            if (clip == null) return;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            string transformRootPath = null;
            AnimationCurve transformX = null;
            AnimationCurve transformY = null;
            AnimationCurve transformZ = null;
            var transformCurves = new Dictionary<string, AnimationCurve[]>();
            var transformRotationCurves = new Dictionary<string, AnimationCurve[]>();
            AnimationCurve animatorX = null;
            AnimationCurve animatorY = null;
            AnimationCurve animatorZ = null;
            AnimationCurve animatorQx = null;
            AnimationCurve animatorQy = null;
            AnimationCurve animatorQz = null;
            AnimationCurve animatorQw = null;
            foreach (EditorCurveBinding binding in bindings)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (binding.type == typeof(Transform) &&
                    (binding.propertyName == "m_LocalPosition.x" ||
                     binding.propertyName == "m_LocalPosition.y" ||
                     binding.propertyName == "m_LocalPosition.z"))
                {
                    if (!transformCurves.TryGetValue(binding.path, out AnimationCurve[] curves))
                    {
                        curves = new AnimationCurve[3];
                        transformCurves.Add(binding.path, curves);
                    }
                    if (binding.propertyName == "m_LocalPosition.x") curves[0] = curve;
                    else if (binding.propertyName == "m_LocalPosition.y") curves[1] = curve;
                    else curves[2] = curve;
                }
                else if (binding.type == typeof(Transform) &&
                    (binding.propertyName == "m_LocalRotation.x" ||
                     binding.propertyName == "m_LocalRotation.y" ||
                     binding.propertyName == "m_LocalRotation.z" ||
                     binding.propertyName == "m_LocalRotation.w"))
                {
                    if (!transformRotationCurves.TryGetValue(binding.path, out AnimationCurve[] curves))
                    {
                        curves = new AnimationCurve[4];
                        transformRotationCurves.Add(binding.path, curves);
                    }
                    if (binding.propertyName == "m_LocalRotation.x") curves[0] = curve;
                    else if (binding.propertyName == "m_LocalRotation.y") curves[1] = curve;
                    else if (binding.propertyName == "m_LocalRotation.z") curves[2] = curve;
                    else curves[3] = curve;
                }
                else if (binding.type == typeof(Animator) && binding.path == string.Empty)
                {
                    if (binding.propertyName == "RootT.x") animatorX = curve;
                    else if (binding.propertyName == "RootT.y") animatorY = curve;
                    else if (binding.propertyName == "RootT.z") animatorZ = curve;
                    else if (binding.propertyName == "RootQ.x") animatorQx = curve;
                    else if (binding.propertyName == "RootQ.y") animatorQy = curve;
                    else if (binding.propertyName == "RootQ.z") animatorQz = curve;
                    else if (binding.propertyName == "RootQ.w") animatorQw = curve;
                }
            }

            foreach (KeyValuePair<string, AnimationCurve[]> pair in transformCurves)
            {
                AnimationCurve[] curves = pair.Value;
                if (curves[0] == null || curves[1] == null || curves[2] == null) continue;
                if (transformRootPath == null || CountPathDepth(pair.Key) < CountPathDepth(transformRootPath))
                {
                    transformRootPath = pair.Key;
                    transformX = curves[0];
                    transformY = curves[1];
                    transformZ = curves[2];
                }
            }

            bool useTransformRoot = transformX != null && transformY != null && transformZ != null;
            AnimationCurve x = useTransformRoot ? transformX : animatorX;
            AnimationCurve y = useTransformRoot ? transformY : animatorY;
            AnimationCurve z = useTransformRoot ? transformZ : animatorZ;
            string path = useTransformRoot ? transformRootPath : string.Empty;
            Type bindingType = useTransformRoot ? typeof(Transform) : typeof(Animator);
            string xProperty = bindingType == typeof(Transform) ? "m_LocalPosition.x" : "RootT.x";
            string yProperty = bindingType == typeof(Transform) ? "m_LocalPosition.y" : "RootT.y";
            string zProperty = bindingType == typeof(Transform) ? "m_LocalPosition.z" : "RootT.z";
            if (x == null || y == null || z == null || x.length == 0 || y.length == 0 || z.length == 0) return;
            Vector3 origin = new Vector3(x.Evaluate(0f), y.Evaluate(0f), z.Evaluate(0f));
            rootOriginY = origin.y;
            Quaternion originYaw = Quaternion.identity;
            AnimationCurve[] rootRotation = null;
            if (useTransformRoot)
            {
                transformRotationCurves.TryGetValue(path, out rootRotation);
            }
            else if (animatorQx != null && animatorQy != null && animatorQz != null && animatorQw != null)
            {
                rootRotation = new[] { animatorQx, animatorQy, animatorQz, animatorQw };
            }
            if (rootRotation != null && rootRotation.All(curve => curve != null && curve.length > 0))
            {
                originYaw = GetRootOriginYaw(rootRotation);
                NormalizeRootHorizontalPath(x, y, z, origin, originYaw);
            }
            else
            {
                OffsetCurve(x, origin.x);
                OffsetCurve(z, origin.z);
            }
            clip.SetCurve(path, bindingType, xProperty, x);
            clip.SetCurve(path, bindingType, yProperty, y);
            clip.SetCurve(path, bindingType, zProperty, z);
            if (rootRotation != null && rootRotation.All(curve => curve != null && curve.length > 0))
            {
                rootOriginRotation = NormalizeRootYawToFrameZero(rootRotation, originYaw);
                if (useTransformRoot)
                {
                    clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rootRotation[0]);
                    clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rootRotation[1]);
                    clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rootRotation[2]);
                    clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rootRotation[3]);
                }
                else
                {
                    clip.SetCurve(string.Empty, typeof(Animator), "RootQ.x", rootRotation[0]);
                    clip.SetCurve(string.Empty, typeof(Animator), "RootQ.y", rootRotation[1]);
                    clip.SetCurve(string.Empty, typeof(Animator), "RootQ.z", rootRotation[2]);
                    clip.SetCurve(string.Empty, typeof(Animator), "RootQ.w", rootRotation[3]);
                }
            }
            EditorUtility.SetDirty(clip);
        }

        internal static bool TryReadRootOriginY(AnimationClip clip, out float rootOriginY)
        {
            rootOriginY = 0f;
            if (clip == null) return false;
            AnimationCurve rootT = null;
            var transformCurves = new Dictionary<string, AnimationCurve[]>();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (binding.type == typeof(Animator) && binding.path == string.Empty && binding.propertyName == "RootT.y")
                {
                    rootT = curve;
                    continue;
                }
                if (binding.type != typeof(Transform)) continue;
                int axis = binding.propertyName == "m_LocalPosition.x" ? 0
                    : binding.propertyName == "m_LocalPosition.y" ? 1
                    : binding.propertyName == "m_LocalPosition.z" ? 2 : -1;
                if (axis < 0) continue;
                if (!transformCurves.TryGetValue(binding.path, out AnimationCurve[] curves))
                {
                    curves = new AnimationCurve[3];
                    transformCurves.Add(binding.path, curves);
                }
                curves[axis] = curve;
            }
            if (rootT != null && rootT.length > 0)
            {
                rootOriginY = rootT.Evaluate(0f);
                return true;
            }
            string rootPath = null;
            AnimationCurve transformY = null;
            foreach (KeyValuePair<string, AnimationCurve[]> pair in transformCurves)
            {
                AnimationCurve[] curves = pair.Value;
                if (curves[0] == null || curves[1] == null || curves[2] == null || curves[1].length == 0)
                    continue;
                if (rootPath == null || CountPathDepth(pair.Key) < CountPathDepth(rootPath))
                {
                    rootPath = pair.Key;
                    transformY = curves[1];
                }
            }
            if (transformY == null) return false;
            rootOriginY = transformY.Evaluate(0f);
            return true;
        }

        private static Quaternion GetRootOriginYaw(AnimationCurve[] curves)
        {
            Quaternion origin = new Quaternion(
                curves[0].Evaluate(0f), curves[1].Evaluate(0f),
                curves[2].Evaluate(0f), curves[3].Evaluate(0f)).normalized;
            return KimodoBridge.KimodoMotionMath.ResolvePlanarHeading(origin);
        }

        private static Quaternion NormalizeRootYawToFrameZero(AnimationCurve[] curves, Quaternion originYaw)
        {
            var normalizedAtTime = new Dictionary<float, Quaternion>();
            foreach (float time in curves.SelectMany(curve => curve.keys).Select(key => key.time).Distinct())
            {
                Quaternion value = new Quaternion(
                    curves[0].Evaluate(time), curves[1].Evaluate(time),
                    curves[2].Evaluate(time), curves[3].Evaluate(time)).normalized;
                normalizedAtTime[time] = KimodoBridge.KimodoMotionMath.NormalizePlanarHeading(value, originYaw);
            }
            for (int component = 0; component < curves.Length; component++)
            {
                Keyframe[] componentKeys = curves[component].keys;
                for (int i = 0; i < componentKeys.Length; i++)
                {
                    Quaternion normalized = normalizedAtTime[componentKeys[i].time];
                    componentKeys[i].value = component == 0 ? normalized.x
                        : component == 1 ? normalized.y
                        : component == 2 ? normalized.z : normalized.w;
                }
                curves[component].keys = componentKeys;
            }
            return originYaw;
        }

        private static void NormalizeRootHorizontalPath(
            AnimationCurve x,
            AnimationCurve y,
            AnimationCurve z,
            Vector3 origin,
            Quaternion originYaw)
        {
            AnimationCurve sourceX = new AnimationCurve(x.keys);
            AnimationCurve sourceY = new AnimationCurve(y.keys);
            AnimationCurve sourceZ = new AnimationCurve(z.keys);
            ApplyHorizontalPathCurve(x, sourceX, sourceY, sourceZ, origin, originYaw, writeX: true);
            ApplyHorizontalPathCurve(z, sourceX, sourceY, sourceZ, origin, originYaw, writeX: false);
        }

        private static void ApplyHorizontalPathCurve(
            AnimationCurve target,
            AnimationCurve sourceX,
            AnimationCurve sourceY,
            AnimationCurve sourceZ,
            Vector3 origin,
            Quaternion originYaw,
            bool writeX)
        {
            Keyframe[] keys = target.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                Vector3 position = new Vector3(
                    sourceX.Evaluate(keys[i].time),
                    sourceY.Evaluate(keys[i].time),
                    sourceZ.Evaluate(keys[i].time));
                Vector3 normalized = KimodoBridge.KimodoMotionMath.NormalizePlanarPosition(
                    position,
                    origin,
                    originYaw);
                keys[i].value = writeX ? normalized.x : normalized.z;
            }
            target.keys = keys;
        }

        private static int CountPathDepth(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int depth = 1;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/') depth++;
            }
            return depth;
        }

        private static void OffsetCurve(AnimationCurve curve, float offset)
        {
            if (curve == null || Mathf.Approximately(offset, 0f)) return;
            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++) keys[i].value -= offset;
            curve.keys = keys;
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
