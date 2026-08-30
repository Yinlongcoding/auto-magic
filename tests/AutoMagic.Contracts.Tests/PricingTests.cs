using AutoMagic.Domain.Pricing;

namespace AutoMagic.Contracts.Tests;

public sealed class PricingTests
{
    [Fact]
    public void CalculateCostRange_ReturnsCommissionAndDynamicProcurementBounds()
    {
        var input = new PricingCalculationInput(
            CommissionRatePercent: 10,
            TargetProfitRatePercent: 10,
            LogisticsCostCny: 10,
            AdvertisingCostCny: 5,
            OtherCostCny: 5,
            SalePriceMinimumCny: 100,
            SalePriceMaximumCny: 150);

        var result = ProfitCalculator.CalculateCostRange(input);

        Assert.Equal(10, result.CommissionMinimumCny);
        Assert.Equal(15, result.CommissionMaximumCny);
        Assert.Equal(10, result.TargetProfitMinimumCny);
        Assert.Equal(15, result.TargetProfitMaximumCny);
        Assert.Equal(60, result.ProcurementMinimumCny);
        Assert.Equal(100, result.ProcurementMaximumCny);
    }

    [Theory]
    [InlineData("19.90", 19.90)]
    [InlineData("¥ 19.90 起", 19.90)]
    [InlineData("19.90 - 25.80", 19.90)]
    [InlineData("1,299.00", 1299.00)]
    [InlineData("19,90", 19.90)]
    public void TryParseMinimum_ReadsTheFirstCnyPrice(string source, decimal expected)
    {
        var success = CnyPriceParser.TryParseMinimum(source, out var actual);

        Assert.True(success);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CalculateCostRange_RejectsCommissionAboveOneHundredPercent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProfitCalculator.CalculateCostRange(new PricingCalculationInput(
                101, 10, 0, 0, 0, 1, 2)));
    }

}
