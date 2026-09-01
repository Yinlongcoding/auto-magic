using System.Net;
using System.Text;
using System.Text.Json;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Infrastructure.Ozon.Mapping;

namespace AutoMagic.Contracts.Tests;

public sealed class QwenSemanticMappingServiceTests
{
    [Fact]
    public async Task MapAsync_SendsFrozenModelSkillAndStrictSchemaThenValidatesContent()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var mappingJson = JsonSerializer.Serialize(
            ValidResponse(),
            SemanticMappingJson.StrictOptions);
        var handler = new StubHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = "chatcmpl-test",
                model = QwenMappingRuntime.ModelId,
                choices = new[]
                {
                    new
                    {
                        message = new { content = mappingJson },
                        finish_reason = "stop",
                    },
                },
                usage = new
                {
                    prompt_tokens = 100,
                    completion_tokens = 20,
                    total_tokens = 120,
                },
            }));
        });
        var service = new QwenSemanticMappingService(new HttpClient(handler)
        {
            BaseAddress = new Uri(QwenMappingRuntime.SharedBaseUrl),
        });

        var result = await service.MapAsync(
            new QwenApiCredentials(" secret-key "),
            Request(),
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", capturedRequest.Headers.Authorization?.Parameter);
        using var body = JsonDocument.Parse(capturedBody!);
        var root = body.RootElement;
        Assert.Equal(QwenMappingRuntime.ModelId, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        var responseFormat = root.GetProperty("response_format");
        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());
        Assert.True(responseFormat.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var schemaText = responseFormat.GetProperty("json_schema").GetProperty("schema").GetRawText();
        Assert.DoesNotContain("$ref", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("$defs", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("uniqueItems", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("minLength", schemaText, StringComparison.Ordinal);
        Assert.Contains("商品属性语义映射候选生成器", root.GetProperty("messages")[0]
            .GetProperty("content").GetString(), StringComparison.Ordinal);

        Assert.True(result.Validation.IsValid);
        Assert.Equal("chatcmpl-test", result.ProviderRequestId);
        Assert.Equal(120, result.Usage.TotalTokens);
        Assert.StartsWith("sha256:", result.SkillVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapAsync_DoesNotEchoApiKeyFromProviderError()
    {
        const string apiKey = "do-not-echo-key";
        var handler = new StubHandler(_ => Task.FromResult(Json(
            HttpStatusCode.BadRequest,
            $"{{\"error\":{{\"message\":\"invalid {apiKey}\"}}}}")));
        var service = new QwenSemanticMappingService(new HttpClient(handler)
        {
            BaseAddress = new Uri(QwenMappingRuntime.SharedBaseUrl),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MapAsync(
                new QwenApiCredentials(apiKey),
                Request(),
                CancellationToken.None));

        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptAssets_ContainVerifiedRulesAndInlineRuntimeSchema()
    {
        Assert.Contains("无吊牌", SemanticMappingPromptAssets.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("图片色", SemanticMappingPromptAssets.SystemPrompt, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", SemanticMappingPromptAssets.SkillVersion, StringComparison.Ordinal);
        var schema = SemanticMappingPromptAssets.ResponseSchema.GetRawText();
        Assert.DoesNotContain("$ref", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("$defs", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("uniqueItems", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("minimum", schema, StringComparison.Ordinal);
        Assert.Contains("targetMappings", schema, StringComparison.Ordinal);
    }

    private static SemanticMappingRequest Request() =>
        new(
            "request-001",
            SemanticMappingPurposes.EvaluateCandidates,
            new SemanticMappingContext(
                "1688",
                "Ozon",
                200000933,
                93211,
                "服装 > 无袖连衣裙",
                true,
                "1060627180703",
                "https://detail.1688.com/offer/1060627180703.html",
                "2026-09-01T12:13:38.007Z",
                1,
                false),
            [
                new SemanticTargetAttribute(
                    9163,
                    0,
                    "性别",
                    string.Empty,
                    "String",
                    true,
                    true,
                    2,
                    320,
                    []),
            ],
            [new SemanticSourceFact("f001", "商品名称", "蓝色连衣裙女夏", "document.title")],
            SemanticMappingOutputContract.Default);

    private static SemanticMappingResponse ValidResponse() =>
        new(
            "request-001",
            [
                new SemanticTargetMapping(
                    9163,
                    "性别",
                    SemanticMappingStatuses.DictionaryPending,
                    ["f001"],
                    ["商品名称"],
                    ["蓝色连衣裙女夏"],
                    ["女性"],
                    [],
                    SemanticMappingMethods.ExplicitTitle,
                    0.95m,
                    true,
                    false,
                    "标题明确写出女。"),
            ],
            [],
            ["尚未提供Ozon字典候选。"]);

    private static HttpResponseMessage Json(HttpStatusCode status, string value) =>
        new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
