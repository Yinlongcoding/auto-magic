using System.Collections.ObjectModel;
using AM.Desktop.Models;

namespace AM.Desktop.Services;

/// <summary>
/// 批次、商品和任务记录的内存存储。静态保存，切换页面时状态不丢；应用关闭即丢失。
/// 接入 SQLite（M2）后由数据层取代。
/// </summary>
public static class BatchStore
{
    public static ObservableCollection<CollectionBatch> Batches { get; } = [];

    public static ObservableCollection<HistoryEntry> History { get; } = [];

    private static readonly Dictionary<string, ObservableCollection<CollectedProduct>> ProductsByBatch = [];

    /// <summary>批次的商品列表。采集中由 CollectionService 逐条写入。</summary>
    public static ObservableCollection<CollectedProduct> ProductsFor(CollectionBatch batch)
    {
        if (!ProductsByBatch.TryGetValue(batch.Id, out var products))
        {
            products = [];
            ProductsByBatch[batch.Id] = products;
        }

        return products;
    }

    // 价格排序的 URL 参数没有实测确认，暂不提供（16 号"实测结果"）。
    public static IReadOnlyList<string> SortOptions { get; } = ["综合", "销量"];
}
