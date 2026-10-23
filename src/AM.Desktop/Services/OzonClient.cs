using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AM.Desktop.Services;

/// <summary>Ozon Seller API 调用失败，Message 可直接显示给用户，不含密钥。</summary>
public sealed class OzonApiException(string message) : Exception(message);

/// <summary>类型的一个属性（v1/description-category/attribute），只留规则和阻断判断用得到的字段。</summary>
public sealed record OzonAttributeInfo(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("is_collection")] bool IsCollection,
    [property: JsonPropertyName("is_required")] bool IsRequired,
    [property: JsonPropertyName("is_aspect")] bool IsAspect,
    [property: JsonPropertyName("dictionary_id")] long DictionaryId,
    [property: JsonPropertyName("max_value_count")] int MaxValueCount,
    [property: JsonPropertyName("group_name")] string? GroupName,
    [property: JsonPropertyName("attribute_complex_id")] long AttributeComplexId);

/// <summary>字典里的一个值。</summary>
public sealed record OzonDictionaryValue(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("info")] string? Info,
    [property: JsonPropertyName("picture")] string? Picture);

/// <summary>
/// Ozon Seller API。目前只有只读接口：类目树、类型属性、字典值。测试连接用类目树，因为它需要有效凭据。
/// </summary>
public static class OzonClient
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        // 类目树响应约 2 MB。
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>拉取类目树原始 JSON（language=ZH_HANS）。</summary>
    public static async Task<byte[]> GetCategoryTreeAsync(OzonSettings settings, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(settings, "v1/description-category/tree", new { language = "ZH_HANS" }, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>类型的全部属性，名称为中文。</summary>
    public static async Task<IReadOnlyList<OzonAttributeInfo>> GetAttributesAsync(
        OzonSettings settings, long descriptionCategoryId, long typeId, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(settings, "v1/description-category/attribute", new
        {
            description_category_id = descriptionCategoryId,
            type_id = typeId,
            language = "ZH_HANS",
        }, cancellationToken);
        var payload = await ReadAsync<ResultResponse<OzonAttributeInfo>>(response, cancellationToken);
        return payload.Result ?? throw new OzonApiException("Ozon 返回的属性列表缺少 result。");
    }

    /// <summary>
    /// 字典值的一页，按 value_id 递增。lastValueId 传上一页最后一个 id，第一页传 0。
    /// 每页最多 2000 条（第三方 SDK 的说法，官方文档未核实），这里用 1000 与旧仓库一致。
    /// </summary>
    public static async Task<(IReadOnlyList<OzonDictionaryValue> Values, bool HasNext)> GetDictionaryPageAsync(
        OzonSettings settings, long descriptionCategoryId, long typeId, long attributeId, long lastValueId,
        CancellationToken cancellationToken)
    {
        using var response = await PostAsync(settings, "v1/description-category/attribute/values", new
        {
            attribute_id = attributeId,
            description_category_id = descriptionCategoryId,
            type_id = typeId,
            language = "ZH_HANS",
            last_value_id = lastValueId,
            limit = DictionaryPageSize,
        }, cancellationToken);
        var payload = await ReadAsync<ResultResponse<OzonDictionaryValue>>(response, cancellationToken);
        return (payload.Result ?? throw new OzonApiException("Ozon 返回的字典缺少 result。"), payload.HasNext);
    }

    public const int DictionaryPageSize = 1000;

    private static async Task<HttpResponseMessage> PostAsync(OzonSettings settings, string path, object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Client-Id", settings.ClientId);
        request.Headers.TryAddWithoutValidation("Api-Key", settings.ApiKey);
        request.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new OzonApiException($"连不上 Ozon：{error.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OzonApiException("Ozon 请求超时。");
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw new OzonApiException(await DescribeFailureAsync(response, settings, cancellationToken));
            }
        }

        return response;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new OzonApiException("Ozon 返回了空响应。");
        }
        catch (JsonException)
        {
            throw new OzonApiException("Ozon 返回的不是有效 JSON。");
        }
    }

    private sealed record ResultResponse<T>(
        [property: JsonPropertyName("result")] IReadOnlyList<T>? Result,
        [property: JsonPropertyName("has_next")] bool HasNext);

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, OzonSettings settings, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "Ozon 拒绝了凭据，请检查 Client-Id 和 API Key。";
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "Ozon 请求过于频繁，请稍后再试。";
        }

        // 响应体只截取一段，并去掉可能回显的凭据。
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim()
            .Replace(settings.ApiKey, "***", StringComparison.Ordinal)
            .Replace(settings.ClientId, "***", StringComparison.Ordinal);
        body = body.Length <= 300 ? body : body[..300] + "…";
        return $"Ozon 请求失败：HTTP {(int)response.StatusCode}。{body}";
    }
}
