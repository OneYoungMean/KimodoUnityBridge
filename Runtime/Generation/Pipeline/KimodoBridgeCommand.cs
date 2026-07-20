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

            if (string.IsNullOrWhiteSpace(result.motionJsonCompact) && result.motionData != null)
            {
                result.motionJsonCompact = KimodoRawMotionUtility.ToCompactJson(result.motionData);
            }

            bool hasStreamHandle = result.handleOperator?.Info?.IsStream == true &&
                !string.IsNullOrWhiteSpace(result.clipHandle);
            if (string.IsNullOrWhiteSpace(result.motionJsonCompact) && !hasStreamHandle)
            {
                throw new InvalidOperationException(result.message ?? "No motion json found in runtime generation result.");
            }

            progress?.Invoke(KimodoBridgeCommandStage.Completed, "Generation backend completed.");

            return new KimodoBridgeCommandResult
            {
                MotionJsonCompact = result.motionJsonCompact,
                MotionData = result.motionData,
                MotionFormat = result.motionFormat,
                Message = result.message ?? string.Empty,
                RawStatus = result.rawStatus ?? string.Empty,
                MotionBytes = result.motionBytes,
                ClipHandle = result.clipHandle ?? string.Empty,
                MotionRepFingerprint = result.motionRepFingerprint ?? string.Empty,
                ResolvedSeed = result.resolvedSeed,
                HandleOperator = result.handleOperator
            };
        }

        private static async Task<KimodoGenerationResultDto> ExecuteBridgeAsync(
            KimodoBridgeCommandRequest request,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Invoking generation backend...");

            KimodoBridgeGenerationResult bridgeResult = await KimodoBridgeService.Shared.GenerateAsync(
                request.GenerationRequest,
                message => progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, message ?? string.Empty),
                token);

            return new KimodoGenerationResultDto
            {
                rawStatus = bridgeResult?.RawStatus ?? "done",
                message = bridgeResult?.Message ?? "Bridge generation complete.",
                motionJsonCompact = bridgeResult?.MotionJsonCompact,
                motionData = bridgeResult?.MotionData,
                motionBytes = bridgeResult?.MotionBytes,
                motionFormat = bridgeResult?.MotionFormat,
                clipHandle = bridgeResult?.ClipHandle,
                motionRepFingerprint = bridgeResult?.MotionRepFingerprint,
                resolvedSeed = bridgeResult?.ResolvedSeed,
                handleOperator = bridgeResult?.HandleOperator
            };
        }
    }

    public sealed class KimodoBridgeCommandResult
    {
        public string MotionJsonCompact;
        public KimodoRawMotionData MotionData;
        public string MotionFormat;
        public string Message;
        public string RawStatus;
        public byte[] MotionBytes;
        public string ClipHandle;
        public string MotionRepFingerprint;
        public int? ResolvedSeed;
        public AnimationHandleOperator HandleOperator;
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
        public KimodoGenerationRequestDto GenerationRequest;
    }
}
