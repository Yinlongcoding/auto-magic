using AM.Bridge;

namespace AM.Desktop.Models;

/// <summary>
/// 提交预览中的商品分组（一个商品及其所有 SKU）。
/// </summary>
public sealed record ProductSubmitGroup(
    /// <summary>商品标题。</summary>
    string Title,

    /// <summary>商品来源 ID。</summary>
    string SourceOfferId,

    /// <summary>该商品下的所有 SKU 提交请求。</summary>
    IReadOnlyList<OzonProductImportRequest> Requests,

    /// <summary>SKU 数量。</summary>
    int SkuCount,

    /// <summary>价格区间摘要（最低-最高）。</summary>
    string PriceRange,

    /// <summary>该商品的警告列表。</summary>
    IReadOnlyList<SubmitWarning> Warnings
);
