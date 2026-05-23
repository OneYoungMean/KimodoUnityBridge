using System;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Bridge;

namespace KimodoUnityMotionTools.Generation
{
    internal sealed class BridgeBackendAdapter : IGenerationBackendAdapter
    {
        private readonly KimodoBridgeService bridgeService;

        public BridgeBackendAdapter(BridgeRuntimeSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            bridgeService = new KimodoBridgeService(settings);
        }

        public Task<string> StartAsync(Action<string> progress, CancellationToken token)
        {
            return bridgeService.StartAsync(progress, token);
        }

        public async Task<KimodoGenerationResultDto> GenerateAsync(KimodoGenerationRequestDto request, Action<string> progress, CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string motionJson = await bridgeService.GenerateAsync(
                request.prompt,
                request.duration,
                request.seed,
                request.steps,
                request.constraints_json,
                progress,
                token);

            return new KimodoGenerationResultDto
            {
                backendType = KimodoBackendType.Bridge,
                rawStatus = "done",
                message = "Bridge generation complete.",
                motionJsonCompact = motionJson
            };
        }

        public Task<bool> PingAsync(CancellationToken token)
        {
            return bridgeService.PingAsync(token, acceptLoading: true);
        }

        public Task DetachAsync(CancellationToken token)
        {
            return bridgeService.DetachAsync(token);
        }

        public Task StopAsync(CancellationToken token)
        {
            return bridgeService.StopAsync(token);
        }

        public Task KillAsync(CancellationToken token)
        {
            return bridgeService.KillAsync(token);
        }

        public void Dispose()
        {
            bridgeService?.Dispose();
        }
    }
}
