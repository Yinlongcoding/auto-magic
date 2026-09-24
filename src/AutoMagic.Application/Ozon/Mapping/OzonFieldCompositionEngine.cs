using System.Text.Json.Serialization;

namespace AutoMagic.Application.Ozon.Mapping;

public sealed record OzonImportValueDraft(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("dictionary_value_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DictionaryValueId);

public sealed record OzonImportAttributeDraft(
    [property: JsonPropertyName("complex_id")] long ComplexId,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("values")] IReadOnlyList<OzonImportValueDraft> Values);

public sealed record OzonComplexAttributeDraft(
    [property: JsonPropertyName("attributes")] IReadOnlyList<OzonImportAttributeDraft> Attributes);

public sealed record OzonImportItemDraft(
    [property: JsonPropertyName("offer_id")] string OfferId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description_category_id")] long DescriptionCategoryId,
    [property: JsonPropertyName("type_id")] long TypeId,
    [property: JsonPropertyName("attributes")] IReadOnlyList<OzonImportAttributeDraft> Attributes,
    [property: JsonPropertyName("complex_attributes")] IReadOnlyList<OzonComplexAttributeDraft> ComplexAttributes);

public sealed record OzonImportRequestDraft(
    [property: JsonPropertyName("items")] IReadOnlyList<OzonImportItemDraft> Items);

public sealed record OzonCompositionIssue(
    string Severity,
    string Code,
    string ScopeKey,
    string Message);

public sealed record OzonCompositionResult(
    OzonImportRequestDraft Request,
    IReadOnlyList<OzonCompositionIssue> Issues)
{
    public bool ReadyForSubmit => Issues.All(issue => issue.Severity != "error");
}

/// <summary>
/// Compiles validated product/SKU mappings into one Ozon import item per real source SKU.
/// Shared product mappings are copied to every item; SKU mappings override the same attribute key.
/// </summary>
public static class OzonFieldCompositionEngine
{
    public static OzonCompositionResult Compose(ProductMappingRequest request,
        ProductMappingResponse? response, ProductMappingValidation validation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validation);
        var issues = new List<OzonCompositionIssue>();
        if (!validation.ContractValid || response is null)
        {
            issues.Add(new("error", "composition.mapping_invalid", ProductMappingInputBuilder.ProductScope,
                "属性映射合同未通过，不能组合 Ozon 提交草稿。"));
            return new(new([]), issues);
        }
        if (validation.UnresolvedRequiredCount > 0)
            issues.Add(new("error", "composition.required_unresolved", ProductMappingInputBuilder.ProductScope,
                $"仍有 {validation.UnresolvedRequiredCount} 个 SKU 必填属性未解决。"));
        if (request.Skus.Count == 0)
            issues.Add(new("error", "composition.sku_missing", ProductMappingInputBuilder.ProductScope,
                "没有已确认的真实 SKU，不能构造上架商品项。"));

        var title = request.Facts.FirstOrDefault(fact => fact.ScopeKey == ProductMappingInputBuilder.ProductScope &&
            fact.Label is "商品标题" or "商品名称" or "标题")?.Value;
        if (string.IsNullOrWhiteSpace(title))
            issues.Add(new("error", "composition.title_missing", ProductMappingInputBuilder.ProductScope,
                "缺少商品标题。"));

        var productMappings = Usable(response.ProductMappings);
        var variantMappings = response.Variants.ToDictionary(variant => variant.VariantKey,
            variant => variant.Mappings, StringComparer.Ordinal);
        var attributes = request.Attributes.ToDictionary(attribute => attribute.AttributeId);
        var items = new List<OzonImportItemDraft>();
        foreach (var sku in request.Skus)
        {
            if (!variantMappings.TryGetValue(sku.VariantKey, out var ownMappings))
            {
                issues.Add(new("error", "composition.variant_missing", sku.VariantKey,
                    "映射响应缺少当前 SKU。"));
                continue;
            }
            var overrides = ownMappings.Select(m => (m.AttributeId, m.ComplexInstanceKey)).ToHashSet();
            var effective = productMappings.Where(m =>
                    !overrides.Contains((m.AttributeId, m.ComplexInstanceKey)) &&
                    !overrides.Contains((m.AttributeId, (string?)null)))
                .Concat(Usable(ownMappings))
                .GroupBy(mapping => (mapping.AttributeId, mapping.ComplexInstanceKey))
                .Select(group => group.Last())
                .ToArray();
            var plain = new List<OzonImportAttributeDraft>();
            var complex = new Dictionary<string, List<OzonImportAttributeDraft>>(StringComparer.Ordinal);
            foreach (var mapping in effective)
            {
                if (!attributes.TryGetValue(mapping.AttributeId, out var target)) continue;
                var item = new OzonImportAttributeDraft(target.AttributeComplexId, target.AttributeId,
                    mapping.Values.Select(value => new OzonImportValueDraft(value.Text, value.DictionaryValueId)).ToArray());
                if (target.AttributeComplexId <= 0) plain.Add(item);
                else
                {
                    var instance = mapping.ComplexInstanceKey ?? string.Empty;
                    if (!complex.TryGetValue(instance, out var members)) complex[instance] = members = [];
                    members.Add(item);
                }
            }
            items.Add(new(sku.MerchantSku, title ?? string.Empty, request.DescriptionCategoryId, request.TypeId,
                plain.OrderBy(attribute => attribute.Id).ToArray(),
                complex.OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new OzonComplexAttributeDraft(group.Value.OrderBy(attribute => attribute.Id).ToArray()))
                    .ToArray()));
        }

        // Phase 1 intentionally omits commercial and media fields, so this is inspectable but never submit-ready.
        issues.Add(new("error", "composition.commercial_fields_deferred", ProductMappingInputBuilder.ProductScope,
            "第一阶段尚未提供价格、库存、尺寸、重量、税率和图片字段；当前结果仅为属性组合草稿。"));
        return new(new(items), issues);
    }

    private static IReadOnlyList<ProductAttributeSuggestion> Usable(
        IReadOnlyList<ProductAttributeSuggestion> mappings) => mappings
        .Where(mapping => mapping.Status == ProductMappingStatuses.Suggested && mapping.Values.Count > 0)
        .ToArray();
}
