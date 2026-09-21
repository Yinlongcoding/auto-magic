using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoMagic.Application.Ozon.Mapping;

public static class ProductMappingPromptAssets
{
    public static string SystemPrompt { get; } = Read("product-mapping-v2-prompt.txt");
    public static string PromptVersion { get; } = "sha256:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SystemPrompt)));
    public static JsonElement ResponseSchema { get; } = CreateSchema();

    private static string Read(string name)
    {
        using var stream = typeof(ProductMappingPromptAssets).Assembly
            .GetManifestResourceStream($"AutoMagic.AiMapping.{name}")
            ?? throw new InvalidDataException($"缺少 V2 映射资源：{name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    private static JsonElement CreateSchema()
    {
        var root = JsonNode.Parse(Read("product-mapping-response.v2.schema.json"))!.AsObject();
        var mapping = root["$defs"]!["mapping"]!;
        root["properties"]!["productMappings"]!["items"] = mapping.DeepClone();
        root["properties"]!["variants"]!["items"]!["properties"]!["mappings"]!["items"] = mapping.DeepClone();
        root.Remove("$defs");
        return JsonSerializer.SerializeToElement(root);
    }
}
