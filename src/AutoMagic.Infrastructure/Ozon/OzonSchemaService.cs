using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMagic.Application.Ozon;

namespace AutoMagic.Infrastructure.Ozon;

public sealed class OzonSchemaService(HttpClient httpClient) : IOzonSchemaService
{
    private const string AttributeEndpoint = "v1/description-category/attribute";

    public async Task<OzonCategorySchema> GetCategorySchemaAsync(
        OzonTemporaryCredentials credentials,
        long descriptionCategoryId,
        long typeId,
        CancellationToken cancellationToken)
    {
        credentials = credentials.Validate();
        if (descriptionCategoryId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptionCategoryId),
                "description_category_id 必须是正整数。");
        }

        if (typeId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(typeId), "type_id 必须是正整数。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, AttributeEndpoint);
        request.Headers.TryAddWithoutValidation("Client-Id", credentials.ClientId);
        request.Headers.TryAddWithoutValidation("Api-Key", credentials.ApiKey);
        request.Content = JsonContent.Create(new
        {
            description_category_id = descriptionCategoryId,
            type_id = typeId,
            language = "ZH_HANS",
        });

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await CreateSafeErrorAsync(
                response,
                credentials,
                cancellationToken));
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<OzonAttributesResponse>(
            content,
            cancellationToken: cancellationToken);
        if (payload?.Result is null)
        {
            throw new InvalidOperationException("Ozon 返回的属性 Schema 格式无效。");
        }

        var attributes = payload.Result
            .Where(attribute => attribute.Id > 0 && !string.IsNullOrWhiteSpace(attribute.Name))
            .Select(attribute => new OzonAttributeDefinition(
                attribute.Id,
                attribute.AttributeComplexId,
                attribute.Name.Trim(),
                attribute.Description?.Trim() ?? string.Empty,
                attribute.Type?.Trim() ?? string.Empty,
                attribute.IsCollection,
                attribute.IsRequired,
                attribute.DictionaryId,
                attribute.MaxValueCount,
                attribute.GroupName?.Trim() ?? string.Empty))
            .ToArray();

        return new OzonCategorySchema(
            descriptionCategoryId,
            typeId,
            DateTimeOffset.UtcNow,
            attributes);
    }

    private static async Task<string> CreateSafeErrorAsync(
        HttpResponseMessage response,
        OzonTemporaryCredentials credentials,
        CancellationToken cancellationToken)
    {
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Ozon 拒绝了临时凭证，请检查 Client-Id、Api-Key 及权限。",
            HttpStatusCode.TooManyRequests =>
                "Ozon 请求过于频繁，请稍后重试。",
            _ => $"Ozon Schema 请求失败：HTTP {(int)response.StatusCode}。",
        };

        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (body.Length == 0 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return reason;
        }

        var safeBody = body
            .Replace(credentials.ApiKey, "***", StringComparison.Ordinal)
            .Replace(credentials.ClientId, "***", StringComparison.Ordinal);
        safeBody = safeBody.Length <= 500 ? safeBody : safeBody[..500] + "…";
        return $"{reason} Ozon 返回：{safeBody}";
    }

    private sealed record OzonAttributesResponse(
        [property: JsonPropertyName("result")] IReadOnlyList<OzonAttributeResponse>? Result);

    private sealed record OzonAttributeResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("attribute_complex_id")] long AttributeComplexId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("is_collection")] bool IsCollection,
        [property: JsonPropertyName("is_required")] bool IsRequired,
        [property: JsonPropertyName("dictionary_id")] long DictionaryId,
        [property: JsonPropertyName("max_value_count")] int MaxValueCount,
        [property: JsonPropertyName("group_name")] string? GroupName);
}
