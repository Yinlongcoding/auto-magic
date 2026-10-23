using AM.Core.Pricing;
using AM.Desktop.Models;

namespace AM.Desktop.Services;

/// <summary>
/// 采集完成后给批次里每个 SKU 定价（设计目录 13 号，方案 A），结果写回商品和批次计数。
/// 参数取批次冻结的那一份，和搜索时粗筛用的是同一份。
/// </summary>
public static class BatchPricing
{
    public static void Apply(CollectionBatch batch)
    {
        var priced = 0;
        var dropped = 0;
        var blockingProducts = 0;
        var allDroppedProducts = 0;

        foreach (var product in BatchStore.ProductsFor(batch))
        {
            if (product.Offer is not { } offer)
            {
                continue;
            }

            var prices = offer.Skus.Select(sku => PricingEngine.Price(sku.PurchasePrice, batch.Pricing)).ToList();
            product.Prices = prices;

            var kept = prices.Count(p => p.State == SkuPriceState.Priced);
            var droppedHere = prices.Count(p => p.State == SkuPriceState.Dropped);
            priced += kept;
            dropped += droppedHere;

            // 价格异常按商品阻断：放弃动作是商品级的（19 号）
            if (prices.Any(p => p.State == SkuPriceState.Abnormal))
            {
                blockingProducts++;
            }
            else if (kept == 0)
            {
                allDroppedProducts++;
            }

            if (droppedHere > 0)
            {
                var reason = prices.First(p => p.State == SkuPriceState.Dropped).Reason;
                BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "价格丢弃",
                    $"{offer.Title}：丢弃 {droppedHere}/{prices.Count} 个 SKU（{reason}）"));
            }
        }

        batch.SkuCount = priced;
        batch.DroppedSkuCount = dropped;
        batch.BlockingCount = blockingProducts;

        var detail = $"保留 {priced} 个 SKU，丢弃 {dropped} 个";
        if (allDroppedProducts > 0)
        {
            detail += $"，{allDroppedProducts} 个商品的 SKU 全部丢弃";
        }

        if (blockingProducts > 0)
        {
            detail += $"，{blockingProducts} 个商品价格异常";
        }

        BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "定价完成", detail));
    }
}
