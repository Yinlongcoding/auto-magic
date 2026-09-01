using AutoMagic.Application.Ozon.Mapping;

namespace AutoMagic.Infrastructure.Ozon.Mapping;

public sealed class QwenTestSettingsStore : IQwenTestSettingsStore, IDisposable
{
    public const string ApiKeyCredentialTarget = "AutoMagic.Qwen.DashScopeApiKey";

    private readonly IWindowsCredentialStore _credentialStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QwenTestSettingsStore(IWindowsCredentialStore credentialStore)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async Task<QwenTestSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new QwenTestSettings(
                _credentialStore.Read(ApiKeyCredentialTarget) ?? string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        QwenTestSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _credentialStore.Write(ApiKeyCredentialTarget, settings.ApiKey ?? string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearCredentialsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _credentialStore.Delete(ApiKeyCredentialTarget);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
