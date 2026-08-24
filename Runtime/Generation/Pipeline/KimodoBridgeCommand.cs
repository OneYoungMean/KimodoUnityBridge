using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public sealed class KimodoBridgeCommand
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

            bool emptyKmbResult = string.Equals(result.motionFormat, "kmb_v1", StringComparison.OrdinalIgnoreCase) &&
                result.motionBytes != null && result.motionBytes.Length == 0;
            if (string.IsNullOrWhiteSpace(result.motionJsonCompact) && !emptyKmbResult)
            {
                throw new InvalidOperationException(result.message ?? "No motion json found in runtime generation result.");
            }

            progress?.Invoke(KimodoBridgeCommandStage.Completed, "Generation backend completed.");

            return KimodoBridgeCommandResult.From(result);
        }

        private static async Task<KimodoGenerationResultDto> ExecuteBridgeAsync(
            KimodoBridgeCommandRequest request,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Invoking generation backend...");

            return await KimodoBridgeService.Shared.GenerateAsync(
                request.GenerationRequest,
                message => progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, message ?? string.Empty),
                token);
        }
    }

    public sealed class KimodoBridgeCommandResult : KimodoGenerationResultDto
    {
        internal static KimodoBridgeCommandResult From(KimodoGenerationResultDto result)
        {
            if (result == null)
            {
                return null;
            }

            return new KimodoBridgeCommandResult
            {
                motionJsonCompact = result.motionJsonCompact,
                motionData = result.motionData,
                motionBytes = result.motionBytes,
                kmbAttachments = result.kmbAttachments,
                motionFormat = result.motionFormat,
                rawStatus = result.rawStatus ?? "done",
                message = result.message ?? "Bridge generation complete.",
                motionRepFingerprint = result.motionRepFingerprint ?? string.Empty,
                resolvedSeed = result.resolvedSeed,
                startFrame = result.startFrame,
                endFrameExclusive = result.endFrameExclusive,
                ardyPlaybackReserveSeconds = result.ardyPlaybackReserveSeconds,
                analysisJson = result.analysisJson
            };
        }
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
