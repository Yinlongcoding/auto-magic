using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoMagic.Application.Ozon.Mapping;

public static class ProductMappingInputBuilder
{
    public const string ProductScope = "product";
    public const string ContractVersion = "2.1";

    public static ProductMappingInput Create(FieldMatchingInput input, string? requestId = null)
    {
        if (!input.Target.CategoryAndTypeConfirmedByUser ||
            input.Target.DescriptionCategoryId is not > 0 || input.Target.TypeId is not > 0 ||
            input.Target.Attributes.Count == 0)
            throw new ArgumentException("请先确认 Ozon 类目和类型，并读取对应 Schema。");
        if (string.IsNullOrWhiteSpace(input.ProductRef.OfferId))
            throw new ArgumentException("源商品标识缺失，不能建立单商品映射请求。");
        if (input.Target.Attributes.Any(a => a.AttributeId <= 0) ||
            input.Target.Attributes.Select(a => a.AttributeId).Distinct().Count() != input.Target.Attributes.Count)
            throw new ArgumentException("Ozon Schema 包含无效或重复的属性标识。");

        var issues = new List<ProductMappingIssue>();
        if (input.ProductRef.CaptureStatus != "success")
            issues.Add(new("warning", "source.partial", ProductScope, null,
                $"详情采集状态为 {input.ProductRef.CaptureStatus}，仅使用已取得的证据；缺少的信息不会补造。"));
        // Legacy normalized facts may contain policy defaults. Only original observations enter V2.
        var facts = input.Source.Facts
            .Where(f => f.Kind is not ("derived" or "unresolved") && f.DerivedFromFactIds.Count == 0 &&
                !f.Source.StartsWith("inference:", StringComparison.Ordinal) &&
                !f.Source.StartsWith("normalization:", StringComparison.Ordinal) &&
                !f.Source.StartsWith("policy:", StringComparison.Ordinal) &&
                !f.Source.StartsWith("conversion:", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(f.Label) && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new ProductMappingFact($"product:{f.FactId}", ProductScope,
                f.Label, f.Value, f.Source, f.SourcePath)).ToList();
        if (!string.IsNullOrWhiteSpace(input.Source.Title) && !facts.Any(f => f.Value == input.Source.Title))
            facts.Add(new("product:title", ProductScope, "商品标题", input.Source.Title,
                "collected-title", "$.source.title"));
        if (facts.Select(f => f.FactId).Distinct().Count() != facts.Count)
            throw new ArgumentException("源事实标识重复，不能建立证据关联。");
        issues.AddRange(FindCategoryCompatibilityIssues(input.Target.CategoryPath, facts));

        var combinations = input.Source.SkuCombinations;
        var repeatedKeys = combinations.GroupBy(s => s.CombinationKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var repeatedIds = combinations.Where(s => !string.IsNullOrWhiteSpace(s.SkuId))
            .GroupBy(s => s.SkuId!, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var observedOptions = combinations.Select(ReadObservedOptions).ToArray();
        var optionMaps = observedOptions.Select(options => options
            .OrderBy(option => option.Name, StringComparer.Ordinal)
            .ToDictionary(option => option.Name, option => option.Value, StringComparer.Ordinal)).ToArray();
        var dimensionIds = input.Source.SkuDimensions
            .GroupBy(dimension => dimension.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(dimension => dimension.SourceDimensionId)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
        var identities = new Dictionary<int, ProductVariantIdentity>();
        for (var index = 0; index < combinations.Count; index++)
        {
            var sku = combinations[index];
            var options = observedOptions[index];
            var confirmed = sku.Verification is "structured-json" or "dom-interaction" or "verified";
            var validOptions = options.Count > 0 && options.All(option =>
                !string.IsNullOrWhiteSpace(option.Name) && !string.IsNullOrWhiteSpace(option.Value)) &&
                options.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count() == options.Count &&
                input.Source.SkuDimensions.All(dimension => options.Any(o => o.Name == dimension.Name));
            if (confirmed && validOptions && !string.IsNullOrWhiteSpace(sku.CombinationKey) &&
                !repeatedKeys.Contains(sku.CombinationKey) &&
                (sku.SkuId is null || !repeatedIds.Contains(sku.SkuId)))
            {
                identities[index] = ProductVariantIdentityFactory.Create(input.ProductRef.OfferId, sku.SkuId,
                    optionMaps[index],
                    sku.OptionIds,
                    dimensionIds);
            }
        }
        var repeatedVariantKeys = identities.Values.GroupBy(identity => identity.VariantKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var repeatedMerchantSkus = identities.Values.GroupBy(identity => identity.MerchantSku, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var skus = new List<ProductMappingSku>();
        for (var index = 0; index < combinations.Count; index++)
        {
            var sku = combinations[index];
            var options = observedOptions[index];
            if (!identities.TryGetValue(index, out var identity) ||
                repeatedVariantKeys.Contains(identity.VariantKey) ||
                repeatedMerchantSkus.Contains(identity.MerchantSku))
            {
                var code = identities.TryGetValue(index, out _)
                    ? "sku.identity_collision"
                    : "sku.unconfirmed";
                var message = $"源 SKU {sku.SkuId ?? sku.CombinationKey} 未确认、标识重复或规格结构不完整，仅展示问题，不生成映射。";
                issues.Add(new("warning", code, $"source-combination:{sku.CombinationKey}", null, message));
                continue;
            }
            var key = identity.VariantKey;
            var ids = new List<string>();
            var optionIndex = 0;
            foreach (var option in options.OrderBy(o => o.Name, StringComparer.Ordinal))
            {
                var factId = $"sku:{index + 1}:option:{++optionIndex}";
                ids.Add(factId);
                facts.Add(new(factId, key, option.Name, option.Value!, "verified-sku-option",
                    $"$.raw.skuCombinations[{index}].options[{JsonSerializer.Serialize(option.SourceKey)}]"));
            }
            // Zero stock does not make a real SKU imaginary; inventory decisions are outside phase 1.
            skus.Add(new(key, identity.MerchantSku, identity.IdentityStrategy, sku.CombinationKey, sku.SkuId,
                optionMaps[index], ids));
        }
        if (skus.Count == 0)
            issues.Add(new("warning", "sku.none_confirmed", ProductScope, null,
                "没有已确认的真实 SKU，本轮只生成商品公共属性建议，不推造变体。"));
        if (facts.Count == 0)
            throw new ArgumentException("没有可供映射的文本事实或已确认 SKU 规格。");

        var attributes = input.Target.Attributes.Select(a => new ProductMappingAttribute(
            a.AttributeId, a.AttributeComplexId, a.Name, a.Description, a.Type,
            a.IsCollection, a.IsRequired, a.MaxValueCount, a.DictionaryId, [])).ToArray();
        // All target definitions are visible to AI; optional values require evidence, not heuristics.
        var snapshot = JsonSerializer.Serialize(new
        {
            input.CollectionId, input.ProductRef, ProductGroupKey = $"1688:{input.ProductRef.OfferId}", facts, skus, attributes,
            input.Target.DescriptionCategoryId, input.Target.TypeId,
        }, ProductMappingJson.StrictOptions);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        var request = new ProductMappingRequest(ContractVersion,
            requestId ?? $"am-v2-{Guid.NewGuid():N}", input.CollectionId, input.ProductRef.OfferId,
            $"1688:{input.ProductRef.OfferId}",
            input.ProductRef.CapturedAt, fingerprint, input.Target.DescriptionCategoryId.Value,
            input.Target.TypeId.Value, input.Target.CategoryPath ?? string.Empty, facts, skus, attributes);
        return new(request, issues);
    }

    private static IReadOnlyList<(string Name, string? Value, string SourceKey)> ReadObservedOptions(
        FieldMatchingSkuCombination sku)
    {
        // The collector's DOM fallback uses an older color/colorSourceValue/size wire shape.
        // Adapt its field names only; keep the observed text and its exact source path.
        var hasRawColor = sku.Options.TryGetValue("colorSourceValue", out var rawColor) &&
            !string.IsNullOrWhiteSpace(rawColor);
        return sku.Options
            .Where(o => sku.Verification != "dom-interaction" ||
                (o.Key != "color" || !hasRawColor) && (o.Key != "colorSourceValue" || hasRawColor))
            .Select(o => (
                Name: sku.Verification == "dom-interaction" ? o.Key switch
                {
                    "color" or "colorSourceValue" => "颜色",
                    "size" => "尺码",
                    _ => o.Key,
                } : o.Key,
                 o.Value, SourceKey: o.Key)).ToArray();
    }

    private static IReadOnlyList<ProductMappingIssue> FindCategoryCompatibilityIssues(
        string? categoryPath, IReadOnlyList<ProductMappingFact> facts)
    {
        if (string.IsNullOrWhiteSpace(categoryPath)) return [];
        var sleeve = facts.FirstOrDefault(fact => fact.ScopeKey == ProductScope &&
            fact.Label.Trim() is "袖长" or "袖子长度");
        if (sleeve is null || string.IsNullOrWhiteSpace(sleeve.Value)) return [];

        var selected = categoryPath.Replace(" ", string.Empty, StringComparison.Ordinal);
        var observed = sleeve.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        var conflict = selected.Contains("无袖", StringComparison.Ordinal) &&
                       !observed.Contains("无袖", StringComparison.Ordinal) ||
                       selected.Contains("长袖", StringComparison.Ordinal) &&
                       (observed.Contains("短袖", StringComparison.Ordinal) || observed.Contains("无袖", StringComparison.Ordinal)) ||
                       selected.Contains("短袖", StringComparison.Ordinal) &&
                       (observed.Contains("长袖", StringComparison.Ordinal) || observed.Contains("无袖", StringComparison.Ordinal));
        if (!conflict) return [];
        return [new("error", "category.type_conflict", ProductScope, null,
            $"所选 Ozon 类型“{categoryPath}”与源事实“{sleeve.Label}：{sleeve.Value}”明显冲突，请重新选择商品类型后再映射。")];
    }

}
