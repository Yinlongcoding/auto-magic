using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace AutoMagic.Application.Ozon.Mapping;

public sealed record QwenApiCredentials(string ApiKey)
{
    public QwenApiCredentials Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("请输入百炼 DashScope API Key。", nameof(ApiKey));
        }

        if (ApiKey.Length > 512)
        {
            throw new ArgumentException("DashScope API Key 长度无效。", nameof(ApiKey));
        }

        return this with { ApiKey = ApiKey.Trim() };
    }
}

public sealed record QwenTokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

public static class QwenMappingRuntime
{
    public const string Provider = "Alibaba Bailian / DashScope";
    public const string RegionId = "cn-beijing";
    public const string RegionDisplayName = "华北2（北京）";
    public const string ModelId = "qwen3.7-plus-2026-05-26";
    public const string SharedBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/";
}

public static class ProductMappingJson
{
    public static JsonSerializerOptions StrictOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static JsonSerializerOptions IndentedOptions { get; } = new(StrictOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}
