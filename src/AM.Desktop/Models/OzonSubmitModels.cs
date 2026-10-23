namespace AM.Desktop.Models;

/// <summary>Ozon 商品导入请求（/v3/product/import）。</summary>
public sealed record OzonProductImportRequest(
    long DescriptionCategoryId,
    long TypeId,
    string OfferId,
    string Price,
    string Name,
    IReadOnlyList<OzonAttribute> Attributes,
    IReadOnlyList<string> Images,
    string? PrimaryImage = null,
    int? Depth = null,
    int? Width = null,
    int? Height = null,
    string? DimensionUnit = null,
    int? Weight = null,
    string? WeightUnit = null,
    string? Vat = null,
    string? CurrencyCode = "RUB",
    string? OldPrice = null
);

/// <summary>Ozon 属性。</summary>
public sealed record OzonAttribute(
    long Id,
    IReadOnlyList<OzonAttributeValue> Values
);

/// <summary>Ozon 属性值。</summary>
public sealed record OzonAttributeValue(
    long? DictionaryValueId = null,
    string? Value = null
);

/// <summary>提交预览结果（按商品分组）。</summary>
public sealed record SubmitPreview(
    /// <summary>按商品分组的提交数据。</summary>
    IReadOnlyList<ProductSubmitGroup> ProductGroups,

    /// <summary>总 SKU 数。</summary>
    int TotalSkuCount,

    /// <summary>总商品数。</summary>
    int TotalProductCount
);

/// <summary>提交警告（数据缺失或异常）。</summary>
public sealed record SubmitWarning(
    string OfferId,
    string ProductTitle,
    string Message
);
