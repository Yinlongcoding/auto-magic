using System.Text.Json;
using AutoMagic.Application.Ozon;

namespace AutoMagic.Infrastructure.Ozon;

/// <summary>Development cache for Chinese category schemas. Contains no credentials.</summary>
public sealed class OzonSchemaCache(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string FilePath(long category, long type)
    {
        if (category <= 0 || type <= 0) throw new ArgumentOutOfRangeException(nameof(category));
        return Path.Combine(root, category.ToString(), type + ".zh-Hans.json");
    }

    public OzonCategorySchema? Load(long category, long type)
    {
        var path = FilePath(category, type);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        // Caches written before is_aspect was parsed would silently read every attribute as non-variant.
        using (var document = JsonDocument.Parse(text))
        {
            if (!document.RootElement.TryGetProperty("attributes", out var raw) || raw.ValueKind != JsonValueKind.Array ||
                raw.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("isAspect", out _)))
                throw new InvalidDataException("Schema 缓存缺少 is_aspect 字段（旧版本缓存），请手动刷新。");
        }
        var schema = JsonSerializer.Deserialize<OzonCategorySchema>(text, Json);
        if (schema is null || schema.DescriptionCategoryId != category || schema.TypeId != type ||
            schema.Attributes is null || schema.Attributes.Count == 0 ||
            schema.Attributes.Any(a => a is null || a.Id <= 0 || string.IsNullOrWhiteSpace(a.Name)) ||
            schema.Attributes.Select(a => a.Id).Distinct().Count() != schema.Attributes.Count)
            throw new InvalidDataException("Schema 缓存内容无效或品类/类型不一致，请手动刷新。");
        return schema;
    }

    public void Save(OzonCategorySchema schema)
    {
        if (schema.Attributes.Count == 0) throw new InvalidDataException("不保存空 Schema。");
        var path = FilePath(schema.DescriptionCategoryId, schema.TypeId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(schema, Json));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
