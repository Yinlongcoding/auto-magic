using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AM.Desktop.Services;

/// <summary>一本字典的缓存。Full 为 true 时 Values 是整本；为 false 时太大没有整本拉，匹配要走搜索接口（08 号问题 6）。</summary>
public sealed record CachedDictionary(long AttributeId, long DictionaryId, bool Full, IReadOnlyList<OzonDictionaryValue> Values);

/// <summary>一个类型的属性和字典缓存。</summary>
public sealed record TypeSchema(
    long DescriptionCategoryId,
    long TypeId,
    DateTime FetchedAt,
    IReadOnlyList<OzonAttributeInfo> Attributes,
    IReadOnlyList<CachedDictionary> Dictionaries)
{
    public CachedDictionary? DictionaryFor(long attributeId) => Dictionaries.FirstOrDefault(d => d.AttributeId == attributeId);
}

/// <summary>
/// 类型属性和字典的本地缓存，每个类型一个文件：%LocalAppData%\AM\schemas\{类目}-{类型}.json。
/// 只给必填属性和变体属性拉字典，可选属性第一阶段留空（12 号）。
/// 字典按（类目、类型、属性）各存一份：实测颜色字典在三个类型下完全相同，尺码字典随类型不同（2026-10-23，类型 93211/93216/93232）。
/// 匹配阶段只读这里，不联网（08 号问题 6）。
/// </summary>
public static class TypeSchemaCache
{
    /// <summary>超过这么多条就不整本拉。实测品牌字典远超此数，颜色 651 条。</summary>
    private const int FullDictionaryLimit = 5000;

    private static readonly string CacheDirectory = Path.Combine(SettingsStore.DataDirectory, "schemas");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文原样写出，方便直接打开查看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读本地缓存，没有则返回 null。</summary>
    public static TypeSchema? Load(long descriptionCategoryId, long typeId)
    {
        var path = PathFor(descriptionCategoryId, typeId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TypeSchema>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // 文件坏了按没有缓存处理，下次拉取覆盖
            return null;
        }
    }

    /// <summary>有缓存就用缓存，没有才拉。</summary>
    public static async Task<TypeSchema> GetAsync(OzonSettings settings, long descriptionCategoryId, long typeId,
        CancellationToken cancellationToken = default) =>
        Load(descriptionCategoryId, typeId) ?? await RefreshAsync(settings, descriptionCategoryId, typeId, cancellationToken);

    /// <summary>从 Ozon 重新拉取并替换缓存。失败抛 OzonApiException，旧缓存不动。</summary>
    public static async Task<TypeSchema> RefreshAsync(OzonSettings settings, long descriptionCategoryId, long typeId,
        CancellationToken cancellationToken = default)
    {
        var attributes = await OzonClient.GetAttributesAsync(settings, descriptionCategoryId, typeId, cancellationToken);
        if (attributes.Count == 0)
        {
            throw new OzonApiException("Ozon 返回的属性列表是空的，已保留旧缓存。");
        }

        var dictionaries = new List<CachedDictionary>();
        foreach (var attribute in attributes.Where(a => a.DictionaryId > 0 && (a.IsRequired || a.IsAspect)))
        {
            dictionaries.Add(await FetchDictionaryAsync(settings, descriptionCategoryId, typeId, attribute, cancellationToken));
        }

        var schema = new TypeSchema(descriptionCategoryId, typeId, DateTime.Now, attributes, dictionaries);

        Directory.CreateDirectory(CacheDirectory);
        var path = PathFor(descriptionCategoryId, typeId);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(schema, JsonOptions), cancellationToken);
        File.Move(temp, path, overwrite: true);
        return schema;
    }

    private static async Task<CachedDictionary> FetchDictionaryAsync(OzonSettings settings, long descriptionCategoryId,
        long typeId, OzonAttributeInfo attribute, CancellationToken cancellationToken)
    {
        var values = new List<OzonDictionaryValue>();
        long lastValueId = 0;
        while (true)
        {
            var (page, hasNext) = await OzonClient.GetDictionaryPageAsync(
                settings, descriptionCategoryId, typeId, attribute.Id, lastValueId, cancellationToken);
            values.AddRange(page);
            if (!hasNext)
            {
                break;
            }

            // 大字典只记"按需"，不留半本，免得把没拉到的值当成不存在
            if (values.Count >= FullDictionaryLimit)
            {
                return new CachedDictionary(attribute.Id, attribute.DictionaryId, Full: false, []);
            }

            var next = page.Count > 0 ? page.Max(v => v.Id) : lastValueId;
            if (next <= lastValueId)
            {
                throw new OzonApiException($"Ozon 字典分页没有前进（属性 {attribute.Id}），已停止读取。");
            }

            lastValueId = next;
        }

        return new CachedDictionary(attribute.Id, attribute.DictionaryId, Full: true,
            values.DistinctBy(v => v.Id).ToList());
    }

    private static string PathFor(long descriptionCategoryId, long typeId) =>
        Path.Combine(CacheDirectory, $"{descriptionCategoryId}-{typeId}.json");
}
