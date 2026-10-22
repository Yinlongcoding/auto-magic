using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AM.Desktop.Services;

public sealed record OzonType(long TypeId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>类目树里直接挂着类型的那一级类目。Path 是从根到它的中文名。</summary>
public sealed record OzonCategory(long DescriptionCategoryId, string Path, IReadOnlyList<OzonType> Types)
{
    public override string ToString() => Path;
}

/// <summary>
/// Ozon 类目树缓存，文件在 %LocalAppData%\AM\category-tree.json，内容是接口原始响应。
/// 更新时先解析校验，再写临时文件替换；失败保留旧缓存。没有缓存就是空，不回退到任何打包文件。
/// </summary>
public static class CategoryCache
{
    private static readonly string FilePath = Path.Combine(SettingsStore.DataDirectory, "category-tree.json");
    private static readonly StringComparer Chinese = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), false);

    private static bool _loaded;
    private static IReadOnlyList<OzonCategory> _categories = [];
    private static DateTime? _updatedAt;

    public static event Action? Changed;

    public static IReadOnlyList<OzonCategory> Categories
    {
        get { EnsureLoaded(); return _categories; }
    }

    /// <summary>上次成功更新的时间（本地时间）。没有缓存为 null。</summary>
    public static DateTime? UpdatedAt
    {
        get { EnsureLoaded(); return _updatedAt; }
    }

    public static int TypeCount => Categories.Sum(c => c.Types.Count);

    /// <summary>从 Ozon 拉取并替换缓存。失败抛 OzonApiException，旧缓存不动。</summary>
    public static async Task RefreshAsync(OzonSettings settings, CancellationToken cancellationToken = default)
    {
        var raw = await OzonClient.GetCategoryTreeAsync(settings, cancellationToken);
        var categories = await Task.Run(() => Parse(raw), cancellationToken);
        if (categories.Count == 0)
        {
            throw new OzonApiException("Ozon 返回的类目树是空的，已保留旧缓存。");
        }

        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var temp = FilePath + ".tmp";
        await File.WriteAllBytesAsync(temp, raw, cancellationToken);
        File.Move(temp, FilePath, overwrite: true);

        _categories = categories;
        _updatedAt = File.GetLastWriteTime(FilePath);
        _loaded = true;
        Changed?.Invoke();
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            _categories = Parse(File.ReadAllBytes(FilePath));
            _updatedAt = File.GetLastWriteTime(FilePath);
        }
        catch (OzonApiException)
        {
            // 缓存文件坏了按没有缓存处理，下次更新覆盖。
        }
    }

    private static List<OzonCategory> Parse(byte[] raw)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            throw new OzonApiException("Ozon 返回的类目树不是有效 JSON。");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                throw new OzonApiException("Ozon 返回的类目树缺少 result。");
            }

            var categories = new List<OzonCategory>();
            foreach (var node in result.EnumerateArray())
            {
                Walk(node, [], categories);
            }

            categories.Sort((a, b) => Chinese.Compare(a.Path, b.Path));
            return categories;
        }
    }

    /// <summary>类目节点有 description_category_id，类型节点有 type_id。层级深度不假定。</summary>
    private static void Walk(JsonElement node, List<string> path, List<OzonCategory> output)
    {
        if (IsDisabled(node) || !node.TryGetProperty("description_category_id", out var idElement))
        {
            return;
        }

        path = [.. path, node.TryGetProperty("category_name", out var name) ? name.GetString() ?? "" : ""];
        var types = new List<OzonType>();
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (child.TryGetProperty("type_id", out var typeId))
                {
                    if (!IsDisabled(child) && typeId.GetInt64() > 0)
                    {
                        types.Add(new OzonType(typeId.GetInt64(), child.GetProperty("type_name").GetString() ?? ""));
                    }
                }
                else
                {
                    Walk(child, path, output);
                }
            }
        }

        if (types.Count > 0)
        {
            types.Sort((a, b) => Chinese.Compare(a.Name, b.Name));
            output.Add(new OzonCategory(idElement.GetInt64(), string.Join(" / ", path), types));
        }
    }

    private static bool IsDisabled(JsonElement node) =>
        node.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True;
}
