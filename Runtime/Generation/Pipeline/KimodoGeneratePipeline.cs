using System;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Generation;

namespace KimodoUnityMotionTools.Generation.Pipeline
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

            progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Starting generation backend...");

            using var runtimeService = new KimodoRuntimeGenerationService(request.RuntimeSettings);
            _ = await runtimeService.StartAsync(
                request.BackendType,
                message => progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, message ?? string.Empty),
                token);

            token.ThrowIfCancellationRequested();

            progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, "Invoking generation backend...");

            KimodoGenerationResultDto result = await runtimeService.GenerateAsync(
                request.GenerationRequest,
                request.BackendType,
                message => progress?.Invoke(KimodoGeneratePipelineStage.InvokeBackend, message ?? string.Empty),
                token);

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
    }
}
