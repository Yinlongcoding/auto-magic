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
        var schema = JsonSerializer.Deserialize<OzonCategorySchema>(File.ReadAllText(path), Json);
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
