using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public sealed class KimodoGeneratePipeline : IKimodoGeneratePipeline
    {
        public async Task<KimodoGeneratePipelineResult> ExecuteAsync(
            KimodoGeneratePipelineRequest request,
            Action<KimodoGeneratePipelineStage, string> progress,
            CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            progress?.Invoke(KimodoGeneratePipelineStage.Validate, "Validating generation request...");

            if (request.RuntimeSettings == null)
            {
                throw new InvalidOperationException("Runtime settings are required.");
            }

            if (request.GenerationRequest == null)
            {
                throw new InvalidOperationException("Generation request is required.");
            }

            token.ThrowIfCancellationRequested();
            KimodoGenerationResultDto result = await ExecuteBackendAsync(request, progress, token);

            if (result == null)
            {
                throw new InvalidOperationException("Runtime generation returned null result.");
            }

            if (string.IsNullOrWhiteSpace(result.motionJsonCompact))
            {
                throw new InvalidOperationException(result.message ?? "No motion json found in runtime generation result.");
            }

            progress?.Invoke(KimodoGeneratePipelineStage.Completed, "Generation backend completed.");

            return new KimodoGeneratePipelineResult
            {
                BackendType = result.backendType,
                MotionJsonCompact = result.motionJsonCompact,
                Message = result.message ?? string.Empty,
                RawStatus = result.rawStatus ?? string.Empty
            };
        }

        private static async Task<KimodoGenerationResultDto> ExecuteBackendAsync(
            KimodoGeneratePipelineRequest request,
            Action<KimodoGeneratePipelineStage, string> progress,
            CancellationToken token)
        {
            switch (request.BackendType)
            {
                case KimodoBackendType.Bridge:
                    return await ExecuteBridgeAsync(request, progress, token);
                case KimodoBackendType.ComfyUi:
                    return await ExecuteComfyUiAsync(request, progress, token);
                default:
                    throw new NotSupportedException($"Unsupported backend type: {request.BackendType}");
            }
        }

        private static async Task<KimodoGenerationResultDto> ExecuteBridgeAsync(
            KimodoGeneratePipelineRequest request,
            Action<KimodoGeneratePipelineStage, string> progress,
            CancellationToken token)
        {
            if (request.RuntimeSettings.bridgeSettings == null)
            {
                throw new InvalidOperationException("Bridge runtime settings are required.");
            }

            progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Starting generation backend...");

            using var bridgeService = new KimodoBridgeService(request.RuntimeSettings.bridgeSettings);
            _ = await bridgeService.StartAsync(
                message => progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, message ?? string.Empty),
                token);

            token.ThrowIfCancellationRequested();
            progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Invoking generation backend...");

            string motionJson = await bridgeService.GenerateAsync(
                request.GenerationRequest,
                message => progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, message ?? string.Empty),
                token);

            return new KimodoGenerationResultDto
            {
                backendType = KimodoBackendType.Bridge,
                rawStatus = "done",
                message = "Bridge generation complete.",
                motionJsonCompact = motionJson
            };
        }

        private static async Task<KimodoGenerationResultDto> ExecuteComfyUiAsync(
            KimodoGeneratePipelineRequest request,
            Action<KimodoGeneratePipelineStage, string> progress,
            CancellationToken token)
        {
            progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Starting generation backend...");

            using var comfyUi = new ComfyUiBackendAdapter(
                request.RuntimeSettings.comfyHost,
                request.RuntimeSettings.comfyPort,
                request.RuntimeSettings.comfyTimeoutSeconds,
                1f,
                request.RuntimeSettings.comfyWorkflowResourceName);
            _ = await comfyUi.StartAsync(
                message => progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, message ?? string.Empty),
                token);

            token.ThrowIfCancellationRequested();
            progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Invoking generation backend...");

            return await comfyUi.GenerateAsync(
                request.GenerationRequest,
                message => progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, message ?? string.Empty),
                token);
        }
    }
}
