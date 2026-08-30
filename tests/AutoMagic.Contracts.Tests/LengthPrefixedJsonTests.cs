using AutoMagic.Contracts.Protocol;
using System.Text.Json;

namespace AutoMagic.Contracts.Tests;

public sealed class LengthPrefixedJsonTests
{
    [Fact]
    public async Task RoundTrip_PreservesEnvelopeAndPayload()
    {
        await using var stream = new MemoryStream();
        var expected = ProtocolEnvelope.Create(
            "request-1",
            BridgeProtocol.MessageTypes.SearchStart,
            new SearchStartPayload("连衣裙", 60, 23.45m, 67.89m, ProductSortModes.PriceAscending));

        await LengthPrefixedJson.WriteEnvelopeAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        var actual = await LengthPrefixedJson.ReadEnvelopeAsync(stream, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal("request-1", actual.RequestId);
        Assert.Equal(BridgeProtocol.MessageTypes.SearchStart, actual.Type);
        var payload = actual.Payload!.Value.Deserialize<SearchStartPayload>(BridgeJson.Options);
        Assert.Equal("连衣裙", payload!.Keyword);
        Assert.Equal(60, payload.MaxItems);
        Assert.Equal(23.45m, payload.ProcurementMinimumCny);
        Assert.Equal(67.89m, payload.ProcurementMaximumCny);
        Assert.Equal(ProductSortModes.PriceAscending, payload.SortMode);
    }

    [Fact]
    public async Task ReadEnvelope_RejectsUnsupportedVersion()
    {
        await using var stream = new MemoryStream();
        var envelope = new ProtocolEnvelope("99", "request-2", "search.start");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await LengthPrefixedJson.WriteEnvelopeAsync(stream, envelope, CancellationToken.None));
    }

    [Fact]
    public async Task ReadEnvelope_ReturnsNullAtCleanEndOfStream()
    {
        await using var stream = new MemoryStream();

        var actual = await LengthPrefixedJson.ReadEnvelopeAsync(stream, CancellationToken.None);

        Assert.Null(actual);
    }
}
