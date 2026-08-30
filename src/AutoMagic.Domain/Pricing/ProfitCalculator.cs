namespace AutoMagic.Domain.Pricing;

public sealed record PricingCalculationInput(
    decimal CommissionRatePercent,
    decimal TargetProfitRatePercent,
    decimal LogisticsCostCny,
    decimal AdvertisingCostCny,
    decimal OtherCostCny,
    decimal SalePriceMinimumCny,
    decimal SalePriceMaximumCny);

public sealed record CostRange(
    decimal CommissionMinimumCny,
    decimal CommissionMaximumCny,
    decimal TargetProfitMinimumCny,
    decimal TargetProfitMaximumCny,
    decimal ProcurementMinimumCny,
    decimal ProcurementMaximumCny);

public static class ProfitCalculator
{
    public static CostRange CalculateCostRange(PricingCalculationInput input)
    {
        Validate(input);

        var commissionRate = input.CommissionRatePercent / 100m;
        var targetProfitRate = input.TargetProfitRatePercent / 100m;
        var commissionMinimum = input.SalePriceMinimumCny * commissionRate;
        var commissionMaximum = input.SalePriceMaximumCny * commissionRate;
        var targetProfitMinimum = input.SalePriceMinimumCny * targetProfitRate;
        var targetProfitMaximum = input.SalePriceMaximumCny * targetProfitRate;
        var nonProcurementCosts = input.LogisticsCostCny +
                                  input.AdvertisingCostCny +
                                  input.OtherCostCny;

        return new CostRange(
            commissionMinimum,
            commissionMaximum,
            targetProfitMinimum,
            targetProfitMaximum,
            input.SalePriceMinimumCny - commissionMinimum - nonProcurementCosts - targetProfitMinimum,
            input.SalePriceMaximumCny - commissionMaximum - nonProcurementCosts - targetProfitMaximum);
    }

    private static void Validate(PricingCalculationInput input)
    {
        if (input.CommissionRatePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "平台佣金比例必须在 0% 到 100% 之间。");
        }

        if (input.TargetProfitRatePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "目标利润率必须在 0% 到 100% 之间。");
        }

        if (input.LogisticsCostCny < 0 ||
            input.AdvertisingCostCny < 0 ||
            input.OtherCostCny < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "成本金额不能为负数。");
        }

        if (input.SalePriceMinimumCny < 0 || input.SalePriceMaximumCny < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "售价不能为负数。");
        }

        if (input.SalePriceMinimumCny > input.SalePriceMaximumCny)
        {
            throw new ArgumentException("售价下限不能高于售价上限。", nameof(input));
        }
    }

}
