namespace AutoMagic.Application.Ozon.Mapping;

/// <summary>
/// Repairs only mechanical first-pass contract mistakes. It never creates facts,
/// selects dictionary values or turns an unresolved value into a suggestion.
/// </summary>
public static class ProductMappingResponseNormalizer
{
    private static readonly IReadOnlySet<long> PhaseOnePolicyAttributeIds = new HashSet<long> { 8292 };

    public static (ProductMappingResponse Response, IReadOnlyList<ProductMappingIssue> Issues) Normalize(
        ProductMappingRequest request, ProductMappingResponse response)
    {
        var issues = new List<ProductMappingIssue>();
        if (response.ProductMappings is null || response.Variants is null ||
            response.ProductMappings.Any(m => m is null || m.Values is null || m.EvidenceFactIds is null) ||
            response.Variants.Any(v => v is null || v.Mappings is null ||
                v.Mappings.Any(m => m is null || m.Values is null || m.EvidenceFactIds is null)))
            return (response, issues);
        var facts = request.Facts.ToDictionary(f => f.FactId, StringComparer.Ordinal);
        var suffixes = request.Facts.Where(f => f.FactId.StartsWith("product:", StringComparison.Ordinal))
            .GroupBy(f => f.FactId["product:".Length..], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.FactId).ToArray(), StringComparer.Ordinal);
        var attributes = request.Attributes.ToDictionary(a => a.AttributeId);

        ProductAttributeSuggestion Mapping(string scope, ProductAttributeSuggestion mapping)
        {
            var evidence = (mapping.EvidenceFactIds ?? []).Select(id =>
            {
                if (facts.ContainsKey(id)) return id;
                if (!id.Contains(':') && suffixes.TryGetValue(id, out var matches) && matches.Length == 1)
                {
                    issues.Add(new("warning", "evidence.canonicalized", scope, mapping.AttributeId,
                        $"AI 将证据写为 {id}；程序已唯一还原为 {matches[0]}。"));
                    return matches[0];
                }
                return id;
            }).ToArray();

            if (PhaseOnePolicyAttributeIds.Contains(mapping.AttributeId))
            {
                if (mapping.Status != ProductMappingStatuses.PolicyRequired || mapping.Values.Count > 0 || evidence.Length > 0)
                    issues.Add(new("warning", "policy.deferred", scope, mapping.AttributeId,
                        "商品卡分组属于后续业务策略，本阶段忽略 AI 给出的值并保留为待策略。"));
                return mapping with
                {
                    Status = ProductMappingStatuses.PolicyRequired,
                    Values = [],
                    EvidenceFactIds = [],
                    Reason = "商品卡分组属于后续业务策略，不在第一阶段决定。",
                };
            }

            if (attributes.TryGetValue(mapping.AttributeId, out var attribute) &&
                attribute.DictionaryId > 0 && attribute.DictionaryCandidates.Count == 0 &&
                mapping.Values is { Count: > 0 } &&
                mapping.Status is ProductMappingStatuses.Suggested or ProductMappingStatuses.DictionaryPending)
            {
                if (mapping.Status == ProductMappingStatuses.Suggested || mapping.Values.Any(v => v.DictionaryValueId is not null))
                    issues.Add(new("warning", "dictionary.first_pass_normalized", scope, mapping.AttributeId,
                        "首轮没有 Ozon 字典候选；已丢弃模型生成的占位 ID，仅保留文本用于查询真实候选。"));
                return mapping with
                {
                    Status = ProductMappingStatuses.DictionaryPending,
                    Values = mapping.Values.Select(v => v with { DictionaryValueId = null }).ToArray(),
                    EvidenceFactIds = evidence,
                };
            }
            return mapping with { EvidenceFactIds = evidence };
        }

        var normalized = response with
        {
            ProductMappings = (response.ProductMappings ?? []).Select(m => Mapping("product", m)).ToArray(),
            Variants = (response.Variants ?? []).Select(v => v with
            {
                Mappings = (v.Mappings ?? []).Select(m => Mapping(v.VariantKey, m)).ToArray(),
            }).ToArray(),
        };
        return (normalized, issues);
    }

    public static IReadOnlyList<(long AttributeId, string Text)> SafeDictionaryQueries(
        ProductMappingRequest request, ProductMappingResponse response)
    {
        if (response.RequestId != request.RequestId) return [];
        if (response.ProductMappings is null || response.Variants is null) return [];
        var attributes = request.Attributes.ToDictionary(a => a.AttributeId);
        var facts = request.Facts.ToDictionary(f => f.FactId, StringComparer.Ordinal);
        var skuKeys = request.Skus.Select(s => s.VariantKey).ToHashSet(StringComparer.Ordinal);
        var result = new List<(long, string)>();

        void Read(string scope, IEnumerable<ProductAttributeSuggestion> mappings)
        {
            foreach (var mapping in mappings)
            {
                if (mapping is null || mapping.Values is null || mapping.EvidenceFactIds is null) continue;
                if (!attributes.TryGetValue(mapping.AttributeId, out var target) || target.DictionaryId <= 0 ||
                    mapping.Status != ProductMappingStatuses.DictionaryPending || mapping.Values.Count == 0 ||
                    mapping.Values.Any(v => v.DictionaryValueId is not null || string.IsNullOrWhiteSpace(v.Text)) ||
                    mapping.EvidenceFactIds.Count == 0) continue;
                var evidenceIsSafe = mapping.EvidenceFactIds.All(id => facts.TryGetValue(id, out var fact) &&
                    (fact.ScopeKey == ProductMappingInputBuilder.ProductScope || fact.ScopeKey == scope));
                if (!evidenceIsSafe) continue;
                result.AddRange(mapping.Values.Select(v => (mapping.AttributeId, v.Text.Trim())));
            }
        }

        Read(ProductMappingInputBuilder.ProductScope, response.ProductMappings ?? []);
        foreach (var variant in response.Variants)
            if (variant is not null && skuKeys.Contains(variant.VariantKey) && variant.Mappings is not null)
                Read(variant.VariantKey, variant.Mappings);
        return result.Distinct().ToArray();
    }
}
