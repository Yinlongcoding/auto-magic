using AM.Core.Pricing;

namespace AM.Core.Tests;

/// <summary>
/// 定价公式的行为检查。示例参数与讨论方案 A 时用的例子一致：
/// 佣金 15%、物流 ¥18、目标利润 20%、最低利润 10%、售价 800～1500、汇率 12.1、浮动 2%（计算用汇率 12.342）。
/// 期望值按公式手算。
/// </summary>
public class PricingEngineTests
{
    private static readonly PricingParameters Example = new(
        CommissionRate: 0.15m,
        LogisticsCost: 18m,
        TargetMargin: 0.20m,
        MinMargin: 0.10m,
        SalePriceMin: 800m,
        SalePriceMax: 1500m,
        ExchangeRate: 12.1m,
        ExchangeFloat: 0.02m);

    [Fact]
    public void EffectiveRateRaisesExchangeRateByFloat()
    {
        Assert.Equal(12.342m, Example.EffectiveRate);
    }

    [Fact]
    public void MidRangePriceKeepsTargetMargin()
    {
        // (50 + 18) / 0.65 × 12.342 = 1291.16… → 1292
        var price = PricingEngine.Price(50m, Example);

        Assert.Equal(SkuPriceState.Priced, price.State);
        Assert.Equal(1292m, price.TheoreticalPrice);
        Assert.Equal(1292m, price.FinalPrice);
        Assert.True(price.ActualMargin >= Example.TargetMargin);
    }

    [Fact]
    public void CheapSkuIsRaisedToSaleFloorAndKept()
    {
        // 方案 A：(6.5 + 18) / 0.65 × 12.342 = 465.2 → 466，抬到 800，利润率约 47%，保留
        var price = PricingEngine.Price(6.5m, Example);

        Assert.Equal(SkuPriceState.Priced, price.State);
        Assert.Equal(466m, price.TheoreticalPrice);
        Assert.Equal(800m, price.FinalPrice);
        Assert.InRange(price.ActualMargin!.Value, 0.47m, 0.48m);
    }

    [Fact]
    public void ExpensiveSkuIsCappedAndDroppedWhenMarginFallsBelowMinimum()
    {
        // (80 + 18) / 0.65 × 12.342 → 1861，压到 1500，利润率 1 − 0.15 − 98 × 12.342 / 1500 ≈ 4.4%，低于 10%
        var price = PricingEngine.Price(80m, Example);

        Assert.Equal(SkuPriceState.Dropped, price.State);
        Assert.Equal(1500m, price.FinalPrice);
        Assert.InRange(price.ActualMargin!.Value, 0.04m, 0.05m);
        Assert.Contains("低于最低利润率", price.Reason);
    }

    [Fact]
    public void CappedSkuIsKeptWhileMarginStaysAboveMinimum()
    {
        // (70 + 18) × 12.342 / 1500 = 0.724，利润率 12.6%，压到上限但保留
        var price = PricingEngine.Price(70m, Example);

        Assert.Equal(SkuPriceState.Priced, price.State);
        Assert.Equal(1500m, price.FinalPrice);
        Assert.InRange(price.ActualMargin!.Value, 0.12m, 0.13m);
    }

    [Fact]
    public void MaxPurchasePriceIsTheBoundaryBetweenKeptAndDropped()
    {
        // 1500 × (1 − 0.15 − 0.10) / 12.342 − 18 = 73.153…
        var max = PricingEngine.MaxPurchasePrice(Example)!.Value;
        Assert.InRange(max, 73.15m, 73.16m);

        Assert.Equal(SkuPriceState.Priced, PricingEngine.Price(73.15m, Example).State);
        Assert.Equal(SkuPriceState.Dropped, PricingEngine.Price(73.16m, Example).State);
    }

