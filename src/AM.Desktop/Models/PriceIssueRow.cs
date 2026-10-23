namespace AM.Desktop.Models;

/// <summary>
/// 待处理页"价格异常"分段的一行，对应一个有异常 SKU 的商品。
/// </summary>
public sealed record PriceIssueRow(
    CollectedProduct Product,
    string Title,
    int AbnormalCount,
    int TotalSkuCount,
    string Reason)
{
    public string Summary => $"{Title}：{AbnormalCount}/{TotalSkuCount} 个 SKU 算不出价格（{Reason}）";
}
