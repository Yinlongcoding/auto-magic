using System.Collections.ObjectModel;
using AM.Desktop.Models;

namespace AM.Desktop.Services;

/// <summary>
/// M1 用的内存示例数据，接入 SQLite 后删除。静态保存，切换页面时状态不丢。
/// </summary>
public static class SampleData
{
    private const string Pricing = "佣金 15% · 物流 ¥18 · 目标利润 20% · 汇率 12.1（浮动 2%）";

    public static ObservableCollection<CollectionBatch> Batches { get; } =
    [
        new()
        {
            Id = "b-1005",
            Keyword = "女士针织开衫",
            CategoryPath = "服装 / 开衫",
            CreatedAt = new DateTime(2026, 10, 21, 9, 40, 0),
            PricingSummary = Pricing,
            TargetCount = 50,
            State = BatchState.Collected,
            CollectPass = 2,
            CollectedCount = 50,
            SkuCount = 168,
        },
        new()
        {
            Id = "b-1004",
            Keyword = "硅胶厨房铲",
            CategoryPath = "家居 / 厨具",
            CreatedAt = new DateTime(2026, 10, 20, 17, 5, 0),
            PricingSummary = Pricing,
            TargetCount = 50,
            State = BatchState.Listing,
            CollectPass = 2,
            CollectedCount = 48,
            SkuCount = 142,
            BlockingCount = 12,
            AbandonedCount = 2,
            CopyFailedCount = 3,
        },
        new()
        {
            Id = "b-1003",
            Keyword = "宠物牵引绳",
            CategoryPath = "宠物用品 / 牵引绳",
            CreatedAt = new DateTime(2026, 10, 19, 15, 20, 0),
            PricingSummary = Pricing,
            TargetCount = 30,
            State = BatchState.Collected,
            CollectPass = 2,
            CollectedCount = 30,
            SkuCount = 86,
        },
        new()
        {
            Id = "b-1002",
            Keyword = "陶瓷马克杯",
            CategoryPath = "家居 / 杯具",
            CreatedAt = new DateTime(2026, 10, 18, 14, 10, 0),
            PricingSummary = Pricing,
            TargetCount = 50,
            State = BatchState.Completed,
            CollectPass = 2,
            CollectedCount = 50,
            SkuCount = 120,
            SubmittedCount = 112,
            SucceededCount = 105,
        },
        new()
        {
            Id = "b-1001",
            Keyword = "车载手机支架",
            CategoryPath = "汽车用品 / 手机支架",
            CreatedAt = new DateTime(2026, 10, 18, 11, 0, 0),
            PricingSummary = Pricing,
            TargetCount = 50,
            State = BatchState.Interrupted,
            CollectPass = 1,
            CollectedCount = 17,
        },
    ];

    private static readonly Dictionary<string, ObservableCollection<CollectedProduct>> ProductsByBatch = [];

    /// <summary>
    /// 批次的商品列表。采集中的批次由 CollectionService 逐条写入（内存暂存）；
    /// 示例批次按需生成几条假数据。
    /// </summary>
    public static ObservableCollection<CollectedProduct> ProductsFor(CollectionBatch batch)
    {
        if (!ProductsByBatch.TryGetValue(batch.Id, out var products))
        {
            products = batch.State is BatchState.Interrupted or BatchState.Collecting
                ? []
                : new(Enumerable.Range(1, Math.Min(batch.CollectedCount, 8))
                    .Select(i => new CollectedProduct(i, $"{batch.Keyword} 示例商品 {i}", 2 + i % 4, "完成")));
            ProductsByBatch[batch.Id] = products;
        }

        return products;
    }

    public static IReadOnlyList<OverviewStat> Stats { get; } =
    [
        new("采集批次", "18"),
        new("采集成功率", "93%"),
        new("上架次数", "11"),
        new("上架成功率", "88%"),
    ];

    public static ObservableCollection<HistoryEntry> History { get; } =
    [
        new(new DateTime(2026, 10, 21, 9, 40, 0), "女士针织开衫", "开始采集", "目标 50 个商品"),
        new(new DateTime(2026, 10, 20, 18, 30, 0), "硅胶厨房铲", "开始上架", "142 个 SKU，12 项阻断"),
        new(new DateTime(2026, 10, 20, 17, 50, 0), "硅胶厨房铲", "第 2 遍完成", "成功 48，失败 2"),
        new(new DateTime(2026, 10, 19, 16, 0, 0), "宠物牵引绳", "采集完成", "成功 30，失败 0"),
        new(new DateTime(2026, 10, 18, 16, 45, 0), "陶瓷马克杯", "提交 112 条", "成功 105，失败 7"),
        new(new DateTime(2026, 10, 18, 11, 20, 0), "车载手机支架", "中断采集，任务结束", "已采集 17 个，全部丢弃"),
    ];

    public static IReadOnlyList<string> Categories { get; } = ["服装", "家居", "宠物用品", "汽车用品"];

    public static IReadOnlyDictionary<string, string[]> Types { get; } = new Dictionary<string, string[]>
    {
        ["服装"] = ["开衫", "毛衣", "T 恤"],
        ["家居"] = ["厨具", "杯具", "收纳"],
        ["宠物用品"] = ["牵引绳", "宠物窝"],
        ["汽车用品"] = ["手机支架", "座垫"],
    };

    public static IReadOnlyList<string> SortOptions { get; } = ["综合", "销量", "价格从低到高", "价格从高到低"];
}
