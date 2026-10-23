using AM.Bridge;
using AM.Core.Pricing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AM.Desktop.Models;

/// <summary>采集页商品列表的一行。Index 在批次内唯一，第二遍重抓更新同一行。</summary>
public sealed partial class CollectedProduct(int index, string title, int skuCount, string status) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    private string _title = title;

    [ObservableProperty]
    private int _skuCount = skuCount;

    [ObservableProperty]
    private string _status = status;

    /// <summary>插件发来的完整采集结果，内存暂存。接入 SQLite（M2）后落库。</summary>
    public CollectedOffer? Offer { get; set; }

    /// <summary>采集完成后按批次定价参数算出的结果，与 Offer.Skus 一一对应。未定价为 null。</summary>
    public IReadOnlyList<SkuPrice>? Prices { get; set; }

    /// <summary>
    /// 规则匹配结果，与 Offer.Skus 一一对应；定价丢弃的 SKU 为 null（不提交，不用匹配）。
    /// 由 BatchMatching 按当前规则、字典和人工修订重算，不落库：规则一改，下次打开就是新结果。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<IReadOnlyList<AM.Core.Rules.AttributeMatch>?>? AttributeMatches { get; set; }

    /// <summary>用户在待处理页或文案页点"放弃该商品"后标为 true。终态，不可撤销。</summary>
    [ObservableProperty]
    private bool _isAbandoned;
}
