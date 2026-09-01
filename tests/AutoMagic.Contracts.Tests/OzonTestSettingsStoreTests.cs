using AutoMagic.Application.Ozon;
using AutoMagic.Infrastructure.Ozon;

namespace AutoMagic.Contracts.Tests;

public sealed class OzonTestSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_KeepApiKeyOutOfTheJsonFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AutoMagic.OzonTestSettings.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        var credentials = new MemoryCredentialStore();
        using var store = new OzonTestSettingsStore(path, credentials);

        try
        {
            await store.SaveAsync(
                new OzonTestSettings("client-1", "secret-api-key", 10, 20),
                CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("client-1", json, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-api-key", json, StringComparison.Ordinal);

            var restored = await store.LoadAsync(CancellationToken.None);
            Assert.Equal("client-1", restored.ClientId);
            Assert.Equal("secret-api-key", restored.ApiKey);
            Assert.Equal(10, restored.DescriptionCategoryId);
            Assert.Equal(20, restored.TypeId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ClearCredentials_RemovesSecretsButPreservesSelections()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AutoMagic.OzonTestSettings.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        var credentials = new MemoryCredentialStore();
        using var store = new OzonTestSettingsStore(path, credentials);

        try
        {
            await store.SaveAsync(
                new OzonTestSettings("client-1", "secret-api-key", 10, 20),
                CancellationToken.None);
            await store.ClearCredentialsAsync(CancellationToken.None);

            var restored = await store.LoadAsync(CancellationToken.None);
            Assert.Empty(restored.ClientId);
            Assert.Empty(restored.ApiKey);
            Assert.Equal(10, restored.DescriptionCategoryId);
            Assert.Equal(20, restored.TypeId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class MemoryCredentialStore : IWindowsCredentialStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Read(string targetName) =>
            _values.TryGetValue(targetName, out var value) ? value : null;

        public void Write(string targetName, string secret)
        {
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
