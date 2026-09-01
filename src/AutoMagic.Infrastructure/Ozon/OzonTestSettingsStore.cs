using System.Text.Json;
using AutoMagic.Application.Ozon;

namespace AutoMagic.Infrastructure.Ozon;

public sealed class OzonTestSettingsStore : IOzonTestSettingsStore, IDisposable
{
    public const string ApiKeyCredentialTarget = "AutoMagic.Ozon.TestApiKey";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly IWindowsCredentialStore _credentialStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OzonTestSettingsStore(
        string settingsPath,
        IWindowsCredentialStore credentialStore)
    {
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? throw new ArgumentException("设置文件路径不能为空。", nameof(settingsPath))
            : Path.GetFullPath(settingsPath);
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async Task<OzonTestSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var persisted = await ReadFileAsync(cancellationToken);
            var apiKey = _credentialStore.Read(ApiKeyCredentialTarget) ?? string.Empty;
            return new OzonTestSettings(
                persisted.ClientId ?? string.Empty,
                apiKey,
                persisted.DescriptionCategoryId,
                persisted.TypeId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        OzonTestSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _credentialStore.Write(ApiKeyCredentialTarget, settings.ApiKey ?? string.Empty);
            await WriteFileAsync(
                new PersistedSettings(
                    settings.ClientId?.Trim() ?? string.Empty,
                    settings.DescriptionCategoryId,
                    settings.TypeId),
                cancellationToken);
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
            var persisted = await ReadFileAsync(cancellationToken);
            await WriteFileAsync(persisted with { ClientId = string.Empty }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<PersistedSettings> ReadFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new PersistedSettings(string.Empty, null, null);
        }

        await using var stream = new FileStream(
            _settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PersistedSettings>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? new PersistedSettings(string.Empty, null, null);
    }

    private async Task WriteFileAsync(
        PersistedSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
                        ?? throw new InvalidOperationException("设置文件缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record PersistedSettings(
        string? ClientId,
        long? DescriptionCategoryId,
        long? TypeId);
}
