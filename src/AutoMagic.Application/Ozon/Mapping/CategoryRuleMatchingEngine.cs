namespace AutoMagic.Application.Ozon.Mapping;

public enum RuleProfileLayer
{
    CategoryCommon = 1,
    TypeOverride = 2,
}

public enum RuleValueScope
{
    Product,
    Variant,
}

public enum RuleValueStrategy
{
    DirectText,
    DictionaryLookup,
}

/// <summary>
/// Reusable semantic vocabulary. It never contains an Ozon category, attribute id, or product value.
/// </summary>
public sealed record CommonSemanticModule(
    string ModuleId,
    string SemanticKey,
    IReadOnlyList<string> SourceLabels);

/// <summary>
/// Binds one reusable semantic key to the actual attribute contract of a category profile.
/// </summary>
public sealed record CategoryAttributeBinding(
    string BindingId,
    string SemanticKey,
    long AttributeId,
    RuleValueScope Scope,
    RuleValueStrategy Strategy);

public sealed record CategoryRuleProfile(
    string ProfileId,
    long DescriptionCategoryId,
    RuleProfileLayer Layer,
    IReadOnlySet<long> TypeIds,
    IReadOnlyList<CategoryAttributeBinding> Bindings);

public sealed record CategoryRuleCatalog(
    string CatalogVersion,
    IReadOnlyList<CommonSemanticModule> CommonModules,
    IReadOnlyList<CategoryRuleProfile> Profiles)
{
    public static CategoryRuleCatalog Empty { get; } = new("1.0.0", [], []);
}

public sealed record CategoryRuleApplication(
    string ProfileId,
    string BindingId,
    RuleProfileLayer Layer,
    string ScopeKey,
    long AttributeId,
    string SemanticKey,
    IReadOnlyList<string> EvidenceFactIds);

public sealed record CategoryRuleGap(
    string Code,
    string ScopeKey,
    long AttributeId,
    string Message);

public sealed record CategoryRuleMatchResult(
    ProductMappingResponse Suggestions,
    IReadOnlyList<CategoryRuleApplication> Applications,
    IReadOnlyList<CategoryRuleGap> Gaps)
{
    public bool HasDeterministicSuggestions => Applications.Count > 0;

    public ProductMappingResponse MergeWith(ProductMappingResponse aiResponse)
    {
        ArgumentNullException.ThrowIfNull(aiResponse);
        var product = MergeMappings(aiResponse.ProductMappings, Suggestions.ProductMappings);
        var deterministicVariants = Suggestions.Variants.ToDictionary(variant => variant.VariantKey,
            variant => variant.Mappings, StringComparer.Ordinal);
        var variants = aiResponse.Variants.Select(variant => new ProductVariantSuggestion(
            variant.VariantKey,
            deterministicVariants.TryGetValue(variant.VariantKey, out var rules)
                ? MergeMappings(variant.Mappings, rules)
                : variant.Mappings)).ToArray();
        return aiResponse with { ProductMappings = product, Variants = variants };
    }

    private static IReadOnlyList<ProductAttributeSuggestion> MergeMappings(
        IReadOnlyList<ProductAttributeSuggestion> aiMappings,
        IReadOnlyList<ProductAttributeSuggestion> deterministicMappings)
    {
        var result = new Dictionary<(long AttributeId, string? ComplexInstanceKey), ProductAttributeSuggestion>();
        foreach (var mapping in aiMappings)
            result[(mapping.AttributeId, mapping.ComplexInstanceKey)] = mapping;
        foreach (var rule in deterministicMappings)
        {
            var key = (rule.AttributeId, rule.ComplexInstanceKey);
            if (rule.Status == ProductMappingStatuses.DictionaryPending &&
                result.TryGetValue(key, out var selected) && selected.Status == ProductMappingStatuses.Suggested)
                continue;
            result[key] = rule;
        }
        return result.Values.ToArray();
    }
}

/// <summary>
/// Deterministic category rule engine. An empty catalog is a valid learning-mode starting point.
/// Type overrides replace category bindings only for the same target attribute and scope.
/// </summary>
public sealed class CategoryRuleMatchingEngine
{
    private readonly CategoryRuleCatalog _catalog;

    public CategoryRuleMatchingEngine(CategoryRuleCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ValidateCatalog(catalog);
    }

