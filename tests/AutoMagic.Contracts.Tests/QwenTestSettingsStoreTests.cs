using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Infrastructure.Ozon;
using AutoMagic.Infrastructure.Ozon.Mapping;

namespace AutoMagic.Contracts.Tests;

public sealed class QwenTestSettingsStoreTests
{
    [Fact]
    public async Task SaveLoadAndClear_UseOnlyCredentialStore()
    {
        var credentials = new MemoryCredentialStore();
        using var store = new QwenTestSettingsStore(credentials);

        await store.SaveAsync(new QwenTestSettings("secret-qwen-key"), CancellationToken.None);
        var restored = await store.LoadAsync(CancellationToken.None);
        await store.ClearCredentialsAsync(CancellationToken.None);
        var cleared = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("secret-qwen-key", restored.ApiKey);
        Assert.Empty(cleared.ApiKey);
        Assert.Equal(
            QwenTestSettingsStore.ApiKeyCredentialTarget,
            credentials.LastWrittenTarget);
    }

    private sealed class MemoryCredentialStore : IWindowsCredentialStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? LastWrittenTarget { get; private set; }

        public string? Read(string targetName) =>
            _values.TryGetValue(targetName, out var value) ? value : null;

        public void Write(string targetName, string secret)
        {
            LastWrittenTarget = targetName;
            if (secret.Length == 0)
            {
                _values.Remove(targetName);
            }
            else
            {
                _values[targetName] = secret;
            }
        }

        public void Delete(string targetName) => _values.Remove(targetName);
    }
}
