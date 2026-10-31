using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public interface IKimodoGenerationTransport
    {
        Task<KimodoGenerationResultDto> SendGenerateAsync(
            KimodoGenerationRequestDto request,
            Action<string> progress,
            CancellationToken token);
    }

    public sealed class KimodoGenerationTransportFactory
    {
        private readonly Func<IKimodoGenerationTransport> createTransport;

        public KimodoGenerationTransportFactory(Func<IKimodoGenerationTransport> createTransport)
        {
            this.createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
        }

        public IKimodoGenerationTransport Create()
        {
            return createTransport() ?? throw new InvalidOperationException("Generation transport factory returned null.");
        }

        public static KimodoGenerationTransportFactory CreateDefault()
        {
            return new KimodoGenerationTransportFactory(() => KimodoBridgeService.Shared);
        }
    }

    public sealed class KimodoBridgeCommand
    {
        private readonly IKimodoGenerationTransport transport;

        public KimodoBridgeCommand()
            : this(KimodoGenerationTransportFactory.CreateDefault().Create())
        {
        }

        public KimodoBridgeCommand(IKimodoGenerationTransport transport)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        public async Task<KimodoGenerationResultDto> ExecuteAsync(
            KimodoGenerationRequestDto request,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            progress?.Invoke(KimodoBridgeCommandStage.Validate, "Validating generation request...");
            progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, "Invoking generation backend...");
            token.ThrowIfCancellationRequested();
            KimodoGenerationResultDto result = await transport.SendGenerateAsync(
                request,
                message => progress?.Invoke(KimodoBridgeCommandStage.InvokeBackend, message ?? string.Empty),
                token);
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
            return result;
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
}
