using AM.Core.Pricing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AM.Desktop.Models;

/// <summary>
/// 一个采集批次。定价参数在采集时填写并冻结，批次不允许合并，一次上架只处理一个批次。
/// </summary>
public sealed partial class CollectionBatch : ObservableObject
{
    public required string Id { get; init; }
    public required string Keyword { get; init; }
    public required string CategoryPath { get; init; }

    /// <summary>新建时冻结的 Ozon 类目和类型。</summary>
    public long DescriptionCategoryId { get; init; }
    public long TypeId { get; init; }
    public required DateTime CreatedAt { get; init; }

    /// <summary>新建时填写的定价参数，随批次冻结，采集到上架不变。</summary>
    public required PricingParameters Pricing { get; init; }

    /// <summary>售价币种，新建时取自设置里的 currency_code，随批次冻结。</summary>
    public required string CurrencyCode { get; init; }

    public string PricingSummary =>
        $"佣金 {Pricing.CommissionRate:P0} · 物流 ¥{Pricing.LogisticsCost:0.##} · 目标利润 {Pricing.TargetMargin:P0} · " +
        $"最低利润 {Pricing.MinMargin:P0} · 售价 {Pricing.SalePriceMin:0.##}～{Pricing.SalePriceMax:0.##} {CurrencyCode} · " +
        $"汇率 {Pricing.ExchangeRate:0.####}（浮动 {Pricing.ExchangeFloat:P0}）" +
        (Pricing.PurchasePriceMin is { } min ? $" · 采购价 ≥ ¥{min:0.##}" : "");

    public int TargetCount { get; init; }

    /// <summary>新建时选的排序方式（界面文案），下发采集时转成协议取值。</summary>
    public string Sort { get; init; } = "综合";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ProgressText), nameof(ListText))]
    private BatchState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ListText))]
    private int _collectPass = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ListText))]
    private int _collectedCount;

    /// <summary>第一遍健康检查超阈值时的提示，如"采集可能失效"。</summary>
    [ObservableProperty]
    private string? _warning;

    // 上架。SkuCount 是定价后保留的 SKU 数，BlockingCount 是有阻断项的商品数（目前只有价格异常）。
    [ObservableProperty]
    private int _skuCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListText))]
    private int _blockingCount;

    [ObservableProperty]
    private int _droppedSkuCount;

    /// <summary>
    /// 待处理页"属性"分段里为源值选定的结果，只对本批次生效（18 号"同一任务内复用"）。
    /// 按（属性、源值）存一份，不按商品各存一份，这样同批次里含该源值的商品都自动套用，以后补抓的也一样。
    /// </summary>
    public List<AM.Core.Rules.ManualEdit> ManualEdits { get; init; } = [];

    public int AbandonedCount { get; init; }
    public int CopyFailedCount { get; init; }
    public int SubmittedCount { get; init; }
    public int SucceededCount { get; init; }

    public string StateText => State.DisplayName();

    public string ProgressText => State == BatchState.Collecting
        ? $"第 {CollectPass} 遍 {CollectedCount}/{TargetCount}"
        : $"{CollectedCount}/{TargetCount}";

    /// <summary>批次列表第二行：状态和最关键的数字。</summary>
    public string ListText => State switch
    {
        BatchState.Collecting => $"采集中 · {ProgressText}",
        BatchState.Listing when BlockingCount > 0 => $"上架中 · 阻断 {BlockingCount}",
        BatchState.Completed => $"已完成 · 成功 {SucceededCount}/{SubmittedCount}",
        _ => $"{StateText} · {ProgressText}",
    };

    public string DisplayName => $"{Keyword} · {CategoryPath} · {CreatedAt:MM-dd HH:mm}";
}
