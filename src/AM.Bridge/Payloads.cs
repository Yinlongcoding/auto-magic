namespace AM.Bridge;

// 桌面端 → 插件

/// <summary>
/// 开始采集。同一时间插件只接受一个采集。
/// 失败率超过 HealthFailureRate 时插件跳过第二遍。
/// PurchasePriceMin/Max 对应搜索页的 priceStart/priceEnd，为 null 时不筛选。
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
/// 成功时 Offer 有值；失败时 Offer 为 null，FailureCode 说明原因。
/// </summary>
public sealed record CollectItemPayload(
    string BatchId,
    int Index,
    int Pass,
    bool Success,
    string? OfferId,
    string? FailureCode,
    CollectedOffer? Offer);

/// <summary>
/// 一个 1688 商品的采集结果，字段来源见设计目录 16 号"实测结果"。
/// 缺失的值保持 null，不补默认值。
/// </summary>
/// <param name="PriceType">页面上的价格类型：rangePrice（全部 SKU 共用一套阶梯）或 skuPrice（每个 SKU 单独定价）。采购价在每个 SKU 上。</param>
/// <param name="WeightKg">重量（千克）。页面为 0 时为 null。</param>
/// <param name="DetailUrl">详情图的地址，图片本身暂不抓取。</param>
public sealed record CollectedOffer(
    string OfferId,
    string Title,
    string? PriceType,
    decimal? WeightKg,
    string? Brand,
    IReadOnlyList<string> MainImages,
    string? DetailUrl,
    IReadOnlyList<SourceAttribute> Attributes,
    IReadOnlyList<SkuAxis> Axes,
    IReadOnlyList<CollectedSku> Skus);

public sealed record SourceAttribute(string Label, string Value);

public sealed record SkuAxis(string Name, IReadOnlyList<SkuAxisOption> Options);

public sealed record SkuAxisOption(string Name, string? ImageUrl);

/// <summary>
/// 页面上真实存在的 SKU 组合，不做笛卡尔推导。OptionValues 与 Axes 一一对应。
/// </summary>
/// <param name="PurchasePrice">
/// 采购价（原价，买 1 件），见 13 号第 6 项。rangePrice 取商品原价第一档，skuPrice 取该 SKU 的 price。
/// 读不到或价格类型未知时为 null，不补值，进人工处理。
/// </param>
public sealed record CollectedSku(
    string SkuId,
    IReadOnlyList<string> OptionValues,
    decimal? PurchasePrice,
    int? Stock,
    string? ImageUrl);

public sealed record CollectPassDonePayload(
    string BatchId,
    int Pass,
    int Total,
    int Failed,
    bool SkipSecondPass);

public sealed record CollectFinishedPayload(string BatchId, int Succeeded, int Failed, bool Healthy);

public sealed record CollectStoppedPayload(string BatchId);

public sealed record CollectFailedPayload(string BatchId);

/// <summary>
/// 插件和桌面端共用的排序取值。只列实测确认过 URL 参数的排序（16 号"实测结果"）。
/// </summary>
public static class CollectSort
{
    public const string Default = "default";
    public const string Sales = "sales";
}
