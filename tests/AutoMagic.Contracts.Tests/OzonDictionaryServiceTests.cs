using System.Net;
using System.Text;
using AutoMagic.Application.Ozon;
using AutoMagic.Infrastructure.Ozon;

namespace AutoMagic.Contracts.Tests;

public sealed class OzonDictionaryServiceTests
{
    [Fact]
    public async Task SearchAttributeValuesAsync_UsesCandidateSearchContract()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("v1/description-category/attribute/values/search", request.RequestUri!.AbsolutePath.TrimStart('/'));
            Assert.Equal("client", request.Headers.GetValues("Client-Id").Single());
            Assert.Equal("key", request.Headers.GetValues("Api-Key").Single());
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {"result":[{"id":35545,"value":"42","info":"","picture":""}]}
                """);
        });
        var service = new OzonDictionaryService(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        });

        var values = await service.SearchAttributeValuesAsync(
            new OzonTemporaryCredentials("client", "key"),
            200000933,
            93211,
            4295,
            " 42 ",
            CancellationToken.None);

        Assert.Equal([(35545L, "42")], values.Select(value => (value.ValueId, value.Value)));
        Assert.NotNull(body);
        Assert.Contains("\"attribute_id\":4295", body);
        Assert.Contains("\"description_category_id\":200000933", body);
        Assert.Contains("\"type_id\":93211", body);
        Assert.Contains("\"limit\":100", body);
        Assert.Contains("\"value\":\"42\"", body);
    }

    [Fact]
    public async Task SearchAttributeValuesAsync_RejectsEmptyCandidate()
    {
        var service = new OzonDictionaryService(new HttpClient(new StubHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.OK, "{\"result\":[]}"))))
        {
            BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAttributeValuesAsync(
                new OzonTemporaryCredentials("client", "key"),
                1,
                2,
                3,
                " ",
                CancellationToken.None));
    }

    [Fact]
    public async Task GetAttributeValuesAsync_ReadsAllPagesAndUsesOzonCursor()
    {
        var requests = new List<string>();
        var handler = new StubHandler(async request =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync());
            return requests.Count == 1
                ? Json(HttpStatusCode.OK, """
                    {"has_next":true,"result":[{"id":11,"value":"黑色","info":"","picture":""}]}
                    """)
                : Json(HttpStatusCode.OK, """
                    {"has_next":false,"result":[{"id":12,"value":"红色","info":"","picture":""}]}
                    """);
        });
        var service = new OzonDictionaryService(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        });

        var values = await service.GetAttributeValuesAsync(
            new OzonTemporaryCredentials("client", "key"),
            200000933,
            93211,
            10096,
            "ZH_HANS",
            CancellationToken.None);

        Assert.Equal([11L, 12L], values.Select(value => value.ValueId));
        Assert.Equal(2, requests.Count);
        Assert.Contains("\"last_value_id\":0", requests[0]);
        Assert.Contains("\"last_value_id\":11", requests[1]);
        Assert.Contains("\"limit\":1000", requests[0]);
    }

    [Fact]
    public async Task GetAttributeValuesAsync_DoesNotEchoCredentialsInError()
    {
        const string apiKey = "do-not-echo-this-key";
        var service = new OzonDictionaryService(new HttpClient(new StubHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.BadRequest, $"{{\"message\":\"{apiKey}\"}}"))))
        {
            BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetAttributeValuesAsync(
                new OzonTemporaryCredentials("client", apiKey),
                1,
                2,
                3,
                "DEFAULT",
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
