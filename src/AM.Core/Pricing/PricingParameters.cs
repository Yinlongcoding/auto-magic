namespace AM.Core.Pricing;

/// <summary>
/// 一个批次的定价参数，新建采集时填写，随批次冻结（设计目录 13 号、19 号）。
/// 比率都用小数：0.15 表示 15%。成本用人民币，售价用店铺币种（currency_code）。
/// </summary>
/// <param name="CommissionRate">佣金率。</param>
/// <param name="LogisticsCost">物流成本，人民币，整批共用一个金额。</param>
/// <param name="TargetMargin">目标利润率，用来算理论售价。</param>
/// <param name="MinMargin">最低利润率。夹区间后实际利润率低于它的 SKU 丢弃。</param>
/// <param name="SalePriceMin">售价下限。理论售价低于它时抬到下限，只抬价，不用来筛选。</param>
/// <param name="SalePriceMax">售价上限。理论售价高于它时压到上限。</param>
/// <param name="ExchangeRate">汇率：1 元人民币等于多少售价币种。</param>
/// <param name="ExchangeFloat">汇率浮动（安全边际），把计算用的汇率调高，见 <see cref="EffectiveRate"/>。</param>
/// <param name="PurchasePriceMin">
/// 采购价下限（人民币），选填。用来排除质量差的低价货：搜索时作为 priceStart，详情页低于它的 SKU 丢弃。
/// 与售价公式无关；null 表示不设下限。
/// </param>
public sealed record PricingParameters(
    decimal CommissionRate,
    decimal LogisticsCost,
    decimal TargetMargin,
    decimal MinMargin,
    decimal SalePriceMin,
    decimal SalePriceMax,
    decimal ExchangeRate,
    decimal ExchangeFloat,
    decimal? PurchasePriceMin = null)
{
    /// <summary>
    /// 计算用的汇率 = 汇率 × (1 + 浮动)。售价币种结算后要换回人民币，它贬值时到手变少，
    /// 所以浮动把汇率调高、售价跟着调高，留出安全边际。
    /// </summary>
    public decimal EffectiveRate => ExchangeRate * (1 + ExchangeFloat);

    /// <summary>售价按整数使用：区间下限向上取整，上限向下取整，最终售价一定是区间内的整数。</summary>
    public decimal SaleFloor => Math.Ceiling(SalePriceMin);

    public decimal SaleCeiling => Math.Floor(SalePriceMax);

    /// <summary>检查参数能否算出价格。返回给用户看的错误说明，空表示可用。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (CommissionRate is < 0 or >= 1)
        {
            errors.Add("佣金率应在 0% 到 100% 之间。");
        }

        if (LogisticsCost < 0)
        {
            errors.Add("物流成本不能为负数。");
        }

        if (TargetMargin is < 0 or >= 1 || MinMargin is < 0 or >= 1)
        {
            errors.Add("利润率应在 0% 到 100% 之间。");
        }

        if (CommissionRate + TargetMargin >= 1)
        {
            errors.Add("佣金率加目标利润率必须小于 100%，否则算不出价格。");
        }

        if (TargetMargin < MinMargin)
        {
            errors.Add("目标利润率不能低于最低利润率，否则按目标算出的价格大多会被丢弃。");
        }

        if (SalePriceMin <= 0)
        {
            errors.Add("售价下限必须大于 0。");
        }
        else if (SaleFloor > SaleCeiling)
        {
            errors.Add("售价区间无效：下限大于上限。");
        }

        if (ExchangeRate <= 0)
        {
            errors.Add("汇率必须大于 0。");
        }

        if (ExchangeFloat is < 0 or >= 1)
        {
            errors.Add("汇率浮动应在 0% 到 100% 之间。");
        }

        if (PurchasePriceMin is <= 0)
        {
            errors.Add("采购价下限必须大于 0，不设下限请留空。");
        }

        if (errors.Count == 0)
        {
            if (PricingEngine.MaxPurchasePrice(this) is not { } max)
            {
                errors.Add("按这组参数，没有采购价能同时满足售价上限和最低利润率。请提高售价上限，或降低最低利润率、佣金率、物流成本。");
            }
            else if (PurchasePriceMin > max)
            {
                errors.Add($"采购价下限 ¥{PurchasePriceMin:0.00} 高于可采集的采购价上限 ¥{max:0.00}，没有商品能保留。");
            }
        }

        return errors;
    }
}
