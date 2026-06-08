using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
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

            AnimationClip previousClip = clip.clip;
            AnimationClip targetClip = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                $"Kimodo_Playable_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

            string resolvedModelName = string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
            Avatar originRetargetAvatar = ResolveOriginRetargetAvatar(resolvedModelName);
            Avatar targetRetargetAvatar = ResolveTargetRetargetAvatar(clip, externalConstraint?.RetargetAvatar, out bool hasBindingAvatar);
            bool hasValidRetargetAvatar =
                KimodoRetargetCoreUtility.IsValidHumanoid(originRetargetAvatar) &&
                hasBindingAvatar &&
                KimodoRetargetCoreUtility.IsValidHumanoid(targetRetargetAvatar);

            GameObject bindingObject = ConstraintProvider.FindTimelineBindingObjectForAsset(clip);
            bool exportMuscleClip = hasValidRetargetAvatar && TryResolveBindingAnimatorAvatar(clip, out _);
            int effectiveSeed = ResolveEffectiveSeed(clip);
            return new KimodoEditorGenerateRequest
            {
                Prompt = prompt,
                ModelName = resolvedModelName,
                GenerationBackend = clip.generationBackend,
                BridgeVramMode = clip.bridgeVramMode,
                DurationSeconds = Mathf.Clamp(clip.generationFrames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES) / KimodoPlayableClip.FIXED_FRAME_RATE,
                DiffusionSteps = Mathf.Clamp(clip.diffusionSteps, 1, 1000),
                EffectiveSeed = effectiveSeed,
                ConstraintsJson = constraintsJson,
                OriginRetargetAvatar = originRetargetAvatar,
                TargetRetargetAvatar = targetRetargetAvatar,
                ExportMuscleClip = exportMuscleClip,
                CurveFilterOptions = clip.curveFilterOptions,
                CanSkipRetarget = generatedClip =>
                    bindingObject != null &&
                    KimodoEditorClipUtility.CanApplyClipDirectlyToProfileSkeleton(generatedClip, bindingObject, resolvedModelName, out _),
                ModelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim() ?? string.Empty,
                ComfyHost = clip.comfyuiIP,
                ComfyPort = clip.comfyuiPort,
                GenerationTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                TargetClip = targetClip,
                PreviousClip = previousClip,
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

            clip.clip = result.GeneratedClip;
            ApplyGeneratedMetadata(clip, result.Prompt, result.MotionJsonCompact);
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(result.GeneratedClip);
            result.ConstraintsPath = string.IsNullOrWhiteSpace(request.ConstraintsJson) ? "(none)" : "(inline-json)";
            HandleGeneratedClipWritebackCompleted(clip);
        }

        public static void CleanupFailedGeneration(KimodoEditorGenerateRequest request)
        {
            if (request == null || request.TargetClip == null)
            {
                return;
            }

            if (ReferenceEquals(request.TargetClip, request.PreviousClip))
            {
                return;
            }

            KimodoEditorClipWritebackService.TryDeleteGeneratedAnimationClipAsset(request.TargetClip);
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

        private static void HandleGeneratedClipWritebackCompleted(KimodoPlayableClip playableClip)
        {
            KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
            TryMatchOffsetsToPreviousClip(playableClip);
        }

        private static void TryMatchOffsetsToPreviousClip(KimodoPlayableClip playableClip)
        {
            if (playableClip == null ||
                !playableClip.enableInbetweenInterpolation ||
                TimelineEditor.inspectedDirector == null)
            {
                return;
            }

            if (!TryResolveBindingAnimatorAvatar(playableClip, out Avatar bindingAvatar) || !KimodoRetargetCoreUtility.IsValidHumanoid(bindingAvatar))
            {
                return;
            }

            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(playableClip);
            if (timelineClip == null || FindPreviousClip(timelineClip) == null)
            {
                return;
            }

            if (!TryInvokeTimelineMatchClipsToPrevious(timelineClip, out string error))
            {
                throw new InvalidOperationException(
                    $"Match Offsets to Previous Clip failed for '{playableClip.name}': {error}");
            }

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

        private static TimelineClip FindPreviousClip(TimelineClip clip)
        {
            if (clip == null)
            {
                return null;
            }

            TrackAsset parentTrack = clip.GetParentTrack();
            if (parentTrack == null)
            {
                return null;
            }

            TimelineClip previousClip = null;
            foreach (TimelineClip candidate in parentTrack.GetClips())
            {
                if (candidate == null || candidate == clip || candidate.start >= clip.start)
                {
                    continue;
                }

                if (previousClip == null || candidate.start >= previousClip.start)
                {
                    previousClip = candidate;
                }
            }

            return previousClip;
        }

        private static bool TryInvokeTimelineMatchClipsToPrevious(TimelineClip clip, out string error)
        {
            error = string.Empty;
            Type animationOffsetMenuType = typeof(TimelineEditor).Assembly.GetType("UnityEditor.Timeline.AnimationOffsetMenu");
            if (animationOffsetMenuType == null)
            {
                error = "UnityEditor.Timeline.AnimationOffsetMenu not found.";
                return false;
            }

            MethodInfo matchMethod = animationOffsetMenuType.GetMethod(
                "MatchClipsToPrevious",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (matchMethod == null)
            {
                error = "MatchClipsToPrevious method not found.";
                return false;
            }

            try
            {
                matchMethod.Invoke(null, new object[] { new[] { clip } });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                error = inner.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static Avatar ResolveOriginRetargetAvatar(string modelName)
        {
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar avatar, out _))
            {
                return null;
            }

            return KimodoRetargetCoreUtility.IsValidHumanoid(avatar) ? avatar : null;
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
