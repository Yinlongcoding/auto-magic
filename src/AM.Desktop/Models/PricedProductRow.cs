using AM.Bridge;
using AM.Core.Pricing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AM.Desktop.Models;

/// <summary>
/// 提交页的一行：一个商品，点开看它保留下来的 SKU。
/// 商品多时按 SKU 平铺看不过来，所以按商品折叠，同一时间只展开一个。
/// 实测 1688 的 SKU 几乎都是"颜色 × 尺码"，价格和图片跟着颜色走，所以展开后按第一个规格（颜色）一行，
/// 其余规格（尺码）横排；价格相同只在标题行写一次，不同才写到各行。
/// </summary>
public sealed partial class PricedProductRow : ObservableObject
{
    private readonly Action<PricedProductRow> _onExpanded;

    private PricedProductRow(string title, string? thumbnailUrl, int skuCount, string summary, string priceText,
        IReadOnlyList<SkuGroupRow> groups, Action<PricedProductRow> onExpanded)
    {
        Title = title;
        ThumbnailUrl = thumbnailUrl;
        SkuCount = skuCount;
        Summary = summary;
        PriceText = priceText;
        Groups = groups;
        _onExpanded = onExpanded;
    }

    public string Title { get; }

    /// <summary>详情页主图第一张，没有主图时取第一个 SKU 的图。</summary>
    public string? ThumbnailUrl { get; }

    public int SkuCount { get; }

    /// <summary>如"68 个 SKU · 颜色 17 × 尺码 4"。</summary>
    public string Summary { get; }

    /// <summary>整个商品的价格，价格不一致时是区间。</summary>
    public string PriceText { get; }

    public IReadOnlyList<SkuGroupRow> Groups { get; }

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _onExpanded(this);
        }
    }

    /// <summary>只取定价保留的 SKU；一个都没保留时返回 null。</summary>
    public static PricedProductRow? Create(CollectedOffer offer, IReadOnlyList<SkuPrice> prices, string currency,
        Action<PricedProductRow> onExpanded)
    {
        var axes = offer.Axes;
        var priced = offer.Skus.Zip(prices)
            .Where(x => x.Second.State == SkuPriceState.Priced)
            .OrderBy(x => OrderKey(axes, x.First.OptionValues), OrderKeyComparer.Instance)
            .ToList();
        if (priced.Count == 0)
        {
            return null;
        }

        // 各项在整个商品内是否一致：一致的只在标题行写，颜色行只写不一致的那几项
        var all = priced.Select(x => x.Second).ToList();
        var varies = new PriceFields(
            all.Select(p => p.PurchasePrice).Distinct().Count() > 1,
            all.Select(p => p.FinalPrice).Distinct().Count() > 1,
            all.Select(p => p.ActualMargin).Distinct().Count() > 1,
            all.Select(ClampNote).Distinct().Count() > 1);
        var uniform = varies == default;

        var groups = priced
            .GroupBy(x => x.First.OptionValues.FirstOrDefault() ?? "默认")
            .Select(g =>
            {
                var image = axes.FirstOrDefault()?.Options.FirstOrDefault(o => o.Name == g.Key)?.ImageUrl
                    ?? g.Select(x => x.First.ImageUrl).FirstOrDefault(u => u is not null);
                var options = g.Select(x => string.Join(" / ", x.First.OptionValues.Skip(1)))
                    .Where(o => o.Length > 0)
                    .ToList();
                return new SkuGroupRow(g.Key, image, options,
                    uniform ? null : FormatPrice(g.Select(x => x.Second).ToList(), currency, varies));
            })
            .ToList();

        // 每个规格实际出现的取值数，如"颜色 17 × 尺码 4"
        var dims = axes.Select((axis, i) =>
                $"{axis.Name} {priced.Select(x => x.First.OptionValues.ElementAtOrDefault(i)).Distinct().Count()}")
            .ToList();
        var summary = dims.Count > 0 ? $"{priced.Count} 个 SKU · {string.Join(" × ", dims)}" : $"{priced.Count} 个 SKU";

        var thumbnail = offer.MainImages.FirstOrDefault() ?? priced.Select(x => x.First.ImageUrl).FirstOrDefault(u => u is not null);
        return new PricedProductRow(offer.Title, thumbnail, priced.Count, summary,
            FormatPrice(all, currency, new PriceFields(true, true, true, true)), groups, onExpanded);
    }

    /// <summary>要写出的价格项。</summary>
    private readonly record struct PriceFields(bool Purchase, bool Final, bool Margin, bool Clamp);

    /// <summary>
    /// 如"采购 ¥65 · 售价 1200 RUB · 利润 18.2% · 抬到下限"，只写 fields 选中的项，不一致时写区间。
    /// </summary>
    private static string FormatPrice(IReadOnlyList<SkuPrice> prices, string currency, PriceFields fields)
    {
        static string Range(IEnumerable<decimal> values, string format)
        {
            var list = values.ToList();
            var (min, max) = (list.Min(), list.Max());
            return min == max ? min.ToString(format) : $"{min.ToString(format)}～{max.ToString(format)}";
        }

        var parts = new List<string>();
        if (fields.Purchase)
        {
            parts.Add($"采购 ¥{Range(prices.Select(p => p.PurchasePrice!.Value), "0.##")}");
        }

        if (fields.Final)
        {
            parts.Add($"售价 {Range(prices.Select(p => p.FinalPrice!.Value), "0")} {currency}");
        }

        if (fields.Margin)
        {
            parts.Add($"利润 {Range(prices.Select(p => p.ActualMargin!.Value), "P1")}");
        }

        // 理论售价被抬到下限或压到上限时说明一下，解释利润率为什么偏离目标。各 SKU 不一致时不写。
        if (fields.Clamp && prices.Select(ClampNote).Distinct().ToList() is [{ Length: > 0 } clamp])
        {
            parts.Add(clamp);
        }

        return string.Join(" · ", parts);
    }

    private static string ClampNote(SkuPrice p) =>
        p.FinalPrice > p.TheoreticalPrice ? "抬到下限"
        : p.FinalPrice < p.TheoreticalPrice ? "压到上限"
        : "";

    /// <summary>按页面上规格选项的顺序排，找不到的排后面。</summary>
    private static int[] OrderKey(IReadOnlyList<SkuAxis> axes, IReadOnlyList<string> values) =>
        values.Select((v, i) =>
        {
            var index = i < axes.Count ? axes[i].Options.ToList().FindIndex(o => o.Name == v) : -1;
            return index < 0 ? int.MaxValue : index;
        }).ToArray();

    private sealed class OrderKeyComparer : IComparer<int[]>
    {
        public static readonly OrderKeyComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                if (x[i] != y[i])
                {
                    return x[i].CompareTo(y[i]);
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}

/// <summary>展开后的一行：一个颜色，后面横排它的尺码。PriceText 为 null 表示价格和标题行相同。</summary>
public sealed record SkuGroupRow(string Name, string? ImageUrl, IReadOnlyList<string> Options, string? PriceText);
