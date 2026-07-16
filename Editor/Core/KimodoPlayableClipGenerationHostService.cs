using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
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
            CancellationToken token)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(clip.bridgeModelName);
            bool isArdy = KimodoMotionModelProfiles.TryGetArdy(
                resolvedModelName,
                out KimodoMotionModelProfile ardyProfile);
            float durationSeconds = Mathf.Clamp(
                clip.generationFrames,
                KimodoPlayableClip.MIN_FRAMES,
                KimodoPlayableClip.MAX_FRAMES) / KimodoPlayableClip.FIXED_FRAME_RATE;
            if (isArdy)
            {
                TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
                if (timelineClip == null || timelineClip.duration <= 0.0)
                {
                    throw new InvalidOperationException("ARDY duration requires a Timeline clip with positive duration.");
                }
                durationSeconds = (float)timelineClip.duration;
            }

            string constraintsJson;
            bool normalizeConstraintOriginApplied = false;
            KimodoConstraintNormalizationAnchorKind normalizationAnchorKind = KimodoConstraintNormalizationAnchorKind.None;
            KimodoMarkerSampleResult normalizationAnchorSample = null;
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
                int constraintFrames = isArdy
                    ? KimodoInOutConstraintAdapter.DurationSecondsToFrameCount(durationSeconds)
                    : clip.generationFrames;
                KimodoInOutConstraintResult constraintResult = ConstraintProvider.BuildConstraintDataOrThrow(
                    clip,
                    constraintFrames);
                constraintsJson = constraintResult.ConstraintsJson ?? string.Empty;
                AppendSamples(constraintResult.CombinedSamples, constraintSamples);
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
                    durationSeconds,
                    ardyProfile.SourceFps);
                }
                ResolveArdyInitialHistory(clip, ardyProfile, out initialHistorySource);
            }
            int effectiveSeed = ResolveEffectiveSeed(clip);
            return new KimodoEditorGenerateRequest
            {
                Prompt = prompt,
                ModelName = resolvedModelName,
                BridgeVramMode = clip.bridgeVramMode,
                DurationSeconds = durationSeconds,
                DiffusionSteps = isArdy
                    ? Mathf.Clamp(clip.diffusionSteps, 0, ardyProfile.MaxDiffusionSteps)
                    : Mathf.Clamp(clip.diffusionSteps, 1, 1000),
                EffectiveSeed = effectiveSeed,
                ConstraintsJson = constraintsJson,
                CreateTargetClip = () => CreateTimelineTargetClip(clip),
                ResolveOutputPlan = (generatedClip, modelName) => ResolveTimelineOutputPlan(
                    clip,
                    generatedClip,
                    externalConstraint?.RetargetAvatar,
                    modelName),
                ModelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim() ?? string.Empty,
                GenerationTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                Token = token,
                NormalizeConstraintOriginApplied = normalizeConstraintOriginApplied,
                NormalizationAnchorKind = normalizationAnchorKind,
                NormalizationAnchorSample = normalizationAnchorSample,
                ConstraintSamples = constraintSamples,
                InitialArdyHistorySource = initialHistorySource
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

            int undoGroup = BeginReplaceTimelineAnimationUndo(clip, out TimelineClip timelineClip);
            try
            {
                clip.clip = result.GeneratedClip;
                ApplyGeneratedMetadata(clip, result.Prompt, result.MotionJsonCompact);
                clip.ardyMotionCachePath = result.ArdyMotionCachePath ?? string.Empty;
                clip.ardyClipHandles = result.ArdyClipHandles ?? new List<string>();
                clip.ardyMotionRepFingerprint = result.ArdyMotionRepFingerprint ?? string.Empty;
                clip.ardyResolvedSeeds = result.ArdyResolvedSeeds ?? new List<int>();
                EditorUtility.SetDirty(clip);
                EditorUtility.SetDirty(result.GeneratedClip);
                result.ConstraintsPath = string.IsNullOrWhiteSpace(request.ConstraintsJson) ? "(none)" : "(inline-json)";
                HandleGeneratedClipWritebackCompleted(clip, request, timelineClip);

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
            KimodoEditorGenerateRequest request,
            TimelineClip timelineClip)
        {
            KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
            ApplyTimelineOffsets(playableClip, request, timelineClip);
        }

        private static void ApplyTimelineOffsets(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request,
            TimelineClip timelineClip)
        {
            if (playableClip == null)
            {
                return;
            }

            if (request != null && request.NormalizeConstraintOriginApplied)
            {
                Debug.Log(
                    $"[Kimodo][TimelineOffset] normalizationApplied=true clip='{playableClip.name}' " +
                    $"anchorKind={request.NormalizationAnchorKind} anchorType='{request.NormalizationAnchorSample?.constraintType ?? string.Empty}'");
                switch (request.NormalizationAnchorKind)
                {
                    case KimodoConstraintNormalizationAnchorKind.FullBody:
                        if (TryApplyAnchorOffsetToClip(playableClip, request.NormalizationAnchorSample, planarOnly: true))
                        {
                            return;
                        }
                        break;
                    case KimodoConstraintNormalizationAnchorKind.Root2D:
                    case KimodoConstraintNormalizationAnchorKind.Foot:
                        if (TryApplyAnchorOffsetToClip(playableClip, request.NormalizationAnchorSample, planarOnly: true))
                        {
                            return;
                        }
                        break;
                }
            }
            else
            {
                Debug.Log($"[Kimodo][TimelineOffset] normalizationApplied=false clip='{playableClip.name}'.");
            }

            if (KimodoMotionModelProfiles.TryGetArdy(request?.ModelName, out _) &&
                playableClip.inOutConstraintMode != KimodoInOutConstraintMode.None)
            {
                Debug.Log($"[Kimodo][TimelineOffset] skipped Match Offsets to Previous for ARDY InOutConstraint on '{playableClip.name}'.");
                return;
            }

            TryMatchOffsetsToPreviousClip(playableClip, timelineClip);
        }

        private static void TryMatchOffsetsToPreviousClip(KimodoPlayableClip playableClip, TimelineClip timelineClip)
        {
            if (playableClip == null || playableClip.inOutConstraintMode != KimodoInOutConstraintMode.Outside)
            {
                return;
            }

            if (TimelineEditor.inspectedDirector == null)
            {
                Debug.LogWarning($"[Kimodo][TimelineOffset] skipped for '{playableClip.name}': Timeline inspected director is null.");
                return;
            }

            if (timelineClip == null)
            {
                Debug.LogWarning($"[Kimodo][TimelineOffset] skipped for '{playableClip.name}': timeline clip not found.");
                return;
            }

            if (!KimodoInOutConstraintAdapter.HasPreviousNeighbor(timelineClip))
            {
                Debug.LogWarning($"[Kimodo][TimelineOffset] skipped for '{playableClip.name}': no previous neighbor clip.");
                return;
            }

            if (!KimodoTimelinePreviewRefreshUtility.TimelineMatchClipsToPrevious(timelineClip,out string error))
            {
                throw new InvalidOperationException(
                    $"Match Offsets to Previous Clip failed for '{playableClip.name}': {error}");
            }

            Debug.Log($"[Kimodo][TimelineOffset] matched previous offsets for '{playableClip.name}'.");
            EditorUtility.SetDirty(playableClip);
            if (timelineClip.GetParentTrack() != null)
            {
                EditorUtility.SetDirty(timelineClip.GetParentTrack());
            }

            if (TimelineEditor.inspectedAsset != null)
            {
                EditorUtility.SetDirty(TimelineEditor.inspectedAsset);
            }
        }

        private static bool TryApplyAnchorOffsetToClip(
            KimodoPlayableClip playableClip,
            KimodoMarkerSampleResult anchorSample,
            bool planarOnly)
        {
            if (playableClip == null || anchorSample == null)
            {
                return false;
            }

            Vector3 targetPosition = anchorSample.unityRootPos;
            Vector3 targetEulerAngles = anchorSample.unityRootRot.eulerAngles;
            if (planarOnly)
            {
                targetPosition.y = GetSerializedVector3(playableClip, "m_Position").y;
                targetEulerAngles = new Vector3(0f, ResolveHeadingYawDegrees(anchorSample), 0f);
            }

            var serializedObject = new SerializedObject(playableClip);
            SerializedProperty positionProperty = serializedObject.FindProperty("m_Position");
            SerializedProperty eulerAnglesProperty = serializedObject.FindProperty("m_EulerAngles");
            SerializedProperty rotationProperty = serializedObject.FindProperty("m_Rotation");
            if (positionProperty == null || eulerAnglesProperty == null)
            {
                return false;
            }

            positionProperty.vector3Value = targetPosition;
            eulerAnglesProperty.vector3Value = targetEulerAngles;
            if (rotationProperty != null)
            {
                rotationProperty.quaternionValue = Quaternion.Euler(targetEulerAngles);
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[Kimodo][TimelineOffset] applied normalized anchor offset for '{playableClip.name}' using {anchorSample.constraintType}.");
            EditorUtility.SetDirty(playableClip);
            return true;
        }

        private static Vector3 GetSerializedVector3(KimodoPlayableClip playableClip, string propertyPath)
        {
            if (playableClip == null || string.IsNullOrWhiteSpace(propertyPath))
            {
                return Vector3.zero;
            }

            var serializedObject = new SerializedObject(playableClip);
            SerializedProperty property = serializedObject.FindProperty(propertyPath);
            return property != null ? property.vector3Value : Vector3.zero;
        }

        private static float ResolveHeadingYawDegrees(KimodoMarkerSampleResult anchorSample)
        {
            if (anchorSample != null && anchorSample.hasRootHeading)
            {
                Vector2 heading = anchorSample.rootHeading;
                if (heading.sqrMagnitude > 1e-8f)
                {
                    return Mathf.Atan2(heading.x, heading.y) * Mathf.Rad2Deg;
                }
            }

            return anchorSample != null ? anchorSample.unityRootRot.eulerAngles.y : 0f;
        }

        private static int BeginReplaceTimelineAnimationUndo(KimodoPlayableClip playableClip, out TimelineClip timelineClip)
        {
            timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(playableClip);

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
                $"Kimodo_Playable_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        }

        private static KimodoEditorGenerateOutputPlan ResolveTimelineOutputPlan(
            KimodoPlayableClip clip,
            AnimationClip generatedClip,
            Avatar explicitRetargetAvatar,
            string modelName)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("Playable clip is null.");
            }

            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            Avatar originRetargetAvatar = ResolveOriginRetargetAvatar(resolvedModelName);
            Avatar targetRetargetAvatar = ResolveTargetRetargetAvatar(clip, explicitRetargetAvatar, out bool hasBindingAvatar);
            bool hasValidRetargetAvatar =
                KimodoRetargetCoreUtility.IsValidHumanoid(originRetargetAvatar) &&
                hasBindingAvatar &&
                KimodoRetargetCoreUtility.IsValidHumanoid(targetRetargetAvatar);
            GameObject bindingObject = ConstraintProvider.FindTimelineBindingObjectForAsset(clip);
            bool canSkipRetarget =
                bindingObject != null &&
                KimodoEditorClipUtility.CanApplyClipDirectlyToProfileSkeleton(generatedClip, bindingObject, resolvedModelName, out _);

            return new KimodoEditorGenerateOutputPlan
            {
                OriginRetargetAvatar = originRetargetAvatar,
                TargetRetargetAvatar = targetRetargetAvatar,
                ExportMuscleClip = hasValidRetargetAvatar && TryResolveBindingAnimatorAvatar(clip, out _),
                CurveFilterOptions = clip.curveFilterOptions,
                SkipRetarget = canSkipRetarget
            };
        }

        private static Avatar ResolveTargetRetargetAvatar(KimodoPlayableClip clip, Avatar explicitRetargetAvatar, out bool hasBindingAvatar)
        {
            hasBindingAvatar = false;
            if (explicitRetargetAvatar != null && explicitRetargetAvatar.isValid && explicitRetargetAvatar.isHuman)
            {
                hasBindingAvatar = true;
                return explicitRetargetAvatar;
            }

            GameObject bindingObject = ConstraintProvider.FindTimelineBindingObjectForAsset(clip);
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

        private static bool TryResolveBindingAnimatorAvatar(KimodoPlayableClip clip, out Avatar avatar)
        {
            avatar = null;
            GameObject bindingObject = ConstraintProvider.FindTimelineBindingObjectForAsset(clip);
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
            if (clip.inOutConstraintMode != KimodoInOutConstraintMode.Outside)
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
