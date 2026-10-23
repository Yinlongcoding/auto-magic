namespace AM.Desktop.Models;

/// <summary>提交页的一行：保留下来的 SKU 和它的价格（19 号：价格列并入提交页 SKU 列表）。</summary>
public sealed record PricedSkuRow(
    string Title,
    string Spec,
    decimal PurchasePrice,
    decimal TheoreticalPrice,
    decimal FinalPrice,
    decimal ActualMargin)
{
    /// <summary>理论售价被抬到售价下限或压到上限时标出来，说明实际利润率为什么偏离目标。</summary>
    public string ClampNote => FinalPrice > TheoreticalPrice ? "抬到下限"
        : FinalPrice < TheoreticalPrice ? "压到上限"
        : "";
}
