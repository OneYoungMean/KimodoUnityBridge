using System;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Generation;
using UnityEngine;

namespace KimodoUnityMotionTools.Ai
{
    public static class KimodoAiApi
    {
        private const float TargetFps = 30f;

        public static bool ValidateRequest(KimodoGenerationRequestDto req, out string error)
        {
            error = string.Empty;
            if (req == null)
            {
                error = "Request is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(req.prompt))
            {
                error = "Prompt is empty.";
                return false;
            }

            if (req.duration <= 0f)
            {
                error = "Duration must be > 0.";
                return false;
            }

            if (req.steps <= 0)
            {
                error = "Steps must be > 0.";
                return false;
            }

            req.constraints_json ??= string.Empty;
            return true;
        }

        public static KimodoGenerationRequestDto CreateDefaultRequest(string prompt, int frames = 150, int steps = 100, int? seed = null)
        {
            int clampedFrames = Math.Max(1, frames);
            return new KimodoGenerationRequestDto
            {
                prompt = prompt ?? string.Empty,
                duration = clampedFrames / TargetFps,
                seed = seed,
                steps = Math.Max(1, steps),
                constraints_json = string.Empty,
                boundary_pose_json = string.Empty,
                loop_hint = false,
                segment_index = 0,
                transition_duration = 0f
            };
        }

        public static async Task<KimodoGenerationResultDto> GenerateAsync(
            KimodoRuntimeGenerationSettings settings,
            KimodoBackendType backend,
            KimodoGenerationRequestDto req,
            CancellationToken token = default)
        {
            if (!ValidateRequest(req, out string error))
            {
                throw new ArgumentException(error, nameof(req));
            }

            using var service = new KimodoRuntimeGenerationService(settings ?? throw new ArgumentNullException(nameof(settings)));
            _ = await service.StartAsync(backend, null, token);
            return await service.GenerateAsync(req, backend, null, token);
        }

        public static async Task<bool> PingAsync(
            KimodoRuntimeGenerationSettings settings,
            KimodoBackendType backend,
            CancellationToken token = default)
        {
            using var service = new KimodoRuntimeGenerationService(settings ?? throw new ArgumentNullException(nameof(settings)));
            return await service.PingAsync(backend, token);
        }

        public static async Task StopAsync(
            KimodoRuntimeGenerationSettings settings,
            KimodoBackendType backend,
            CancellationToken token = default)
        {
            using var service = new KimodoRuntimeGenerationService(settings ?? throw new ArgumentNullException(nameof(settings)));
            await service.StopAsync(backend, token);
        }

        public static async Task KillAsync(
            KimodoRuntimeGenerationSettings settings,
            KimodoBackendType backend,
            CancellationToken token = default)
        {
            using var service = new KimodoRuntimeGenerationService(settings ?? throw new ArgumentNullException(nameof(settings)));
            await service.KillAsync(backend, token);
        }

        public static bool BakeMotionJsonToClip(AnimationClip clip, string motionJson, out string error)
        {
            return KimodoRuntimeClipBaker.TryBake(clip, motionJson, out error);
        }

        public static async Task<(KimodoGenerationResultDto result, bool baked, string bakeError)> GenerateAndBakeAsync(
            KimodoRuntimeGenerationSettings settings,
            KimodoBackendType backend,
            KimodoGenerationRequestDto req,
            AnimationClip clip,
            CancellationToken token = default)
        {
            KimodoGenerationResultDto result = await GenerateAsync(settings, backend, req, token);
            string bakeError = string.Empty;
            bool baked = false;
            if (result != null && !string.IsNullOrWhiteSpace(result.motionJsonCompact) && clip != null)
            {
                baked = BakeMotionJsonToClip(clip, result.motionJsonCompact, out bakeError);
            }
            else if (clip == null)
            {
                bakeError = "Target clip is null.";
            }
            else
            {
                bakeError = "Generation result motion json is empty.";
            }

            return (result, baked, bakeError);
        }
    }
}
