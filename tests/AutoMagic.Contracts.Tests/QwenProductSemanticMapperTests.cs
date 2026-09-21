using System.Net;
using System.Text;
using System.Text.Json;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Infrastructure.Ozon.Mapping;

namespace AutoMagic.Contracts.Tests;

public sealed class QwenProductSemanticMapperTests
{
    [Fact]
    public async Task Map_SendsCompactReadableContractWithoutInternalAuditOrIdentityFields()
    {
        var request = ProductMappingTests.Fixture.Request();
        var answer = ProductMappingTests.Fixture.Response(request);
        string? body = null;
        var service = Service(async message =>
        {
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            body = await message.Content!.ReadAsStringAsync();
            return Reply(JsonSerializer.Serialize(answer, ProductMappingJson.StrictOptions));
        });
        var result = await service.MapAsync(new("test-key"), request, CancellationToken.None);
        Assert.NotNull(result.Response);
        Assert.Empty(result.ParseIssues);
        Assert.Equal(30, result.Usage.TotalTokens);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.False(root.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(QwenMappingRuntime.ModelId, root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var schema = root.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetRawText();
        Assert.DoesNotContain("$ref", schema);
        Assert.DoesNotContain("$defs", schema);
        var user = root.GetProperty("messages")[1].GetProperty("content").GetString()!;
        using var input = JsonDocument.Parse(user);
        Assert.Equal(3, input.RootElement.GetProperty("variants").GetArrayLength());
        Assert.Contains(input.RootElement.GetProperty("targetAttributes").EnumerateArray(),
            a => !a.GetProperty("isRequired").GetBoolean());
        Assert.Contains(input.RootElement.GetProperty("productFacts").EnumerateArray(),
            fact => fact.GetProperty("text").GetString() == "材质：涤纶");
        Assert.DoesNotContain("sourcePath", user);
        Assert.DoesNotContain("collectionId", user);
        Assert.DoesNotContain("inputFingerprint", user);
        Assert.DoesNotContain("merchantSku", user);
        Assert.DoesNotContain("sourceCombinationKey", user);
        Assert.DoesNotContain("SHOULD_NOT_BE_SENT", user);
        Assert.DoesNotContain("defaultOriginCountry", user);
        Assert.DoesNotContain("cardGroupingStrategy", user);
        Assert.Contains("不生成图片", ProductMappingPromptAssets.SystemPrompt);
        Assert.StartsWith("sha256:", result.PromptVersion);
    }

    [Fact]
    public void CompactTransport_RestoresShortEvidenceAndVariantAliasesToInternalKeys()
    {
        var request = ProductMappingTests.Fixture.Request();
        var transport = QwenProductMappingTransport.Create(request);
        var productEvidence = transport.ProductFacts.Single(fact => fact.Text == "材质：涤纶").EvidenceId;
        var variant = transport.Variants[0];
        var response = new ProductMappingResponse(request.RequestId,
            [new(10, null, ProductMappingStatuses.Suggested, [new("полиэстер", null)],
                [productEvidence], "材质明确。")],
            [new(variant.VariantId, [])], []);

        var restored = transport.RestoreAliases(response);

        Assert.Equal("product:f001", restored.ProductMappings[0].EvidenceFactIds[0]);
        Assert.Equal(request.Skus[0].VariantKey, restored.Variants[0].VariantKey);
        var compact = JsonSerializer.Serialize(transport, ProductMappingJson.StrictOptions);
        var internalJson = JsonSerializer.Serialize(request, ProductMappingJson.StrictOptions);
        Assert.True(compact.Length < internalJson.Length);
    }

    [Fact]
    public void CompactTransport_RestoresCaseChangedAliasesWhenTheyRemainUnique()
    {
        var request = ProductMappingTests.Fixture.Request();
        var transport = QwenProductMappingTransport.Create(request);
        var response = new ProductMappingResponse(request.RequestId,
            [new(10, null, ProductMappingStatuses.Suggested, [new("полиэстер", null)],
                ["E1"], "材质明确。")],
            [new("V1", [])], []);

        var restored = transport.RestoreAliases(response);

        Assert.Equal("product:f001", restored.ProductMappings[0].EvidenceFactIds[0]);
        Assert.Equal(request.Skus[0].VariantKey, restored.Variants[0].VariantKey);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not-json")]
    [InlineData("{\"requestId\":\"test\",\"productMappings\":[],\"variants\":[],\"warnings\":[],\"canPublish\":true}")]
    public async Task Map_InvalidContractKeepsRawResponseWithoutAcceptingIt(string content)
    {
        var service = Service(_ => Task.FromResult(Reply(content)));
        var result = await service.MapAsync(new("test-key"), ProductMappingTests.Fixture.Request(), CancellationToken.None);
        Assert.Null(result.Response);
        Assert.Equal(content, result.RawContent);
        Assert.Contains(result.ParseIssues, i => i.Code == "ai.invalid_json");
    }

    [Fact]
    public async Task Map_TruncatedEvenParseableResponseCannotBeUsed()
    {
        var request = ProductMappingTests.Fixture.Request();
        var content = JsonSerializer.Serialize(ProductMappingTests.Fixture.Response(request), ProductMappingJson.StrictOptions);
        var service = Service(_ => Task.FromResult(Reply(content, "length")));
        var result = await service.MapAsync(new("test-key"), request, CancellationToken.None);
        Assert.Null(result.Response);
        Assert.Contains(result.ParseIssues, i => i.Code == "ai.incomplete");
    }

    [Fact]
    public async Task Map_ProviderErrorNeverEchoesCredentialsOrBody()
    {
        var service = Service(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("the-key-is-private and internal provider context") }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.MapAsync(new("the-key-is-private"),
            ProductMappingTests.Fixture.Request(), CancellationToken.None));
        Assert.DoesNotContain("the-key-is-private", error.Message);
        Assert.DoesNotContain("internal provider", error.Message);
        Assert.Contains("403", error.Message);
    }

    private static QwenProductSemanticMapper Service(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://example.test/") });

    private static HttpResponseMessage Reply(string content, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = "test-response", model = QwenMappingRuntime.ModelId,
            choices = new[] { new { message = new { content }, finish_reason = finish } },
            usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 },
        }), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
}
