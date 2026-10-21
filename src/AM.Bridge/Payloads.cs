namespace AM.Bridge;

// 桌面端 → 插件

/// <summary>
/// 开始采集。同一时间插件只接受一个采集。
/// 失败率超过 HealthFailureRate 时插件跳过第二遍。
/// </summary>
public sealed record CollectStartPayload(
    string BatchId,
    string Keyword,
    int TargetCount,
    string Sort,
    decimal? PurchasePriceMin,
    decimal? PurchasePriceMax,
    int IntervalMs,
    double HealthFailureRate);

public sealed record CollectStopPayload(string BatchId);

// 插件 → 桌面端

public sealed record ExtensionHelloPayload(string ExtensionVersion, string Collector);

public sealed record CollectAcceptedPayload(string BatchId);

public sealed record CollectProgressPayload(string BatchId, int Pass, int Done, int Total);

/// <summary>
/// 一个商品的一次抓取结果。Index 在本批次内唯一，第二遍重抓用同一个 Index。
/// 通信骨架阶段只有最小字段，完整字段按 16 号文档在实现真实抓取时补上。
/// </summary>
public sealed record CollectItemPayload(
    string BatchId,
    int Index,
    int Pass,
    bool Success,
    string? OfferId,
    string? Title,
    int SkuCount,
    string? FailureCode);

public sealed record CollectPassDonePayload(
    string BatchId,
    int Pass,
    int Total,
    int Failed,
    bool SkipSecondPass);

public sealed record CollectFinishedPayload(string BatchId, int Succeeded, int Failed, bool Healthy);

public sealed record CollectStoppedPayload(string BatchId);

public sealed record CollectFailedPayload(string BatchId);

/// <summary>插件和桌面端共用的排序取值。</summary>
public static class CollectSort
{
    public const string Default = "default";
    public const string Sales = "sales";
    public const string PriceAsc = "priceAsc";
    public const string PriceDesc = "priceDesc";
}
