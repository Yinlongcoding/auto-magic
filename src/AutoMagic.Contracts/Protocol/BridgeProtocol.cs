using System.Text.Json;

namespace AutoMagic.Contracts.Protocol;

public static class BridgeProtocol
{
    public const string Version = "1.0";
    public const string NativeHostName = "com.automagic.desktop";
    public const string PipeName = "AutoMagic.Desktop.Bridge.v1";
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static class MessageTypes
    {
        public const string ExtensionReady = "extension.ready";
        public const string SearchStart = "search.start";
        public const string SearchAccepted = "search.accepted";
        public const string SearchProgress = "search.progress";
        public const string SearchCompleted = "search.completed";
        public const string SearchFailed = "search.failed";
        public const string BridgeStatus = "bridge.status";
    }
}

public sealed record BridgeError(string Code, string Message);

public sealed record ProtocolEnvelope(
    string ProtocolVersion,
    string RequestId,
    string Type,
    JsonElement? Payload = null,
    BridgeError? Error = null)
{
    public static ProtocolEnvelope Create<T>(string requestId, string type, T payload) =>
        new(
            BridgeProtocol.Version,
            requestId,
            type,
            JsonSerializer.SerializeToElement(payload, BridgeJson.Options));

    public static ProtocolEnvelope Failure(
        string requestId,
        string type,
        string code,
        string message) =>
        new(BridgeProtocol.Version, requestId, type, null, new BridgeError(code, message));
}

public static class ProductSortModes
{
    public const string Sales = "sales";
    public const string PriceAscending = "priceAscending";

    public static bool IsSupported(string value) =>
        value is Sales or PriceAscending;
}

public sealed record SearchStartPayload(
    string Keyword,
    int MaxItems = 60,
    decimal? ProcurementMinimumCny = null,
    decimal? ProcurementMaximumCny = null,
    string SortMode = ProductSortModes.Sales,
    bool IncludeDetailFacts = false);

public sealed record ExtensionReadyPayload(string ExtensionVersion);

public sealed record SearchAcceptedPayload(string JobId);

public sealed record SearchProgressPayload(
    string JobId,
    string Stage,
    int CompletedItems,
    int TotalItems,
    string? Message = null);

public sealed record ProductItemDto(
    string? DetailUrl,
    string ImageUrl,
    string Title,
    string PriceCny);

public sealed record DetailFactDto(
    string Label,
    string Value,
    string Source);

public sealed record DetailFactSnapshotDto(
    string DetailUrl,
    string CapturedAt,
    string? PageTitle,
    IReadOnlyList<DetailFactDto> Facts,
    JsonElement? Diagnostics,
    JsonElement? Raw = null);

public sealed record DetailCollectionResultDto(
    int ItemIndex,
    int ItemPosition,
    string? ProductTitle,
    string? DetailUrl,
    string Status,
    string? FinalUrl,
    string? CapturedAt,
    string? PageTitle,
    IReadOnlyList<DetailFactDto> Facts,
    IReadOnlyList<string>? Warnings,
    IReadOnlyList<string>? Errors,
    JsonElement? Diagnostics = null,
    JsonElement? Raw = null,
    string? FailureCode = null);

public sealed record SearchResultPayload(
    string Keyword,
    string CapturedAt,
    int Count,
    IReadOnlyList<ProductItemDto> Items,
    JsonElement? Diagnostics,
    DetailFactSnapshotDto? DetailSnapshot = null,
    IReadOnlyList<DetailCollectionResultDto>? DetailResults = null);
