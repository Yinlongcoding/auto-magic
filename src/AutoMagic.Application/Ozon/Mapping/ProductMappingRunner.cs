namespace AutoMagic.Application.Ozon.Mapping;

/// <summary>Deterministic mappings only. Unresolved values remain blank for human review.</summary>
public sealed class ProductMappingRunner
{
    private readonly IOzonDictionaryService _dictionaries;
    private readonly CategoryRuleMatchingEngine _rules;

    // Retained for source compatibility; the semantic mapper is deliberately never used.
    public ProductMappingRunner(IProductSemanticMapper mapper, IOzonDictionaryService dictionaries)
        : this(mapper, dictionaries, new CategoryRuleMatchingEngine(CategoryRuleCatalog.Empty)) { }

    public ProductMappingRunner(IProductSemanticMapper mapper, IOzonDictionaryService dictionaries,
        CategoryRuleMatchingEngine rules)
    {
        _dictionaries = dictionaries;
        _rules = rules;
    }

    public Task<ProductMappingRun> RunAsync(ProductMappingInput input,
        QwenApiCredentials aiCredentials, OzonTemporaryCredentials ozonCredentials,
        IProgress<string>? progress, CancellationToken cancellationToken) =>
        RunAsync(input, ozonCredentials, progress, cancellationToken);

    public async Task<ProductMappingRun> RunAsync(ProductMappingInput input,
        OzonTemporaryCredentials ozonCredentials, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = input.Request;
        if (input.SourceIssues.Any(issue => issue.Severity == "error"))
            return new(request, null, new(false, 0, input.SourceIssues, []), []);
        var match = _rules.Match(request);
        var issues = new List<ProductMappingIssue>(input.SourceIssues);
        issues.AddRange(match.Gaps.Select(gap => new ProductMappingIssue("warning", gap.Code,
            gap.ScopeKey, gap.AttributeId, gap.Message)));
        progress?.Report("正在执行确定性规则；无法确定的字段留空，等待人工填写/校验。");
        var candidates = request.Attributes.ToDictionary(a => a.AttributeId,
            a => a.DictionaryCandidates.ToList());
        var cache = new Dictionary<(long, string), ProductDictionaryCandidate[]>();

        async Task<ProductAttributeSuggestion> Resolve(ProductAttributeSuggestion mapping)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = request.Attributes.Single(a => a.AttributeId == mapping.AttributeId);
            if (mapping.Status == ProductMappingStatuses.Suggested && target.DictionaryId <= 0)
                return mapping;
            if (target.DictionaryId <= 0 || mapping.Values.Count != 1)
                return Manual(mapping, "规则无法确定合法值，等待人工填写/校验。");
            var text = mapping.Values[0].Text;
            var key = (target.AttributeId, text);
            if (!cache.TryGetValue(key, out var found))
            {
                try
                {
                    var values = await _dictionaries.SearchAttributeValuesAsync(ozonCredentials,
                        request.DescriptionCategoryId, request.TypeId, target.AttributeId, text, cancellationToken);
                    found = values.Where(v => v.ValueId > 0 && !string.IsNullOrWhiteSpace(v.Value))
                        .Select(v => new ProductDictionaryCandidate(v.ValueId, v.Value)).Distinct().ToArray();
                    candidates[target.AttributeId].AddRange(found);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    issues.Add(new("warning", "dictionary.query_failed", "product", target.AttributeId,
                        "字典查询失败，目标值留空，等待人工校验。"));
                    return Manual(mapping, "字典查询失败，等待人工填写/校验。");
                }
                cache[key] = found;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Never pick a fuzzy hit, translate a source value, or choose the first search result.
            var exact = found.Where(v => string.Equals(v.Text, text, StringComparison.Ordinal)).Distinct().ToArray();
            return exact.Length == 1
                ? mapping with { Status = ProductMappingStatuses.Suggested,
                    Values = [new(exact[0].Text, exact[0].ValueId)] }
                : Manual(mapping, "字典没有唯一精确命中，目标值和 valueId 留空，等待人工填写/校验。");
        }

        var product = new List<ProductAttributeSuggestion>();
        foreach (var mapping in match.Suggestions.ProductMappings) product.Add(await Resolve(mapping));
        var variants = new List<ProductVariantSuggestion>();
        foreach (var variant in match.Suggestions.Variants)
        {
            var own = new List<ProductAttributeSuggestion>();
            foreach (var mapping in variant.Mappings) own.Add(await Resolve(mapping));
            // A failed SKU rule must suppress a product-level value for that SKU.
            foreach (var gap in match.Gaps.Where(g => g.ScopeKey == variant.VariantKey))
                if (!own.Any(m => m.AttributeId == gap.AttributeId))
                    own.Add(Blank(gap.AttributeId, gap.Message));
            foreach (var attribute in request.Attributes)
                if (!own.Any(m => m.AttributeId == attribute.AttributeId) &&
                    !product.Any(m => m.AttributeId == attribute.AttributeId))
                    own.Add(Blank(attribute.AttributeId, "无可用确定性规则或证据，等待人工填写/校验。"));
            variants.Add(new(variant.VariantKey, own));
        }
        if (request.Skus.Count == 0)
            foreach (var attribute in request.Attributes)
                if (!product.Any(m => m.AttributeId == attribute.AttributeId))
                    product.Add(Blank(attribute.AttributeId, "无可用确定性规则或证据，等待人工填写/校验。"));
        request = request with { Attributes = request.Attributes.Select(a => a with
            { DictionaryCandidates = candidates[a.AttributeId].Distinct().ToArray() }).ToArray() };
        var response = new ProductMappingResponse(request.RequestId, product, variants, []);
        var validation = ProductMappingValidator.Validate(request, response, issues);
        // Invalid deterministic values are also blanked, with the original evidence retained.
        ProductAttributeSuggestion Sanitize(string scope, ProductAttributeSuggestion mapping) =>
            validation.Issues.Any(i => i.Severity == "error" && i.ScopeKey == scope && i.AttributeId == mapping.AttributeId)
                ? Manual(mapping, "规则结果未通过字段约束校验，等待人工填写/校验。") : mapping;
        response = response with
        {
            ProductMappings = response.ProductMappings.Select(m => Sanitize("product", m)).ToArray(),
            Variants = response.Variants.Select(v => v with
                { Mappings = v.Mappings.Select(m => Sanitize(v.VariantKey, m)).ToArray() }).ToArray(),
        };
        issues.AddRange(validation.Issues.Where(i => i.Severity == "error")
            .Select(i => i with { Severity = "warning", Code = "manual." + i.Code }));
        cancellationToken.ThrowIfCancellationRequested();
        return new(request, response, ProductMappingValidator.Validate(request, response, issues), []);
    }

    private static ProductAttributeSuggestion Blank(long id, string reason) =>
        new(id, null, ProductMappingStatuses.ManualRequired, [], [], reason);

    private static ProductAttributeSuggestion Manual(ProductAttributeSuggestion mapping, string reason) =>
        mapping with { Status = ProductMappingStatuses.ManualRequired, Values = [], Reason = reason };
}
