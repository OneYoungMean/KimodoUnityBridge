using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityEngine.Timeline
{
    internal interface IKimodoGenerationBackend
    {
        Task<string> GenerateMotionJsonAsync(KimodoPlayableClipEditor host, string constraintsFilePath, int effectiveSeed, CancellationToken token);
    }

    internal sealed class ComfyUiGenerationBackend : IKimodoGenerationBackend
    {
        public Task<string> GenerateMotionJsonAsync(KimodoPlayableClipEditor host, string constraintsFilePath, int effectiveSeed, CancellationToken token)
        {
            return host.GenerateMotionJsonViaComfyUiBackendAsync(constraintsFilePath, effectiveSeed, token);
        }
    }

    internal sealed class KimodoBridgeGenerationBackend : IKimodoGenerationBackend
    {
        public Task<string> GenerateMotionJsonAsync(KimodoPlayableClipEditor host, string constraintsFilePath, int effectiveSeed, CancellationToken token)
        {
            return host.GenerateMotionJsonViaBridgeBackendAsync(constraintsFilePath, effectiveSeed, token);
        }
    }
}
