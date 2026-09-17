using System.Text.Json;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Infrastructure.Collection;

public sealed class CollectionSnapshotStore
{
    private readonly string _rootDirectory;

    public CollectionSnapshotStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoMagic",
            "collections");
    }

    public async Task<string> SaveAsync(SearchResultPayload result, CancellationToken cancellationToken = default)
    {
        var collectionId = $"COL-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var directory = Path.Combine(_rootDirectory, collectionId);
        var detailsDirectory = Path.Combine(directory, "details");
        Directory.CreateDirectory(detailsDirectory);
        var options = BridgeJson.IndentedOptions;
        await WriteJsonAsync(Path.Combine(directory, "collection.json"), result, options, cancellationToken);
        await WriteJsonAsync(Path.Combine(directory, "list.json"), result.Items, options, cancellationToken);
        if (result.DetailResults is not null)
        {
            for (var index = 0; index < result.DetailResults.Count; index++)
            {
                await WriteJsonAsync(Path.Combine(detailsDirectory, $"{index + 1:000}.json"), result.DetailResults[index], options, cancellationToken);
            }
        }
        return directory;
    }

    public async Task<StoredCollectionSnapshot?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(_rootDirectory)
            .EnumerateDirectories("COL-*")
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate.FullName, "collection.json")));
        if (directory is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(Path.Combine(directory.FullName, "collection.json"));
        var result = await JsonSerializer.DeserializeAsync<SearchResultPayload>(
            stream,
            BridgeJson.Options,
            cancellationToken);
        return result is null ? null : new StoredCollectionSnapshot(directory.FullName, result);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
    }
}

public sealed record StoredCollectionSnapshot(string Directory, SearchResultPayload Result);
