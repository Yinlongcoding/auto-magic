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
        var service = new DesktopBridgeService(NullLogger<DesktopBridgeService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                BridgeProtocol.PipeName,
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
                timeout.Token);
            var searchRequest = await LengthPrefixedJson.ReadEnvelopeAsync(client, timeout.Token);
            Assert.NotNull(searchRequest);
            Assert.Equal(BridgeProtocol.MessageTypes.SearchStart, searchRequest.Type);
            var requestPayload = searchRequest.Payload!.Value.Deserialize<SearchStartPayload>(BridgeJson.Options);
            Assert.NotNull(requestPayload);
            Assert.Equal(20.5m, requestPayload.ProcurementMinimumCny);
            Assert.Equal(80.75m, requestPayload.ProcurementMaximumCny);
            Assert.Equal(ProductSortModes.Sales, requestPayload.SortMode);

            var resultPayload = new SearchResultPayload(
                "连衣裙",
                "2026-08-28T00:00:00.000Z",
                1,
                [new ProductItemDto("https://detail.1688.com/offer/1.html", "https://img.example/1.jpg", "示例商品", "99.00")],
                null);
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
