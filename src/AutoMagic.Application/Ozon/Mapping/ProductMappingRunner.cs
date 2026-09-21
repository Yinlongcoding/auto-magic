namespace AutoMagic.Application.Ozon.Mapping;

/// <summary>Rules-first preview: deterministic category rules win, then AI fills gaps in at most two passes.</summary>
public sealed class ProductMappingRunner
{
    private readonly IProductSemanticMapper _mapper;
    private readonly IOzonDictionaryService _dictionaries;
    private readonly CategoryRuleMatchingEngine _rules;

    public ProductMappingRunner(IProductSemanticMapper mapper, IOzonDictionaryService dictionaries)
        : this(mapper, dictionaries, new CategoryRuleMatchingEngine(CategoryRuleCatalog.Empty)) { }

    public ProductMappingRunner(IProductSemanticMapper mapper, IOzonDictionaryService dictionaries,
        CategoryRuleMatchingEngine rules)
    {
        _mapper = mapper;
        _dictionaries = dictionaries;
        _rules = rules;
    }

    public async Task<ProductMappingRun> RunAsync(ProductMappingInput input,
        QwenApiCredentials aiCredentials, OzonTemporaryCredentials ozonCredentials,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var request = input.Request;
        var ruleMatch = _rules.Match(request);
        var issues = new List<ProductMappingIssue>(input.SourceIssues);
        issues.AddRange(ruleMatch.Gaps.Select(gap => new ProductMappingIssue("warning", gap.Code,
            gap.ScopeKey, gap.AttributeId, gap.Message)));
        var calls = new List<ProductMappingCallResult>();
        cancellationToken.ThrowIfCancellationRequested();
        if (issues.Any(issue => issue.Severity == "error"))
            return new(request, null, new(false, 0, issues, []), calls);
        progress?.Report($"AI 正在为商品及 {request.Skus.Count} 个真实 SKU 提出属性建议…");
        var first = await _mapper.MapAsync(aiCredentials, request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add(first);
        issues.AddRange(first.ParseIssues);
        if (first.Response is null)
        {
            var missing = ProductMappingValidator.Validate(request, null, issues);
            return new(request, null, missing, calls);
        }
        var normalizedFirst = ProductMappingResponseNormalizer.Normalize(request, first.Response);
        issues.AddRange(normalizedFirst.Issues);
        var firstResponse = ruleMatch.MergeWith(normalizedFirst.Response);
        var validation = ProductMappingValidator.Validate(request, firstResponse, issues);
        if (firstResponse.RequestId != request.RequestId)
            return new(request, firstResponse, validation, calls);

        // Valid mappings may still provide safe lookup text even when an unrelated row is invalid.
        // Per-item evidence checks prevent an invalid envelope from driving dictionary calls.
        var pending = ProductMappingResponseNormalizer.SafeDictionaryQueries(request, firstResponse);
        if (pending.Count == 0) return new(request, firstResponse, validation, calls);

        var found = new Dictionary<long, Dictionary<long, ProductDictionaryCandidate>>();
        for (var index = 0; index < pending.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = pending[index];
            progress?.Report($"查询 Ozon 字典候选 {index + 1}/{pending.Count}…");
            try
            {
                var values = await _dictionaries.SearchAttributeValuesAsync(ozonCredentials,
                    request.DescriptionCategoryId, request.TypeId, query.AttributeId, query.Text, cancellationToken);
                if (!found.TryGetValue(query.AttributeId, out var candidates))
                    found[query.AttributeId] = candidates = [];
                foreach (var value in values.Where(v => v.ValueId > 0 && !string.IsNullOrWhiteSpace(v.Value)))
                    candidates.TryAdd(value.ValueId, new(value.ValueId, value.Value));
                if (values.Count == 0)
                    issues.Add(new("warning", "dictionary.no_candidates", "product", query.AttributeId,
                        $"文本“{query.Text}”没有查到候选；这不代表源商品事实缺失或完整字典中一定没有对应值。"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Never copy provider exceptions or credentials into a product preview.
                issues.Add(new("warning", "dictionary.query_failed", "product", query.AttributeId,
                    $"文本“{query.Text}”的字典查询失败，本轮保留未解决状态。"));
            }
        }
        if (!found.Values.Any(values => values.Count > 0))
            return new(request, firstResponse, ProductMappingValidator.Validate(request, firstResponse, issues), calls);

        request = request with
        {
            Attributes = request.Attributes.Select(a => a with
            {
                DictionaryCandidates = found.TryGetValue(a.AttributeId, out var values)
                    ? values.Values.OrderBy(v => v.ValueId).ToArray() : [],
            }).ToArray(),
        };
        progress?.Report("AI 正在根据真实字典候选完成第二轮建议，随后执行程序校验…");
        ProductMappingCallResult second;
        try
        {
            second = await _mapper.MapAsync(aiCredentials, request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A failed refinement must not discard the already validated first-pass suggestions.
            // Keep dictionary values pending: fetched candidates alone do not select a correct value.
            issues.Add(new("warning", "ai.refinement_failed", "product", null,
                "字典补充后的 AI 请求未完成，已保留首轮建议；待解析属性仍为未解决，可重新运行。"));
            return new(request, firstResponse,
                ProductMappingValidator.Validate(request, firstResponse, issues), calls);
        }
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add(second);
        issues.AddRange(second.ParseIssues);
        if (second.Response is null)
            return new(request, firstResponse,
                ProductMappingValidator.Validate(request, firstResponse, issues), calls);
        var normalizedSecond = ProductMappingResponseNormalizer.Normalize(request, second.Response);
        issues.AddRange(normalizedSecond.Issues);
        var secondResponse = ruleMatch.MergeWith(normalizedSecond.Response);
        return new(request, secondResponse,
            ProductMappingValidator.Validate(request, secondResponse, issues), calls);
    }
}
