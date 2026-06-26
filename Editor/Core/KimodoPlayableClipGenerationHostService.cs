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

            string constraintsJson;
            if (externalConstraint != null && externalConstraint.Enabled)
            {
                constraintsJson = externalConstraint.ConstraintsJson ?? string.Empty;
            }
            else
            {
                constraintsJson = ConstraintProvider.BuildConstraintsJsonOrThrow(clip);
            }

            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(clip.bridgeModelName);
            int effectiveSeed = ResolveEffectiveSeed(clip);
            return new KimodoEditorGenerateRequest
            {
                Prompt = prompt,
                ModelName = resolvedModelName,
                BridgeVramMode = clip.bridgeVramMode,
                DurationSeconds = Mathf.Clamp(clip.generationFrames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES) / KimodoPlayableClip.FIXED_FRAME_RATE,
                DiffusionSteps = Mathf.Clamp(clip.diffusionSteps, 1, 1000),
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
                Token = token
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
                EditorUtility.SetDirty(clip);
                EditorUtility.SetDirty(result.GeneratedClip);
                result.ConstraintsPath = string.IsNullOrWhiteSpace(request.ConstraintsJson) ? "(none)" : "(inline-json)";
                HandleGeneratedClipWritebackCompleted(clip, timelineClip);

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
            clip.fps = Mathf.RoundToInt(KimodoPlayableClip.FIXED_FRAME_RATE);
        }

        private static void HandleGeneratedClipWritebackCompleted(KimodoPlayableClip playableClip, TimelineClip timelineClip)
        {
            KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
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

    }
}
