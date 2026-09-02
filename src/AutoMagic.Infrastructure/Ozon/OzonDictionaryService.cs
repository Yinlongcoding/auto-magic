using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMagic.Application.Ozon;

namespace AutoMagic.Infrastructure.Ozon;

public sealed class OzonDictionaryService(HttpClient httpClient) : IOzonDictionaryService
{
    private const string ValuesEndpoint = "v1/description-category/attribute/values";
    private const string ValuesSearchEndpoint = "v1/description-category/attribute/values/search";
    private const int PageSize = 1000;
    private const int MaxPages = 1000;

    public async Task<IReadOnlyList<OzonDictionaryValue>> SearchAttributeValuesAsync(
        OzonTemporaryCredentials credentials,
        long descriptionCategoryId,
        long typeId,
        long attributeId,
        string value,
        CancellationToken cancellationToken)
    {
        credentials = credentials.Validate();
        ValidateIds(descriptionCategoryId, typeId, attributeId);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("字典搜索值不能为空。", nameof(value));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ValuesSearchEndpoint);
        request.Headers.TryAddWithoutValidation("Client-Id", credentials.ClientId);
        request.Headers.TryAddWithoutValidation("Api-Key", credentials.ApiKey);
        request.Content = JsonContent.Create(new
        {
            attribute_id = attributeId,
            description_category_id = descriptionCategoryId,
            type_id = typeId,
            limit = 100,
            value = value.Trim(),
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
        var payload = await JsonSerializer.DeserializeAsync<OzonDictionarySearchResponse>(
            content,
            cancellationToken: cancellationToken);
        if (payload?.Result is null)
        {
            throw new InvalidOperationException("Ozon 返回的字典搜索格式无效。");
        }

        return payload.Result
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => new OzonDictionaryValue(
                item.Id,
                item.Value.Trim(),
                item.Info?.Trim() ?? string.Empty,
                item.Picture?.Trim() ?? string.Empty))
            .GroupBy(item => item.ValueId)
            .Select(group => group.First())
            .ToArray();
    }

    public async Task<IReadOnlyList<OzonDictionaryValue>> GetAttributeValuesAsync(
        OzonTemporaryCredentials credentials,
        long descriptionCategoryId,
        long typeId,
        long attributeId,
        string language,
        CancellationToken cancellationToken)
    {
        credentials = credentials.Validate();
        ValidateIds(descriptionCategoryId, typeId, attributeId);

        language = string.IsNullOrWhiteSpace(language) ? "DEFAULT" : language.Trim();
        var values = new List<OzonDictionaryValue>();
        long lastValueId = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ValuesEndpoint);
            request.Headers.TryAddWithoutValidation("Client-Id", credentials.ClientId);
            request.Headers.TryAddWithoutValidation("Api-Key", credentials.ApiKey);
            request.Content = JsonContent.Create(new
            {
                attribute_id = attributeId,
                description_category_id = descriptionCategoryId,
                type_id = typeId,
                language,
                last_value_id = lastValueId,
                limit = PageSize,
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
            var payload = await JsonSerializer.DeserializeAsync<OzonDictionaryValuesResponse>(
                content,
                cancellationToken: cancellationToken);
            if (payload?.Result is null)
            {
                throw new InvalidOperationException("Ozon 返回的字典值格式无效。");
            }

            var pageValues = payload.Result
                .Where(value => value.Id > 0 && !string.IsNullOrWhiteSpace(value.Value))
                .Select(value => new OzonDictionaryValue(
                    value.Id,
                    value.Value.Trim(),
                    value.Info?.Trim() ?? string.Empty,
                    value.Picture?.Trim() ?? string.Empty))
                .ToArray();
            values.AddRange(pageValues);

            if (!payload.HasNext)
            {
                return values
                    .GroupBy(value => value.ValueId)
                    .Select(group => group.First())
                    .ToArray();
            }

            if (payload.Result.Count == 0)
            {
                throw new InvalidOperationException("Ozon 字典返回空页但仍声明存在下一页，已停止读取。");
            }

            var nextLastValueId = pageValues.Length > 0
                ? pageValues.Max(value => value.ValueId)
                : payload.Result.Max(value => value.Id);
            if (nextLastValueId <= lastValueId)
            {
                throw new InvalidOperationException("Ozon 字典分页游标没有前进，已停止读取以避免重复请求。");
            }

            lastValueId = nextLastValueId;
        }

        throw new InvalidOperationException("Ozon 字典分页超过安全上限，已停止读取。");
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
            _ => $"Ozon 字典请求失败：HTTP {(int)response.StatusCode}。",
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

    private static void ValidateIds(long descriptionCategoryId, long typeId, long attributeId)
    {
        if (descriptionCategoryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptionCategoryId), "description_category_id 必须是正整数。");
        }

        if (typeId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(typeId), "type_id 必须是正整数。");
        }

        if (attributeId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attributeId), "attribute_id 必须是正整数。");
        }
    }

    private sealed record OzonDictionaryValuesResponse(
        [property: JsonPropertyName("has_next")] bool HasNext,
        [property: JsonPropertyName("result")] IReadOnlyList<OzonDictionaryValueResponse>? Result);

    private sealed record OzonDictionarySearchResponse(
        [property: JsonPropertyName("result")] IReadOnlyList<OzonDictionaryValueResponse>? Result);

    private sealed record OzonDictionaryValueResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("info")] string? Info,
        [property: JsonPropertyName("picture")] string? Picture);
}
