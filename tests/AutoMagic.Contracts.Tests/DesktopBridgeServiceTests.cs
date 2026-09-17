using System.IO.Pipes;
using System.Text.Json;
using AutoMagic.Contracts.Protocol;
using AutoMagic.Infrastructure.Bridge;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMagic.Contracts.Tests;

public sealed class DesktopBridgeServiceTests
{
    [Fact]
    public async Task SearchAsync_RoundTripsThroughNamedPipe()
    {
        var pipeName = $"AutoMagic.Desktop.Bridge.Tests.{Guid.NewGuid():N}";
        var service = new DesktopBridgeService(
            NullLogger<DesktopBridgeService>.Instance,
            pipeName);
        SearchProgressPayload? observedProgress = null;
        service.SearchProgressChanged += (_, progress) => observedProgress = progress;
        await service.StartAsync(CancellationToken.None);

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(timeout.Token);
            await LengthPrefixedJson.WriteEnvelopeAsync(
                client,
                ProtocolEnvelope.Create(
                    "ready-1",
                    BridgeProtocol.MessageTypes.ExtensionReady,
                    new ExtensionReadyPayload("0.1.0")),
                timeout.Token);

            await WaitUntilAsync(() => service.IsExtensionConnected, timeout.Token);
            var resultTask = service.SearchAsync(
                "连衣裙",
                60,
                20.5m,
                80.75m,
                ProductSortModes.Sales,
                includeDetailFacts: true,
                cancellationToken: timeout.Token);
            var searchRequest = await LengthPrefixedJson.ReadEnvelopeAsync(client, timeout.Token);
            Assert.NotNull(searchRequest);
            Assert.Equal(BridgeProtocol.MessageTypes.SearchStart, searchRequest.Type);
            var requestPayload = searchRequest.Payload!.Value.Deserialize<SearchStartPayload>(BridgeJson.Options);
            Assert.NotNull(requestPayload);
            Assert.Equal(20.5m, requestPayload.ProcurementMinimumCny);
            Assert.Equal(80.75m, requestPayload.ProcurementMaximumCny);
            Assert.Equal(ProductSortModes.Sales, requestPayload.SortMode);
            Assert.True(requestPayload.IncludeDetailFacts);

            await LengthPrefixedJson.WriteEnvelopeAsync(
                client,
                ProtocolEnvelope.Create(
                    searchRequest.RequestId,
                    BridgeProtocol.MessageTypes.SearchProgress,
                    new SearchProgressPayload("job-1", "first_pass", 1, 10, "商品 1：success")),
                timeout.Token);
            await WaitUntilAsync(() => observedProgress is not null, timeout.Token);
            Assert.Equal(1, observedProgress!.CompletedItems);
            Assert.Equal("first_pass", observedProgress.Stage);

            var resultPayload = new SearchResultPayload(
                "连衣裙",
                "2026-08-28T00:00:00.000Z",
                1,
                [new ProductItemDto("https://detail.1688.com/offer/1.html", "https://img.example/1.jpg", "示例商品", "99.00")],
                null,
                new DetailFactSnapshotDto(
                    "https://detail.1688.com/offer/1.html",
                    "2026-08-28T00:00:01.000Z",
                    "示例商品 - 阿里巴巴",
                    [new DetailFactDto("材质", "棉", "normal-attributes")],
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        offerId = "1",
                        attributes = new[] { new { label = "材质", value = "棉" } },
                    })));
            await LengthPrefixedJson.WriteEnvelopeAsync(
                client,
                ProtocolEnvelope.Create(
                    searchRequest.RequestId,
                    BridgeProtocol.MessageTypes.SearchCompleted,
                    resultPayload),
                timeout.Token);

            var result = await resultTask;
            Assert.Equal(1, result.Count);
            Assert.Equal("示例商品", result.Items[0].Title);
            Assert.NotNull(result.DetailSnapshot);
            Assert.Equal("棉", result.DetailSnapshot.Facts[0].Value);
            Assert.Equal("1", result.DetailSnapshot.Raw?.GetProperty("offerId").GetString());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(20, cancellationToken);
        }
    }
}
