using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public sealed class KimodoBridgeCommand : IKimodoGeneratePipeline
    {
        public async Task<KimodoBridgeCommandResult> ExecuteAsync(
            KimodoBridgeCommandRequest request,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            progress?.Invoke(KimodoBridgeCommandStage.Validate, "Validating generation request...");

            if (request.RuntimeSettings == null)
            {
                throw new InvalidOperationException("Runtime settings are required.");
            }

            if (request.GenerationRequest == null)
            {
                throw new InvalidOperationException("Generation request is required.");
            }

            token.ThrowIfCancellationRequested();
            KimodoGenerationResultDto result = await ExecuteBridgeAsync(request, progress, token);

            if (result == null)
            {
                throw new InvalidOperationException("Runtime generation returned null result.");
            }

            if (string.IsNullOrWhiteSpace(result.motionJsonCompact))
            {
                throw new InvalidOperationException(result.message ?? "No motion json found in runtime generation result.");
            }

            progress?.Invoke(KimodoBridgeCommandStage.Completed, "Generation backend completed.");

            return new KimodoBridgeCommandResult
            {
                MotionJsonCompact = result.motionJsonCompact,
                Message = result.message ?? string.Empty,
                RawStatus = result.rawStatus ?? string.Empty
            };
        }

        private static async Task<KimodoGenerationResultDto> ExecuteBridgeAsync(
            KimodoBridgeCommandRequest request,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            if (request.RuntimeSettings.bridgeSettings == null)
            {
                throw new InvalidOperationException("Bridge runtime settings are required.");
            }

            progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Starting generation backend...");

            using var bridgeService = new KimodoBridgeService(request.RuntimeSettings.bridgeSettings);
            _ = await bridgeService.StartAsync(
                message => progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, message ?? string.Empty),
                token);

            token.ThrowIfCancellationRequested();
            progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Invoking generation backend...");

            string motionJson = await bridgeService.GenerateAsync(
                request.GenerationRequest,
                message => progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, message ?? string.Empty),
                token);

            return new KimodoGenerationResultDto
            {
                rawStatus = "done",
                message = "Bridge generation complete.",
                motionJsonCompact = motionJson
            };
        }
    }

    public sealed class KimodoBridgeCommandResult
    {
        public string MotionJsonCompact;
        public string Message;
        public string RawStatus;
    }

    public enum KimodoBridgeCommandStage
    {
        None = 0,
        Validate = 1,
        Constraint = 2,
        InvokeBackend = 3,
        AssetWrite = 4,
        Bake = 5,
        Retarget = 6,
        Finalize = 7,
        Completed = 8
    }

    public sealed class KimodoBridgeCommandRequest
    {
        public KimodoRuntimeGenerationSettings RuntimeSettings;
        public KimodoGenerationRequestDto GenerationRequest;
    }
}
