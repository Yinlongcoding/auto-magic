namespace AM.Core.Pricing;

public enum SkuPriceState
{
    /// <summary>算出价格，保留。</summary>
    Priced,

    /// <summary>夹到售价上限后实际利润率低于最低利润率，丢弃。</summary>
    Dropped,

    /// <summary>算不出价格（采购价缺失、参数无效等），阻断，进待处理"价格异常"。</summary>
    Abnormal,
}

/// <summary>一个 SKU 的定价结果。售价为店铺币种的整数。</summary>
public sealed record SkuPrice(
    SkuPriceState State,
    decimal? PurchasePrice,
    decimal? TheoreticalPrice,
    decimal? FinalPrice,
    decimal? ActualMargin,
    string? Reason)
{
    internal static SkuPrice Abnormal(decimal? purchasePrice, string reason) =>
        new(SkuPriceState.Abnormal, purchasePrice, null, null, null, reason);
}

/// <summary>
/// 定价公式（设计目录 13 号，方案 A）：
/// <code>
/// 理论售价 = ⌈(采购价 + 物流成本) ÷ (1 − 佣金率 − 目标利润率) × 计算用汇率⌉
/// 最终售价 = 理论售价夹到 [售价下限, 售价上限]
/// 实际利润率 = 1 − 佣金率 − (采购价 + 物流成本) × 计算用汇率 ÷ 最终售价
/// </code>
/// 实际利润率低于最低利润率则丢弃。售价下限只抬价，不丢弃。
/// 另有选填的采购价下限：低于它的 SKU 直接丢弃，用来排除低价货（2026-10-23 用户要求）。
/// 正向定价和搜索用的采购价上限共用这一个类，保证两边用同一份参数、同一条公式。
/// </summary>
public static class PricingEngine
{
    public static SkuPrice Price(decimal? purchasePrice, PricingParameters p)
    {
        if (purchasePrice is not { } purchase || purchase <= 0)
        {
            return SkuPrice.Abnormal(purchasePrice, "采购价缺失");
        }

        // 13 号保护条件：分母非正、汇率无效、区间无效都不得输出价格
        var denominator = 1 - p.CommissionRate - p.TargetMargin;
        if (denominator <= 0)
        {
            return SkuPrice.Abnormal(purchase, "佣金率加目标利润率不小于 100%");
        }

        if (p.EffectiveRate <= 0)
        {
            return SkuPrice.Abnormal(purchase, "汇率无效");
        }

        if (p.SaleFloor <= 0 || p.SaleFloor > p.SaleCeiling)
        {
            return SkuPrice.Abnormal(purchase, "售价区间无效");
        }

        // 采购价下限：排除低价货，与售价公式无关，逐 SKU 判断
        if (p.PurchasePriceMin is { } floor && purchase < floor)
        {
            return new SkuPrice(SkuPriceState.Dropped, purchase, null, null, null,
                $"采购价 ¥{purchase:0.00} 低于下限 ¥{floor:0.00}");
        }

        var cost = purchase + p.LogisticsCost;

        // 向上取整：保证没被夹到上限时，实际利润率不低于目标利润率
        var theoretical = Math.Ceiling(cost / denominator * p.EffectiveRate);
        var final = Math.Clamp(theoretical, p.SaleFloor, p.SaleCeiling);
        var margin = 1 - p.CommissionRate - cost * p.EffectiveRate / final;

        return margin < p.MinMargin
            ? new SkuPrice(SkuPriceState.Dropped, purchase, theoretical, final, margin,
                $"实际利润率 {margin:P1} 低于最低利润率 {p.MinMargin:P0}")
            : new SkuPrice(SkuPriceState.Priced, purchase, theoretical, final, margin, null);
    }

    /// <summary>
    /// 能保留的最高采购价（人民币）：按售价上限卖，实际利润率恰好等于最低利润率。
    /// 采购价不高于它、且不低于采购价下限的 SKU 一定保留（便宜的会被抬到售价下限，利润率只会更高）。
    /// 参数下不存在能保留的采购价时返回 null。
    /// </summary>
    public static decimal? MaxPurchasePrice(PricingParameters p)
    {
        if (p.EffectiveRate <= 0 || p.SaleCeiling <= 0)
        {
            return null;
        }

        var max = p.SaleCeiling * (1 - p.CommissionRate - p.MinMargin) / p.EffectiveRate - p.LogisticsCost;
        return max > 0 ? max : null;
    }

    /// <summary>
    /// 搜索页粗筛用的采购价上限，向上取到分，宁可多采不误伤。
    /// 精确判断在详情页拿到 SKU 价格后由 <see cref="Price"/> 完成。
    /// </summary>
    public static decimal? SearchPriceCeiling(PricingParameters p) =>
        MaxPurchasePrice(p) is { } max ? Math.Ceiling(max * 100) / 100 : null;

    /// <summary>搜索页粗筛用的采购价下限，向下取到分，宁可多采不误伤。未设下限时为 null。</summary>
    public static decimal? SearchPriceFloor(PricingParameters p) =>
        p.PurchasePriceMin is { } min ? Math.Floor(min * 100) / 100 : null;
}
