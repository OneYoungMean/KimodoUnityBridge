using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public interface IKimodoGeneratePipeline
    {
        Task<KimodoBridgeCommandResult> ExecuteAsync(
            KimodoBridgeCommandRequest request,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token);
    }
}
