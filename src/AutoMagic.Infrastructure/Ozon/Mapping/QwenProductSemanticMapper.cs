using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoMagic.Application.Ozon.Mapping;

namespace AutoMagic.Infrastructure.Ozon.Mapping;

public sealed class QwenProductSemanticMapper(HttpClient httpClient) : IProductSemanticMapper
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    public async Task<ProductMappingCallResult> MapAsync(QwenApiCredentials credentials,
        ProductMappingRequest request, CancellationToken cancellationToken)
    {
        credentials = credentials.Validate();
        if (request.ContractVersion != ProductMappingInputBuilder.ContractVersion)
            throw new ArgumentException("不支持的商品映射合同版本。");
        var transport = QwenProductMappingTransport.Create(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = QwenMappingRuntime.ModelId,
            messages = new[]
            {
                new { role = "system", content = ProductMappingPromptAssets.SystemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(transport, ProductMappingJson.StrictOptions) },
            },
            temperature = 0,
            stream = false,
            enable_thinking = false,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "auto_magic_product_mapping_v2", strict = true,
                    schema = ProductMappingPromptAssets.ResponseSchema,
                },
            },
        });
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"百炼商品映射请求失败：HTTP {(int)response.StatusCode}。请检查凭证、模型权限或稍后重试。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var length = await stream.ReadAsync(chunk, cancellationToken);
            if (length == 0) break;
            if (buffer.Length + length > MaximumResponseBytes)
                throw new InvalidOperationException("百炼响应超过本地 4MB 限制。");
            buffer.Write(chunk, 0, length);
        }
        JsonDocument envelope;
        try { envelope = JsonDocument.Parse(buffer.ToArray()); }
        catch (JsonException) { throw new InvalidOperationException("百炼返回了无效的响应格式。"); }
        using (envelope)
        {
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                choices[0].ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("百炼没有返回可用的商品映射内容。");
            var choice = choices[0];
            var raw = choice.TryGetProperty("message", out var reply) && reply.ValueKind == JsonValueKind.Object
                ? Text(reply, "content") : "";
            var problems = new List<ProductMappingIssue>();
            ProductMappingResponse? result = null;
            if (Text(choice, "finish_reason") != "stop")
                problems.Add(new("error", "ai.incomplete", "product", null,
                    "模型响应被截断或未正常完成，不能作为完整的映射建议。"));
            else
            {
                try
                {
                    result = JsonSerializer.Deserialize<ProductMappingResponse>(raw, ProductMappingJson.StrictOptions);
                    if (result is null) throw new JsonException();
                    result = transport.RestoreAliases(result);
                }
                catch (JsonException)
                {
                    problems.Add(new("error", "ai.invalid_json", "product", null,
                        "模型输出不符合商品映射 V2 结构合同，原始响应保留供检查。"));
                }
            }
            var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            return new(Text(root, "id"), Text(root, "model"), ProductMappingPromptAssets.PromptVersion, raw,
                new QwenTokenUsage(Number(usage, "prompt_tokens"), Number(usage, "completion_tokens"), Number(usage, "total_tokens")),
                result, problems);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Number(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : 0;
}