    public CategoryRuleMatchResult Match(ProductMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modules = _catalog.CommonModules.ToDictionary(module => module.SemanticKey, StringComparer.Ordinal);
        var profiles = _catalog.Profiles
            .Where(profile => profile.DescriptionCategoryId == request.DescriptionCategoryId &&
                (profile.Layer == RuleProfileLayer.CategoryCommon || profile.TypeIds.Contains(request.TypeId)))
            .OrderBy(profile => profile.Layer)
            .ThenBy(profile => profile.ProfileId, StringComparer.Ordinal)
            .ToArray();

        var effectiveBindings = new Dictionary<(long AttributeId, RuleValueScope Scope),
            (CategoryRuleProfile Profile, CategoryAttributeBinding Binding)>();
        foreach (var profile in profiles)
        foreach (var binding in profile.Bindings)
            effectiveBindings[(binding.AttributeId, binding.Scope)] = (profile, binding);

        var productMappings = new List<ProductAttributeSuggestion>();
        var variants = request.Skus.ToDictionary(
            sku => sku.VariantKey,
            sku => new List<ProductAttributeSuggestion>(),
            StringComparer.Ordinal);
        var applications = new List<CategoryRuleApplication>();
        var gaps = new List<CategoryRuleGap>();
        var attributes = request.Attributes.ToDictionary(attribute => attribute.AttributeId);

        foreach (var entry in effectiveBindings.Values
                     .OrderBy(value => value.Binding.AttributeId)
                     .ThenBy(value => value.Binding.Scope))
        {
            var (profile, binding) = entry;
            if (!attributes.TryGetValue(binding.AttributeId, out var target))
            {
                gaps.Add(new("rule.schema_mismatch", ProductMappingInputBuilder.ProductScope,
                    binding.AttributeId, $"规则 {binding.BindingId} 的属性不在当前 Ozon Schema 中。"));
                continue;
            }
            if (!modules.TryGetValue(binding.SemanticKey, out var module))
            {
                gaps.Add(new("rule.module_missing", ProductMappingInputBuilder.ProductScope,
                    binding.AttributeId, $"规则 {binding.BindingId} 引用了不存在的公共语义模块 {binding.SemanticKey}。"));
                continue;
            }
            if (target.AttributeComplexId > 0)
            {
                gaps.Add(new("rule.complex_unsupported", ProductMappingInputBuilder.ProductScope,
                    binding.AttributeId, $"规则 {binding.BindingId} 指向复杂属性；当前规则合同尚未声明实例键策略。"));
                continue;
            }

            var scopes = binding.Scope == RuleValueScope.Product
                ? new[] { ProductMappingInputBuilder.ProductScope }
                : request.Skus.Select(sku => sku.VariantKey).ToArray();
            foreach (var scope in scopes)
            {
                var facts = request.Facts.Where(fact => fact.ScopeKey == scope &&
                    module.SourceLabels.Any(label => LabelsEqual(label, fact.Label))).ToArray();
                if (facts.Length != 1)
                {
                    gaps.Add(new(facts.Length == 0 ? "rule.evidence_missing" : "rule.evidence_ambiguous",
                        scope, binding.AttributeId,
                        facts.Length == 0
                            ? $"规则 {binding.BindingId} 没有找到来源事实。"
                            : $"规则 {binding.BindingId} 找到多个来源事实，不能自动选择。"));
                    continue;
                }

                var fact = facts[0];
                var status = binding.Strategy == RuleValueStrategy.DictionaryLookup
                    ? ProductMappingStatuses.DictionaryPending
                    : ProductMappingStatuses.Suggested;
                var values = binding.Strategy == RuleValueStrategy.DictionaryLookup
                    ? new[] { new ProductMappingValue(fact.Value, null) }
                    : new[] { new ProductMappingValue(fact.Value, null) };
                var suggestion = new ProductAttributeSuggestion(target.AttributeId, null, status, values,
                    [fact.FactId], $"由规则 {binding.BindingId} 根据原始事实确定。");
                if (binding.Scope == RuleValueScope.Product) productMappings.Add(suggestion);
                else variants[scope].Add(suggestion);
                applications.Add(new(profile.ProfileId, binding.BindingId, profile.Layer, scope,
                    binding.AttributeId, binding.SemanticKey, [fact.FactId]));
            }
        }

        return new(new(request.RequestId,
                productMappings,
                request.Skus.Select(sku => new ProductVariantSuggestion(sku.VariantKey, variants[sku.VariantKey])).ToArray(),
                []),
            applications,
            gaps);
    }

    public static void ValidateCatalog(CategoryRuleCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.CatalogVersion))
            throw new ArgumentException("规则目录版本不能为空。", nameof(catalog));
        EnsureUnique(catalog.CommonModules.Select(module => module.ModuleId), "公共模块 ID");
        EnsureUnique(catalog.CommonModules.Select(module => module.SemanticKey), "语义键");
        EnsureUnique(catalog.Profiles.Select(profile => profile.ProfileId), "品类规则 ID");
        foreach (var module in catalog.CommonModules)
        {
            if (string.IsNullOrWhiteSpace(module.ModuleId) || string.IsNullOrWhiteSpace(module.SemanticKey) ||
                module.SourceLabels.Count == 0 || module.SourceLabels.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"公共模块 {module.ModuleId} 定义不完整。", nameof(catalog));
        }
        foreach (var profile in catalog.Profiles)
        {
            if (profile.DescriptionCategoryId <= 0)
                throw new ArgumentException($"规则 {profile.ProfileId} 缺少有效品类 ID。", nameof(catalog));
            if (profile.Layer == RuleProfileLayer.CategoryCommon && profile.TypeIds.Count > 0)
                throw new ArgumentException($"品类通用规则 {profile.ProfileId} 不能限定 type_id。", nameof(catalog));
            if (profile.Layer == RuleProfileLayer.TypeOverride && profile.TypeIds.Count == 0)
                throw new ArgumentException($"类型补丁 {profile.ProfileId} 必须明确 type_id。", nameof(catalog));
            EnsureUnique(profile.Bindings.Select(binding => binding.BindingId), $"规则 {profile.ProfileId} 的绑定 ID");
            if (profile.Bindings.Any(binding => binding.AttributeId <= 0 || string.IsNullOrWhiteSpace(binding.SemanticKey)))
                throw new ArgumentException($"规则 {profile.ProfileId} 包含无效属性绑定。", nameof(catalog));
        }
    }

    private static bool LabelsEqual(string left, string right) =>
        string.Equals(NormalizeLabel(left), NormalizeLabel(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLabel(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character) && character is not ':' and not '：'));

    private static void EnsureUnique(IEnumerable<string> values, string name)
    {
        var duplicate = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"{name} 重复：{duplicate.Key}。");
    }
}
