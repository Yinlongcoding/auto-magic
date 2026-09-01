using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMagic.Application.Ozon;

namespace AutoMagic.Infrastructure.Ozon;

public sealed class LocalOzonCategoryCatalog(string catalogPath) : ILocalOzonCategoryCatalog
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private OzonCategoryCatalogSnapshot? _cached;

    public async Task<OzonCategoryCatalogSnapshot> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("本地 Ozon 测试品类目录不存在。", catalogPath);
            }

            await using var stream = new FileStream(
                catalogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var categories = await OzonCategoryCatalogParser.ParseAsync(
                stream,
                cancellationToken);
            _cached = new OzonCategoryCatalogSnapshot(
                Path.GetFileName(catalogPath),
                DateTimeOffset.UtcNow,
                categories);
            return _cached;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}

public static class OzonCategoryCatalogParser
{
    public static async Task<IReadOnlyList<OzonCategoryOption>> ParseAsync(
        Stream json,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        var payload = await JsonSerializer.DeserializeAsync<CategoryTreeResponse>(
            json,
            cancellationToken: cancellationToken);
        if (payload?.Result is null || payload.Result.Count == 0)
        {
            throw new InvalidDataException("本地 Ozon 品类目录缺少 result 数据。");
        }

        var categories = new Dictionary<long, CategoryAccumulator>();
        foreach (var root in payload.Result)
        {
            Traverse(root, [], null, categories);
        }

        var result = categories.Values
            .Where(category => category.Types.Count > 0)
            .Select(category => new OzonCategoryOption(
                category.Id,
                category.Name,
                string.Join(" > ", category.Path),
                category.Types.Values
                    .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(type => type.TypeId)
                    .ToArray()))
            .OrderBy(category => category.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (result.Length == 0)
        {
            throw new InvalidDataException("本地 Ozon 品类目录没有可选择的商品类型。");
        }

        return result;
    }

    private static void Traverse(
        CategoryTreeNode node,
        IReadOnlyList<string> parentPath,
        CategoryAccumulator? currentCategory,
        IDictionary<long, CategoryAccumulator> categories)
    {
        if (node.Disabled)
        {
            return;
        }

        var category = currentCategory;
        var path = parentPath;
        if (node.DescriptionCategoryId is > 0 && !string.IsNullOrWhiteSpace(node.CategoryName))
        {
            path = [.. parentPath, node.CategoryName.Trim()];
            if (!categories.TryGetValue(node.DescriptionCategoryId.Value, out category))
            {
                category = new CategoryAccumulator(
                    node.DescriptionCategoryId.Value,
                    node.CategoryName.Trim(),
                    path);
                categories.Add(category.Id, category);
            }
        }

        if (node.TypeId is > 0 &&
            !string.IsNullOrWhiteSpace(node.TypeName) &&
            category is not null)
        {
            var typeName = node.TypeName.Trim();
            category.Types.TryAdd(
                node.TypeId.Value,
                new OzonTypeOption(
                    node.TypeId.Value,
                    typeName,
                    $"{typeName} · {node.TypeId.Value}"));
        }

        foreach (var child in node.Children ?? [])
        {
            Traverse(child, path, category, categories);
        }
    }

    private sealed record CategoryAccumulator(
        long Id,
        string Name,
        IReadOnlyList<string> Path)
    {
        public Dictionary<long, OzonTypeOption> Types { get; } = [];
    }

    private sealed record CategoryTreeResponse(
        [property: JsonPropertyName("result")] IReadOnlyList<CategoryTreeNode>? Result);

    private sealed record CategoryTreeNode(
        [property: JsonPropertyName("description_category_id")] long? DescriptionCategoryId,
        [property: JsonPropertyName("category_name")] string? CategoryName,
        [property: JsonPropertyName("type_id")] long? TypeId,
        [property: JsonPropertyName("type_name")] string? TypeName,
        [property: JsonPropertyName("disabled")] bool Disabled,
        [property: JsonPropertyName("children")] IReadOnlyList<CategoryTreeNode>? Children);
}