    [Fact]
    public void SearchCeilingRoundsUpToCentsSoBoundarySkusAreNotExcluded()
    {
        Assert.Equal(73.16m, PricingEngine.SearchPriceCeiling(Example));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9.99)]
    [InlineData(23.45)]
    [InlineData(55.44)]
    [InlineData(60.0)] // (60 + 18) / 0.65 × 12.342 = 1481.04 → 1482，刚好没到上限
    public void UncappedPriceNeverFallsBelowTargetMargin(double purchase)
    {
        // 向上取整的目的：没被夹到上限时，实际利润率不低于目标
        var price = PricingEngine.Price((decimal)purchase, Example);

        Assert.True(price.FinalPrice < Example.SaleCeiling);
        Assert.True(price.ActualMargin >= Example.TargetMargin, $"margin {price.ActualMargin}");
    }

    [Fact]
    public void FinalPriceIsAlwaysAnIntegerInsideTheRange()
    {
        var fractional = Example with { SalePriceMin = 799.5m, SalePriceMax = 1500.9m };

        Assert.Equal(800m, PricingEngine.Price(1m, fractional).FinalPrice);
        Assert.Equal(1500m, PricingEngine.Price(70m, fractional).FinalPrice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void MissingPurchasePriceIsAbnormal(double? purchase)
    {
        var price = PricingEngine.Price((decimal?)purchase, Example);

        Assert.Equal(SkuPriceState.Abnormal, price.State);
        Assert.Null(price.FinalPrice);
        Assert.Equal("采购价缺失", price.Reason);
    }

    [Fact]
    public void NonPositiveDenominatorIsAbnormalAndOutputsNoPrice()
    {
        var broken = Example with { CommissionRate = 0.6m, TargetMargin = 0.4m };
        var price = PricingEngine.Price(50m, broken);

        Assert.Equal(SkuPriceState.Abnormal, price.State);
        Assert.Null(price.FinalPrice);
    }

    [Fact]
    public void ExampleParametersAreValid()
    {
        Assert.Empty(Example.Validate());
    }

    [Fact]
    public void ValidationRejectsCommissionPlusTargetMarginOfOneHundredPercent()
    {
        var errors = (Example with { CommissionRate = 0.5m, TargetMargin = 0.5m }).Validate();

        Assert.Contains(errors, e => e.Contains("佣金率加目标利润率"));
    }

    [Fact]
    public void ValidationRejectsTargetMarginBelowMinimum()
    {
        var errors = (Example with { TargetMargin = 0.05m }).Validate();

        Assert.Contains(errors, e => e.Contains("目标利润率不能低于最低利润率"));
    }

    [Fact]
    public void ValidationRejectsRangeWhereNoPurchasePriceCanBeKept()
    {
        // 售价上限 100 卢布 ≈ ¥8，连物流成本都不够
        var p = Example with { SalePriceMin = 50m, SalePriceMax = 100m };

        Assert.Null(PricingEngine.MaxPurchasePrice(p));
        Assert.Contains(p.Validate(), e => e.Contains("没有采购价能同时满足"));
    }

    [Fact]
    public void SkuBelowPurchaseFloorIsDroppedWithoutPricing()
    {
        var p = Example with { PurchasePriceMin = 20m };
        var price = PricingEngine.Price(6.5m, p);

        Assert.Equal(SkuPriceState.Dropped, price.State);
        Assert.Null(price.FinalPrice);
        Assert.Contains("低于下限", price.Reason);
    }

    [Fact]
    public void SkuAtPurchaseFloorIsKept()
    {
        var p = Example with { PurchasePriceMin = 20m };

        Assert.Equal(SkuPriceState.Priced, PricingEngine.Price(20m, p).State);
    }

    [Fact]
    public void NoPurchaseFloorKeepsCheapSkus()
    {
        Assert.Null(Example.PurchasePriceMin);
        Assert.Null(PricingEngine.SearchPriceFloor(Example));
        Assert.Equal(SkuPriceState.Priced, PricingEngine.Price(1m, Example).State);
    }

    [Fact]
    public void SearchFloorRoundsDownToCentsSoBoundarySkusAreNotExcluded()
    {
        Assert.Equal(19.99m, PricingEngine.SearchPriceFloor(Example with { PurchasePriceMin = 19.999m }));
    }

    [Fact]
    public void ValidationRejectsPurchaseFloorAboveCeiling()
    {
        var errors = (Example with { PurchasePriceMin = 80m }).Validate();

        Assert.Contains(errors, e => e.Contains("高于可采集的采购价上限"));
    }

    [Fact]
    public void ValidationRejectsNonPositivePurchaseFloor()
    {
        var errors = (Example with { PurchasePriceMin = 0m }).Validate();

        Assert.Contains(errors, e => e.Contains("采购价下限必须大于 0"));
    }

    [Fact]
    public void ValidationRejectsInvertedSaleRange()
    {
        var errors = (Example with { SalePriceMin = 1500m, SalePriceMax = 800m }).Validate();

        Assert.Contains(errors, e => e.Contains("下限大于上限"));
    }
}
