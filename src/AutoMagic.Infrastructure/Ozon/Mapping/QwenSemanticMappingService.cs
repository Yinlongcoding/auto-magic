using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMagic.Application.Ozon.Mapping;

namespace AutoMagic.Infrastructure.Ozon.Mapping;

public sealed class QwenSemanticMappingService(HttpClient httpClient) : IQwenSemanticMappingService
{
    private const string ChatCompletionsEndpoint = "chat/completions";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<QwenSemanticMappingResult> MapAsync(
        QwenApiCredentials credentials,
        SemanticMappingRequest request,
        CancellationToken cancellationToken)
    {
        credentials = credentials.Validate();
        ArgumentNullException.ThrowIfNull(request);
        var requestIssues = SemanticMappingResponseValidator.ValidateRequest(request);
        if (requestIssues.Count > 0)
        {
            throw new ArgumentException(
                $"Qwen映射请求未通过本地校验：{requestIssues[0].Message}",
                nameof(request));
        }

        var payload = new
        {
            model = QwenMappingRuntime.ModelId,
            messages = new[]
            {
                new { role = "system", content = SemanticMappingPromptAssets.SystemPrompt },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(request, SemanticMappingJson.IndentedOptions),
                },
            },
            temperature = 0,
            stream = false,
            enable_thinking = false,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "auto_magic_semantic_mapping_response",
                    strict = true,
                    schema = SemanticMappingPromptAssets.ResponseSchema,
                },
            },
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiKey);
        httpRequest.Content = JsonContent.Create(payload, options: ApiJsonOptions);

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBytes = await ReadBoundedAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(CreateSafeError(response.StatusCode, responseBytes, credentials));
        }

        QwenChatCompletionResponse? completion;
        try
        {
            completion = JsonSerializer.Deserialize<QwenChatCompletionResponse>(
                responseBytes,
                ApiJsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("百炼返回的Chat Completion响应格式无效。");
        }

        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("百炼响应没有包含可用的映射JSON内容。");
        }

        var validation = SemanticMappingResponseValidator.ParseAndValidate(request, content);
        return new QwenSemanticMappingResult(
            completion?.Id?.Trim() ?? string.Empty,
            completion?.Model?.Trim() ?? QwenMappingRuntime.ModelId,
            SemanticMappingPromptAssets.SkillVersion,
            QwenMappingRuntime.ContractVersion,
            DateTimeOffset.UtcNow,
            content,
            new QwenTokenUsage(
                completion?.Usage?.PromptTokens ?? 0,
                completion?.Usage?.CompletionTokens ?? 0,
                completion?.Usage?.TotalTokens ?? 0),
            validation);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InvalidOperationException("百炼响应超过本地4MB安全限制。");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static string CreateSafeError(
        HttpStatusCode statusCode,
        byte[] responseBytes,
        QwenApiCredentials credentials)
    {
        var reason = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "百炼拒绝了API Key，请确认凭证属于华北2（北京）地域并已开通模型权限。",
            HttpStatusCode.TooManyRequests =>
                "百炼请求频率或Token额度已达到限制，请稍后重试。",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                "百炼映射请求超时，请稍后重试。",
            _ => $"百炼映射请求失败：HTTP {(int)statusCode}。",
        };

        if (responseBytes.Length == 0 ||
            statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return reason;
        }

        string? providerMessage = null;
        try
        {
            using var document = JsonDocument.Parse(responseBytes);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                providerMessage = message.GetString();
            }
        }
        catch (JsonException)
        {
            // 不把未知响应体回显到界面或日志。
        }

        if (string.IsNullOrWhiteSpace(providerMessage))
        {
            return reason;
        }

        var safeMessage = providerMessage
            .Replace(credentials.ApiKey, "***", StringComparison.Ordinal)
            .Trim();
        safeMessage = safeMessage.Length <= 500 ? safeMessage : safeMessage[..500] + "…";
        return $"{reason} 百炼返回：{safeMessage}";
    }

    private sealed record QwenChatCompletionResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] IReadOnlyList<QwenChoice>? Choices,
        [property: JsonPropertyName("usage")] QwenUsage? Usage);

    private sealed record QwenChoice(
        [property: JsonPropertyName("message")] QwenMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record QwenMessage(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record QwenUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens);
}
