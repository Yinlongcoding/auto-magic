using System.Text.Json;
using AutoMagic.Application.Ozon.Rules;

namespace AutoMagic.Infrastructure.Ozon.Rules;

/// <summary>
/// Reads %LOCALAPPDATA%/AutoMagic/dictionary-cache/{categoryId}/{typeId}/{dictionaryId}.json,
/// shaped like the Ozon attribute values response: { "result": [ { "id": 123, "value": "..." } ] }.
/// </summary>
public sealed class FileDictionaryValueCache(string root) : IDictionaryValueCache
{
    private readonly Dictionary<string, HashSet<long>?> _loaded = [];

    public async Task<bool?> ContainsAsync(long categoryId, long typeId, long dictionaryId, long valueId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, categoryId.ToString(), typeId.ToString(), $"{dictionaryId}.json");
        if (!_loaded.TryGetValue(path, out var ids)) _loaded[path] = ids = await LoadAsync(path, cancellationToken);
        return ids?.Contains(valueId);
    }

    private static async Task<HashSet<long>?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"字典缓存 {path} 缺少 result 数组。");
        return result.EnumerateArray().Select(v => v.GetProperty("id").GetInt64()).ToHashSet();
    }
}
