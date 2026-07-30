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
        private static readonly HumanBodyBones[] FirstFrameDiagnosticBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightHand,
            HumanBodyBones.Head
        };

        public static KimodoEditorGenerateRequest BuildRequest(
            KimodoPlayableClip clip,
            string prompt,
            KimodoExternalConstraintRequest externalConstraint,
            CancellationToken token,
            bool? normalizeConstraintOriginOverride = null,
            int? effectiveSeedOverride = null,
            bool disableTimelineInOut = false)
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
                    normalizeConstraintOriginOverride,
                    disableTimelineInOut);
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
                    targetLengthSeconds,
                    ardyProfile.SourceFps);
                }
                if (!disableTimelineInOut)
                {
                    ResolveArdyInitialHistory(
                        clip,
                        ardyProfile,
                        normalizeConstraintOriginApplied,
                        normalizationAnchorSample,
                        out initialHistorySource);
                }
            }
            int effectiveSeed = effectiveSeedOverride ?? ResolveEffectiveSeed(clip);
            if (effectiveSeedOverride.HasValue && clip.seed != effectiveSeed)
            {
                clip.seed = effectiveSeed;
                EditorUtility.SetDirty(clip);
            }
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

            int undoGroup = BeginReplaceTimelineAnimationUndo(clip, out TimelineClip timelineClip);
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

            if (request != null && request.DisableTimelineInOut)
            {
                ResetClipOffset(playableClip, removeStartOffset: false);
                Debug.Log($"[Kimodo][TimelineOffset] using continuous ARDY stream root motion for '{playableClip.name}'.");
                return;
            }

            if (request != null && (request.ConstraintSamples == null || request.ConstraintSamples.Count == 0))
            {
                ResetClipOffset(playableClip, removeStartOffset: true);
                Debug.Log($"[Kimodo][TimelineOffset] no constraints; using Timeline track/scene offset as the anchor for '{playableClip.name}'.");
                return;
            }

            if (request != null && request.NormalizeConstraintOriginApplied)
            {
                Debug.Log(
                    $"[Kimodo][TimelineOffset] normalizationApplied=true clip='{playableClip.name}' " +
                    $"anchorKind={request.NormalizationAnchorKind} anchorType='{request.NormalizationAnchorSample?.constraintType ?? string.Empty}'");
                if (!TryMatchNormalizedAnchorOnTargetAvatar(
                        playableClip,
                        request,
                        timelineClip,
                        out string error))
                {
                    throw new InvalidOperationException(
                        $"Match normalized anchor on target Avatar failed for '{playableClip.name}': {error}");
                }
                return;
            }
            else
            {
                Debug.Log($"[Kimodo][TimelineOffset] normalizationApplied=false clip='{playableClip.name}'.");
            }

            TryMatchOffsetsToPreviousClip(playableClip, timelineClip);
        }

        private static bool TryMatchNormalizedAnchorOnTargetAvatar(
            KimodoPlayableClip playableClip,
            KimodoEditorGenerateRequest request,
            TimelineClip timelineClip,
            out string error)
        {
            error = string.Empty;
            if (playableClip == null || request?.NormalizationAnchorSample == null || timelineClip == null)
            {
                error = "Playable clip, normalized anchor sample, or Timeline clip is missing.";
                return false;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForPlayableClip(
                    playableClip,
                    out PoseCacheRenderContext context,
                    out _,
                    out error))
            {
                return false;
            }

            var targetBonePositions = new Vector3[FirstFrameDiagnosticBones.Length];
            var targetBoneRotations = new Quaternion[FirstFrameDiagnosticBones.Length];
            var targetBoneValid = new bool[FirstFrameDiagnosticBones.Length];
            Vector3 targetRootPosition = Vector3.zero;
            Quaternion targetRootRotation = Quaternion.identity;
            bool targetPoseCaptured = false;
            Action<Animator, Transform> captureTargetPose = (animator, root) =>
            {
                if (animator == null || root == null)
                {
                    return;
                }

                targetRootPosition = root.position;
                targetRootRotation = root.rotation;
                for (int i = 0; i < FirstFrameDiagnosticBones.Length; i++)
                {
                    Transform bone = animator.GetBoneTransform(FirstFrameDiagnosticBones[i]);
                    targetBoneValid[i] = bone != null;
                    if (bone != null)
                    {
                        targetBonePositions[i] = bone.position;
                        targetBoneRotations[i] = bone.rotation;
                    }
                }
                targetPoseCaptured = true;
            };

            if (!KimodoConstraintPoseCache.TryResolveTargetHipsPose(
                    context,
                    request.NormalizationAnchorSample,
                    out Vector3 targetHipsPosition,
                    out Quaternion targetHipsRotation,
                    out error,
                    captureTargetPose))
            {
                return false;
            }

            ResetClipOffset(playableClip, removeStartOffset: false);
            bool hasFullPose = request.NormalizationAnchorSample.localAxisAngles != null &&
                request.NormalizationAnchorSample.localAxisAngles.Count > 0;
            bool planarOnly = request.NormalizationAnchorKind == KimodoConstraintNormalizationAnchorKind.Root2D ||
                !hasFullPose;
            Action<Animator, Transform> logFirstFrameComparison = (animator, root) =>
            {
                if (!targetPoseCaptured || animator == null || root == null)
                {
                    Debug.LogWarning(
                        $"[Kimodo][TimelineFirstFrameConstraintDiag] clip='{playableClip.name}' pose data is unavailable.");
                    return;
                }

                var boneDiagnostics = new List<string>(FirstFrameDiagnosticBones.Length);
                float maxPositionError = 0f;
                float maxPlanarError = 0f;
                HumanBodyBones maxPositionBone = HumanBodyBones.LastBone;
                for (int i = 0; i < FirstFrameDiagnosticBones.Length; i++)
                {
                    HumanBodyBones boneId = FirstFrameDiagnosticBones[i];
                    Transform actualBone = animator.GetBoneTransform(boneId);
                    if (!targetBoneValid[i] || actualBone == null)
                    {
                        boneDiagnostics.Add($"{boneId}[missing]");
                        continue;
                    }

                    float positionError = Vector3.Distance(targetBonePositions[i], actualBone.position);
                    float planarError = Vector2.Distance(
                        new Vector2(targetBonePositions[i].x, targetBonePositions[i].z),
                        new Vector2(actualBone.position.x, actualBone.position.z));
                    if (positionError > maxPositionError)
                    {
                        maxPositionError = positionError;
                        maxPositionBone = boneId;
                    }
                    maxPlanarError = Mathf.Max(maxPlanarError, planarError);
                    boneDiagnostics.Add(
                        $"{boneId}[targetPos={targetBonePositions[i]:F6},actualPos={actualBone.position:F6}," +
                        $"posError={positionError:F6},xzError={planarError:F6}," +
                        $"rotErrorDeg={Quaternion.Angle(targetBoneRotations[i], actualBone.rotation):F6}]");
                }

                string boneSummary = string.Join(";", boneDiagnostics);
                Debug.Log(
                    $"[Kimodo][TimelineFirstFrameConstraintDiag] clip='{playableClip.name}' " +
                    $"sampleTime={timelineClip.start + 0.00001d:F6} anchorKind={request.NormalizationAnchorKind} " +
                    $"rootTargetPos={targetRootPosition:F6} rootActualPos={root.position:F6} " +
                    $"rootPosError={Vector3.Distance(targetRootPosition, root.position):F6} " +
                    $"rootRotErrorDeg={Quaternion.Angle(targetRootRotation, root.rotation):F6} " +
                    $"maxBonePosError={maxPositionError:F6} maxBone={maxPositionBone} " +
                    $"maxBoneXZError={maxPlanarError:F6} bones={boneSummary}");
            };
            if (!KimodoTimelinePreviewRefreshUtility.TimelineMatchClipToWorldHips(
                    timelineClip,
                    targetHipsPosition,
                    targetHipsRotation,
                    planarOnly,
                    out error,
                    logFirstFrameComparison))
            {
                return false;
            }

            Debug.Log(
                $"[Kimodo][TimelineOffset] matched normalized anchor on target Avatar Hips for '{playableClip.name}'.");
            EditorUtility.SetDirty(playableClip);
            if (timelineClip.GetParentTrack() != null)
            {
                EditorUtility.SetDirty(timelineClip.GetParentTrack());
            }
            if (TimelineEditor.inspectedAsset != null)
            {
                EditorUtility.SetDirty(TimelineEditor.inspectedAsset);
            }
            return true;
        }

        private static void TryMatchOffsetsToPreviousClip(KimodoPlayableClip playableClip, TimelineClip timelineClip)
        {
            if (playableClip == null ||
                playableClip.inOutConstraintMode != KimodoInOutConstraintMode.Outside ||
                !playableClip.enableInConstraint)
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

        private static void ResetClipOffset(KimodoPlayableClip playableClip, bool removeStartOffset)
        {
            playableClip.removeStartOffset = removeStartOffset;
            var serializedObject = new SerializedObject(playableClip);
            SerializedProperty positionProperty = serializedObject.FindProperty("m_Position");
            SerializedProperty eulerAnglesProperty = serializedObject.FindProperty("m_EulerAngles");
            SerializedProperty rotationProperty = serializedObject.FindProperty("m_Rotation");
            if (positionProperty != null)
            {
                positionProperty.vector3Value = Vector3.zero;
            }
            if (eulerAnglesProperty != null)
            {
                eulerAnglesProperty.vector3Value = Vector3.zero;
            }
            if (rotationProperty != null)
            {
                rotationProperty.quaternionValue = Quaternion.identity;
            }
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(playableClip);
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
            bool normalizeRootToAnchor,
            KimodoMarkerSampleResult normalizationAnchorSample,
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
                RangeEndSeconds = Math.Max(0.0, timelineClip.start),
                NormalizeRootToAnchor = normalizeRootToAnchor && normalizationAnchorSample != null,
                AnchorRootPosition = normalizationAnchorSample != null
                    ? new Vector3(normalizationAnchorSample.unityRootPos.x, 0f, normalizationAnchorSample.unityRootPos.z)
                    : Vector3.zero,
                AnchorRootRotation = KimodoConstraintNormalizationUtility.ResolvePlanarRootRotation(
                    normalizationAnchorSample)
            };
        }

    }
}
