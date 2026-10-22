using AM.Bridge;
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
}
