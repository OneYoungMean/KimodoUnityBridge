using System;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Bridge;

namespace KimodoUnityMotionTools.Generation
{
    public sealed class KimodoRuntimeGenerationService : IDisposable
    {
        private readonly KimodoRuntimeGenerationSettings settings;
        private readonly IGenerationBackendAdapter bridgeAdapter;
        private readonly IGenerationBackendAdapter comfyAdapter;
        private bool disposed;

        public KimodoRuntimeGenerationService(KimodoRuntimeGenerationSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (this.settings.bridgeSettings == null)
            {
                throw new ArgumentException("bridgeSettings is required.", nameof(settings));
            }

            bridgeAdapter = new BridgeBackendAdapter(this.settings.bridgeSettings);
            comfyAdapter = new ComfyUiBackendAdapter(
                this.settings.comfyHost,
                this.settings.comfyPort,
                this.settings.comfyTimeoutSeconds,
                this.settings.comfyPollIntervalSeconds,
                this.settings.comfyWorkflowResourceName);
        }

        public Task<string> StartAsync(KimodoBackendType backendType, Action<string> progress, CancellationToken token)
        {
            ThrowIfDisposed();
            return GetAdapter(backendType).StartAsync(progress, token);
        }

        public Task<KimodoGenerationResultDto> GenerateAsync(
            KimodoGenerationRequestDto request,
            KimodoBackendType backendType,
            Action<string> progress,
            CancellationToken token)
        {
            ThrowIfDisposed();
            return GetAdapter(backendType).GenerateAsync(request, progress, token);
        }

        public Task<bool> PingAsync(KimodoBackendType backendType, CancellationToken token)
        {
            ThrowIfDisposed();
            return GetAdapter(backendType).PingAsync(token);
        }

        public Task DetachAsync(KimodoBackendType backendType, CancellationToken token)
        {
            ThrowIfDisposed();
            return GetAdapter(backendType).DetachAsync(token);
        }

        public Task StopAsync(KimodoBackendType backendType, CancellationToken token)
        {
            ThrowIfDisposed();
            return GetAdapter(backendType).StopAsync(token);
        }

        public Task KillAsync(KimodoBackendType backendType, CancellationToken token)
        {
            ThrowIfDisposed();
            return GetAdapter(backendType).KillAsync(token);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try { bridgeAdapter?.Dispose(); } catch { }
            try { comfyAdapter?.Dispose(); } catch { }
        }

        private IGenerationBackendAdapter GetAdapter(KimodoBackendType backendType)
        {
            return backendType == KimodoBackendType.Bridge ? bridgeAdapter : comfyAdapter;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(KimodoRuntimeGenerationService));
            }
        }
    }
}
