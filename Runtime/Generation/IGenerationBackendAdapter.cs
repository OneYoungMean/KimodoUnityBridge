using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoUnityMotionTools.Generation
{
    internal interface IGenerationBackendAdapter : IDisposable
    {
        Task<string> StartAsync(Action<string> progress, CancellationToken token);
        Task<KimodoGenerationResultDto> GenerateAsync(KimodoGenerationRequestDto request, Action<string> progress, CancellationToken token);
        Task<bool> PingAsync(CancellationToken token);
        Task DetachAsync(CancellationToken token);
        Task StopAsync(CancellationToken token);
        Task KillAsync(CancellationToken token);
    }
}
