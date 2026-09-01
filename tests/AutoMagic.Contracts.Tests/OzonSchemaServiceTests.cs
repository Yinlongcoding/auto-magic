using System.Net;
using System.Text;
using AutoMagic.Application.Ozon;
using AutoMagic.Infrastructure.Ozon;

namespace AutoMagic.Contracts.Tests;

public sealed class OzonSchemaServiceTests
{
    [Fact]
    public async Task GetCategorySchemaAsync_MapsDynamicAttributesAndSendsTemporaryHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new StubHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {
                  "result": [
                    {
                      "id": 85,
                      "attribute_complex_id": 0,
                      "name": "品牌",
                      "description": "商品品牌",
                      "type": "String",
                      "is_collection": false,
                      "is_required": true,
                      "dictionary_id": 28732849,
                      "max_value_count": 1,
                      "group_name": "基础信息"
                    }
                  ]
                }
                """);
        });
        var service = new OzonSchemaService(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        });

        var schema = await service.GetCategorySchemaAsync(
            new OzonTemporaryCredentials(" client-1 ", " secret-key "),
            170000001,
            90001,
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("client-1", capturedRequest.Headers.GetValues("Client-Id").Single());
        Assert.Equal("secret-key", capturedRequest.Headers.GetValues("Api-Key").Single());
        Assert.Contains("\"description_category_id\":170000001", capturedBody);
        Assert.Single(schema.Attributes);
        Assert.Equal(1, schema.RequiredCount);
        Assert.Equal(28732849, schema.Attributes[0].DictionaryId);
    }

    [Fact]
    public async Task GetCategorySchemaAsync_DoesNotEchoCredentialsInAuthorizationError()
    {
        const string apiKey = "do-not-echo-this-key";
        var service = new OzonSchemaService(new HttpClient(new StubHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.BadRequest, $"{{\"message\":\"{apiKey}\"}}"))))
        {
            BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetCategorySchemaAsync(
                new OzonTemporaryCredentials("client", apiKey),
                1,
                2,
                CancellationToken.None));

        Assert.DoesNotContain(apiKey, error.Message);
        Assert.Contains("HTTP 400", error.Message);
    }

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
