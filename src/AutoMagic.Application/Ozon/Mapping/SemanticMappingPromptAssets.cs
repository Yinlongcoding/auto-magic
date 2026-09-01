using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoMagic.Application.Ozon.Mapping;

public static class SemanticMappingPromptAssets
{
    private const string PromptResourceName = "AutoMagic.AiMapping.qwen-system-prompt.txt";
    private const string SchemaResourceName = "AutoMagic.AiMapping.semantic-mapping-response.schema.json";

    private static readonly Lazy<string> SystemPromptValue = new(() =>
        ReadEmbeddedText(PromptResourceName));

    private static readonly Lazy<JsonElement> ResponseSchemaValue = new(() =>
        CreateQwenResponseSchema(ReadEmbeddedText(SchemaResourceName)));

    private static readonly Lazy<string> SkillVersionValue = new(() =>
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(SystemPrompt));
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    });

    public static string SystemPrompt => SystemPromptValue.Value;

    public static JsonElement ResponseSchema => ResponseSchemaValue.Value;

    public static string SkillVersion => SkillVersionValue.Value;

    private static string ReadEmbeddedText(string resourceName)
    {
        var assembly = typeof(SemanticMappingPromptAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"缺少内嵌AI映射资源：{resourceName}。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var value = reader.ReadToEnd().Trim();
        return value.Length > 0
            ? value
            : throw new InvalidOperationException($"内嵌AI映射资源为空：{resourceName}。");
    }

    private static JsonElement CreateQwenResponseSchema(string sourceJson)
    {
        var root = JsonNode.Parse(sourceJson)?.AsObject()
                   ?? throw new InvalidDataException("AI映射JSON Schema格式无效。");
        var targetMapping = root["$defs"]?["targetMapping"]?.DeepClone()
                            ?? throw new InvalidDataException("AI映射JSON Schema缺少targetMapping定义。");
        var items = root["properties"]?["targetMappings"]?.AsObject()
                    ?? throw new InvalidDataException("AI映射JSON Schema缺少targetMappings定义。");
        items["items"] = targetMapping;
        root.Remove("$schema");
        root.Remove("$id");
        root.Remove("$defs");
        RemoveProviderOptionalConstraints(root);
        return JsonSerializer.SerializeToElement(root, SemanticMappingJson.StrictOptions);
    }

    private static void RemoveProviderOptionalConstraints(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            value.Remove("title");
            value.Remove("minimum");
            value.Remove("maximum");
            value.Remove("minLength");
            value.Remove("uniqueItems");
            foreach (var child in value.ToArray())
            {
                RemoveProviderOptionalConstraints(child.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                RemoveProviderOptionalConstraints(child);
            }
        }
    }
}
