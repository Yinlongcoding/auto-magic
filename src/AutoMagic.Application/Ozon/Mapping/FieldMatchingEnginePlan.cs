using System.Globalization;
using System.Text;

namespace AutoMagic.Application.Ozon.Mapping;

public static class FieldMatchingEngineStatuses
{
    public const string Resolved = "resolved";
    public const string DictionaryLookupRequired = "dictionary_lookup_required";
    public const string AiRequired = "ai_required";
    public const string ConversionRequired = "conversion_required";
    public const string PolicyRequired = "policy_required";
    public const string MissingEvidence = "missing_evidence";
}

public static class DictionaryLookupStrategies
{
    public const string SearchByEvidence = "search_by_evidence";
    public const string LoadAllValues = "load_all_values";
}

public sealed record FieldMatchingEnginePlan(
    string ContractVersion,
    string MappingJobId,
    DateTimeOffset GeneratedAt,
    string SourceContract,
    FieldMatchingEngineTarget Target,
    IReadOnlyList<FieldMatchingEngineStage> Stages,
    IReadOnlyList<FieldMatchingDictionaryLookup> DictionaryLookups,
    IReadOnlyList<long> QwenAttributeIds,
    IReadOnlyList<FieldMatchingEngineAttribute> Attributes);

public sealed record FieldMatchingEngineTarget(
    string Platform,
    long? DescriptionCategoryId,
    long? TypeId,
    string? CategoryPath);

public sealed record FieldMatchingEngineStage(
    int Order,
    string Name,
    string Status);

public sealed record FieldMatchingDictionaryLookup(
    long AttributeId,
    string AttributeName,
    string Strategy,
    IReadOnlyList<string> SearchTexts);

public sealed record FieldMatchingEngineAttribute(
    long AttributeId,
    string AttributeName,
    bool IsRequired,
    long DictionaryId,
    string Status,
    IReadOnlyList<string> SourceFactIds,
    IReadOnlyList<string> SourceValues,
    IReadOnlyList<string> CandidateTextValues,
    IReadOnlyList<SemanticDictionaryCandidate> DictionaryCandidates,
    IReadOnlyList<long> SelectedDictionaryValueIds,
    string MappingMethod,
    decimal Confidence,
    string Reason)
{
    public IReadOnlyList<string> ExcludedSourceValues { get; init; } = [];
}

/// <summary>
/// Produces an auditable execution plan for the reusable field-matching engine.
/// The builder is pure: it never calls Ozon or Qwen and never mutates source facts.
/// External values are supplied explicitly, so every selected valueId can be traced
/// back to an Ozon response.
/// </summary>
public static class FieldMatchingEnginePlanBuilder
{
    private const long OzonProductTypeAttributeId = 8229;
    private const long OzonGenderAttributeId = 9163;
    private const long OzonBrandAttributeId = 31;
    private const long OzonMaterialAttributeId = 4496;
    private const long OzonProductColorAttributeId = 10096;
    private const long OzonColorNameAttributeId = 10097;
    private static readonly HashSet<long> FullDictionaryAttributeIds =
        [OzonProductTypeAttributeId, OzonGenderAttributeId];
    private static readonly HashSet<string> MaterialSourceLabels = new(StringComparer.Ordinal)
    {
        "材料", "材质", "面料", "面料名称", "主面料成分", "主面料成分2",
    };

    public static FieldMatchingEnginePlan Create(
        FieldMatchingInput input,
        AttributeCoverageReport coverage,
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>? dictionaryCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(coverage);

        var coverageById = coverage.Attributes.ToDictionary(attribute => attribute.AttributeId);
        var attributes = input.Target.Attributes.Select(attribute => ResolveAttribute(
            input,
            attribute,
            coverageById.GetValueOrDefault(attribute.AttributeId),
            GetCandidates(attribute.AttributeId, dictionaryCandidates))).ToArray();

        var lookups = attributes
            .Where(attribute => attribute.Status == FieldMatchingEngineStatuses.DictionaryLookupRequired)
            .Select(attribute => new FieldMatchingDictionaryLookup(
                attribute.AttributeId,
                attribute.AttributeName,
                FullDictionaryAttributeIds.Contains(attribute.AttributeId)
                    ? DictionaryLookupStrategies.LoadAllValues
                    : DictionaryLookupStrategies.SearchByEvidence,
                FullDictionaryAttributeIds.Contains(attribute.AttributeId)
                    ? []
                    : attribute.CandidateTextValues))
            .ToArray();

        var qwenAttributeIds = attributes
            .Where(attribute => attribute.Status == FieldMatchingEngineStatuses.AiRequired)
            .Select(attribute => attribute.AttributeId)
            .ToArray();

        return new FieldMatchingEnginePlan(
            "1.0",
            input.MappingJobId,
            DateTimeOffset.UtcNow,
            "details/XXX.json -> FieldMatchingInput/1.0",
            new FieldMatchingEngineTarget(
                input.Target.Platform,
                input.Target.DescriptionCategoryId,
                input.Target.TypeId,
                input.Target.CategoryPath),
            BuildStages(lookups.Length, qwenAttributeIds.Length),
            lookups,
            qwenAttributeIds,
            attributes);
    }

    private static FieldMatchingEngineAttribute ResolveAttribute(
        FieldMatchingInput input,
        FieldMatchingTargetAttribute target,
        AttributeResolution? resolution,
        IReadOnlyList<SemanticDictionaryCandidate> candidates)
    {
        var normalizedColorFacts = input.Source.Facts
            .Where(fact => fact.Source == "normalization:color-options")
            .ToArray();
        var isColorTarget = target.AttributeId is OzonProductColorAttributeId or OzonColorNameAttributeId;
        var facts = isColorTarget && normalizedColorFacts.Length > 0
            ? normalizedColorFacts
            : IsMaterialCollection(target)
            ? FindMaterialEvidence(input.Source.Facts)
            : FindEvidence(input.Source.Facts, resolution);
        var sourceValues = facts.Select(fact => fact.Value).Distinct(StringComparer.Ordinal).ToArray();
        var candidateTexts = isColorTarget && normalizedColorFacts.Length > 0
            ? sourceValues.SelectMany(SplitCandidateText).Distinct(StringComparer.Ordinal).ToArray()
            : BuildCandidateTexts(resolution, sourceValues);
        var excludedSourceValues = target.AttributeId == OzonProductColorAttributeId
            ? input.Source.Facts
                .Where(fact => fact.Source == "normalization:color-options-unresolved")
                .Select(fact => fact.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
        if (IsMaterialCollection(target))
        {
            candidateTexts = ExpandMaterialCandidateTexts(candidateTexts);
        }

        if (resolution is null)
        {
            return Result(target, FieldMatchingEngineStatuses.MissingEvidence, facts, candidateTexts,
                candidates, [], AttributeMatchMethods.None, 0m, "确定性阶段没有生成该目标字段的覆盖结果。");
        }

        if (resolution.Status == AttributeResolutionStatuses.Resolved)
        {
            return Result(target, FieldMatchingEngineStatuses.Resolved, facts, candidateTexts,
                candidates, [], resolution.MatchMethod ?? AttributeMatchMethods.None,
                resolution.Confidence, "确定性规则已经得到非字典目标值。", excludedSourceValues);
        }

        if (resolution.Status == AttributeResolutionStatuses.ConversionRequired)
        {
            return Result(target, FieldMatchingEngineStatuses.ConversionRequired, facts, candidateTexts,
                candidates, [], resolution.MatchMethod ?? AttributeMatchMethods.Conversion,
                resolution.Confidence, "该字段必须由版本化转换规则处理，Qwen不能直接生成最终值。");
        }

        if (IsUnknownBrandPolicy(target, resolution))
        {
            var policyTexts = new[] { "Нет бренда", "Без бренда", "No name", "无品牌" };
            if (candidates.Count == 0)
            {
                return Result(target, FieldMatchingEngineStatuses.DictionaryLookupRequired, facts,
                    policyTexts, candidates, [], AttributeMatchMethods.Policy, resolution.Confidence,
                    "源品牌为‘其他/未知’，必须先查询Ozon官方无品牌字典值。");
            }

            SemanticDictionaryCandidate? selected = null;
            var ambiguousPolicyValue = false;
            foreach (var policyText in policyTexts)
            {
                var normalizedPolicyText = Normalize(policyText);
                var matches = candidates.Where(candidate =>
                    string.Equals(Normalize(candidate.Value), normalizedPolicyText, StringComparison.Ordinal)).ToArray();
                if (matches.Length == 1)
                {
                    selected = matches[0];
                    break;
                }

                if (matches.Length > 1)
                {
                    ambiguousPolicyValue = true;
                    break;
                }
            }

            if (selected is not null)
            {
                return Result(target, FieldMatchingEngineStatuses.Resolved, facts,
                    [selected.Value], candidates, [selected.ValueId],
                    AttributeMatchMethods.Policy, 1m,
                    "源品牌为‘其他/未知’，已唯一匹配Ozon官方无品牌字典值。");
            }

            return Result(target, FieldMatchingEngineStatuses.PolicyRequired, facts, policyTexts,
                candidates, [], AttributeMatchMethods.Policy, resolution.Confidence,
                ambiguousPolicyValue
                    ? "Ozon对同一无品牌值返回多个候选，无法唯一确定valueId。"
                    : "Ozon返回的候选中没有官方无品牌值，不能自动代填品牌。");
        }

        if (resolution.Status == AttributeResolutionStatuses.PolicyRequired && target.DictionaryId <= 0)
        {
            return Result(target, FieldMatchingEngineStatuses.PolicyRequired, facts, candidateTexts,
                candidates, [], resolution.MatchMethod ?? AttributeMatchMethods.Policy,
                resolution.Confidence, "该字段依赖目标平台策略，不能从源字段直接复制。");
        }

        if (resolution.Status == AttributeResolutionStatuses.Missing &&
            !target.IsRequired &&
            facts.Count == 0)
        {
            return Result(target, FieldMatchingEngineStatuses.MissingEvidence, facts, candidateTexts,
                candidates, [], resolution.MatchMethod ?? AttributeMatchMethods.None,
                resolution.Confidence, "可选字段没有可用商品事实，不触发字典查询或Qwen调用。");
        }

        if (target.DictionaryId > 0)
        {
            if (candidates.Count == 0)
            {
                return Result(target, FieldMatchingEngineStatuses.DictionaryLookupRequired, facts,
                    candidateTexts, candidates, [], resolution.MatchMethod ?? AttributeMatchMethods.None,
                    resolution.Confidence, "必须先从Ozon取得合法valueId候选，再允许语义裁决。", excludedSourceValues);
            }

            if (target.AttributeId == OzonProductTypeAttributeId && input.Target.TypeId is > 0)
            {
                var categoryType = candidates.FirstOrDefault(candidate => candidate.ValueId == input.Target.TypeId);
                if (categoryType is not null)
                {
                    return Result(target, FieldMatchingEngineStatuses.Resolved, facts,
                        [categoryType.Value], candidates, [categoryType.ValueId],
                        SemanticMappingMethods.CategoryContext, 1m,
                        "Ozon类型字典valueId与已确认的目标typeId一致。");
                }
            }

            var exact = candidates
                .Where(candidate => candidateTexts.Any(text =>
                    string.Equals(Normalize(text), Normalize(candidate.Value), StringComparison.Ordinal)))
                .ToArray();
            if (exact.Length == 1 && (!target.IsCollection || candidateTexts.Count == 1))
            {
                return Result(target, FieldMatchingEngineStatuses.Resolved, facts,
                    [exact[0].Value], candidates, [exact[0].ValueId],
                    AttributeMatchMethods.Exact, Math.Max(0.95m, resolution.Confidence),
                    "源事实文本与Ozon合法字典值精确一致。", excludedSourceValues);
            }

            if (target.IsCollection && candidateTexts.Count > 1)
            {
                var exactPerSource = candidateTexts.Select(text => candidates.Where(candidate =>
                        string.Equals(Normalize(text), Normalize(candidate.Value), StringComparison.Ordinal)).ToArray())
                    .ToArray();
                if (exactPerSource.All(matches => matches.Length == 1))
                {
                    var selected = exactPerSource.Select(matches => matches[0])
                        .GroupBy(candidate => candidate.ValueId)
                        .Select(group => group.First())
                        .ToArray();
                    if (target.MaxValueCount <= 0 || selected.Length <= target.MaxValueCount)
                    {
                        return Result(target, FieldMatchingEngineStatuses.Resolved, facts,
                            selected.Select(candidate => candidate.Value).ToArray(), candidates,
                            selected.Select(candidate => candidate.ValueId).ToArray(),
                            AttributeMatchMethods.Exact, Math.Max(0.95m, resolution.Confidence),
                            "每个源集合值都唯一精确匹配到Ozon合法字典值。", excludedSourceValues);
                    }
                }
            }

            var controlled = ResolveControlledDictionaryCandidate(target, facts, candidates);
            if (controlled is not null)
            {
                return Result(target, FieldMatchingEngineStatuses.Resolved, facts,
                    [controlled.Value], candidates, [controlled.ValueId],
                    AttributeMatchMethods.Alias, Math.Max(0.98m, resolution.Confidence),
                    "源事实命中受控枚举词表，并唯一对应到Ozon合法字典值。");
            }

            return Result(target, FieldMatchingEngineStatuses.AiRequired, facts, candidateTexts,
                candidates, [], resolution.MatchMethod ?? AttributeMatchMethods.None,
                resolution.Confidence, "合法字典候选已经准备完成，需要Qwen在候选范围内进行语义判断。", excludedSourceValues);
        }

        if (resolution.Status is AttributeResolutionStatuses.ReviewRequired or AttributeResolutionStatuses.Missing)
        {
            var status = target.IsRequired || facts.Count > 0
                ? FieldMatchingEngineStatuses.AiRequired
                : FieldMatchingEngineStatuses.MissingEvidence;
            return Result(target, status, facts, candidateTexts, candidates, [],
                resolution.MatchMethod ?? AttributeMatchMethods.None, resolution.Confidence,
                status == FieldMatchingEngineStatuses.AiRequired
                    ? "确定性规则无法完成可靠映射，交由Qwen进行受约束语义判断。"
                    : "可选字段没有可用商品事实。");
        }

        return Result(target, FieldMatchingEngineStatuses.AiRequired, facts, candidateTexts,
            candidates, [], resolution.MatchMethod ?? AttributeMatchMethods.None,
            resolution.Confidence, "确定性阶段尚未得到最终值。");
    }

    private static IReadOnlyList<FieldMatchingSourceFact> FindEvidence(
        IReadOnlyList<FieldMatchingSourceFact> facts,
        AttributeResolution? resolution)
    {
        if (resolution?.SourceLabel is null || resolution.SourceValue is null)
        {
            return [];
        }

        var exact = facts.Where(fact =>
                string.Equals(fact.Label, resolution.SourceLabel, StringComparison.Ordinal) &&
                string.Equals(fact.Value, resolution.SourceValue, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length > 0) return exact;

        // A normalized/derived value may intentionally have a different label
        // than its raw ancestor (for example 产地=浙江 -> 原产国=China).
        return facts.Where(fact =>
                string.Equals(fact.Value, resolution.SourceValue, StringComparison.Ordinal) &&
                string.Equals(fact.Source, resolution.Source, StringComparison.Ordinal))
            .ToArray();
    }

    private static bool IsMaterialCollection(FieldMatchingTargetAttribute target) =>
        target.IsCollection &&
        (target.AttributeId == OzonMaterialAttributeId ||
         Normalize(target.Name) is "面料主要成分" or "面料成分" or "材料" or "材质" or "material" or "материал");

    private static IReadOnlyList<FieldMatchingSourceFact> FindMaterialEvidence(
        IReadOnlyList<FieldMatchingSourceFact> facts) =>
        facts.Where(fact => MaterialSourceLabels.Contains(fact.Label.Trim())).ToArray();

    private static bool IsUnknownBrandPolicy(
        FieldMatchingTargetAttribute target,
        AttributeResolution resolution) =>
        target.AttributeId == OzonBrandAttributeId &&
        resolution.SourceValue is not null &&
        Normalize(resolution.SourceValue) is "其他" or "其它" or "other" or "unknown";

    private static SemanticDictionaryCandidate? ResolveControlledDictionaryCandidate(
        FieldMatchingTargetAttribute target,
        IReadOnlyList<FieldMatchingSourceFact> facts,
        IReadOnlyList<SemanticDictionaryCandidate> candidates)
    {
        if (target.AttributeId != OzonGenderAttributeId || candidates.Count == 0) return null;

        var source = string.Join(" ", facts.Select(fact => fact.Value));
        string[] wanted;
        if (ContainsAny(source, "女童", "女孩", "儿童女"))
        {
            wanted = ["девочки", "для девочек", "女孩", "女童"];
        }
        else if (ContainsAny(source, "男童", "男孩", "儿童男"))
        {
            wanted = ["мальчики", "для мальчиков", "男孩", "男童"];
        }
        else if (ContainsAny(source, "男女", "中性", "通用", "унисекс"))
        {
            wanted = ["унисекс", "男女通用", "中性"];
        }
        else if (ContainsAny(source, "女性", "女士", "女装", "女款") ||
                 facts.Any(fact => fact.Label == "适用性别" && fact.Value.Trim() == "女"))
        {
            wanted = ["женский", "для женщин", "女性", "女"];
        }
        else if (ContainsAny(source, "男性", "男士", "男装", "男款") ||
                 facts.Any(fact => fact.Label == "适用性别" && fact.Value.Trim() == "男"))
        {
            wanted = ["мужской", "для мужчин", "男性", "男"];
        }
        else
        {
            return null;
        }

        var normalizedWanted = wanted.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var matches = candidates.Where(candidate => normalizedWanted.Contains(Normalize(candidate.Value))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> BuildCandidateTexts(
        AttributeResolution? resolution,
        IReadOnlyList<string> sourceValues)
    {
        var values = new List<string>(sourceValues);
        if (!string.IsNullOrWhiteSpace(resolution?.SourceValue) &&
            !values.Contains(resolution.SourceValue, StringComparer.Ordinal))
        {
            values.Add(resolution.SourceValue);
        }

        return values
            .SelectMany(SplitCandidateText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ExpandMaterialCandidateTexts(IReadOnlyList<string> values)
    {
        var expanded = new List<string>(values);
        foreach (var value in values)
        {
            AddParentheticalValues(expanded, value, '（', '）');
            AddParentheticalValues(expanded, value, '(', ')');
            foreach (var modifier in new[] { "弹力", "复合", "混纺" })
            {
                if (value.StartsWith(modifier, StringComparison.Ordinal) && value.Length > modifier.Length)
                {
                    expanded.Add(value[modifier.Length..]);
                }
            }
        }

        return expanded.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AddParentheticalValues(List<string> result, string value, char opening, char closing)
    {
        var start = value.IndexOf(opening);
        var end = value.IndexOf(closing, start + 1);
        if (start < 0 || end <= start) return;
        result.Add(value[..start].Trim());
        result.Add(value[(start + 1)..end].Trim());
    }

    private static IEnumerable<string> SplitCandidateText(string value) =>
        value.Split([',', '，', '、', ';', '；', '/', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<SemanticDictionaryCandidate> GetCandidates(
        long attributeId,
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>? candidates) =>
        candidates is not null && candidates.TryGetValue(attributeId, out var values)
            ? values.Where(value => value.ValueId > 0 && !string.IsNullOrWhiteSpace(value.Value))
                .GroupBy(value => value.ValueId)
                .Select(group => group.First())
                .ToArray()
            : [];

    private static FieldMatchingEngineAttribute Result(
        FieldMatchingTargetAttribute target,
        string status,
        IReadOnlyList<FieldMatchingSourceFact> facts,
        IReadOnlyList<string> candidateTexts,
        IReadOnlyList<SemanticDictionaryCandidate> candidates,
        IReadOnlyList<long> selectedValueIds,
        string mappingMethod,
        decimal confidence,
        string reason,
        IReadOnlyList<string>? excludedSourceValues = null) =>
        new(
            target.AttributeId,
            target.Name,
            target.IsRequired,
            target.DictionaryId,
            status,
            facts.Select(fact => fact.FactId).ToArray(),
            facts.Select(fact => fact.Value).Distinct(StringComparer.Ordinal).ToArray(),
            candidateTexts,
            candidates,
            selectedValueIds,
            mappingMethod,
            confidence,
            reason)
        {
            ExcludedSourceValues = excludedSourceValues ?? [],
        };

    private static IReadOnlyList<FieldMatchingEngineStage> BuildStages(int lookupCount, int qwenCount) =>
    [
        new(1, "normalize_source_facts", "completed"),
        new(2, "deterministic_matching", "completed"),
        new(3, "ozon_dictionary_lookup", lookupCount == 0 ? "completed" : "pending"),
        new(4, "qwen_semantic_arbitration", qwenCount == 0 ? "not_required" : "pending"),
        new(5, "final_validation", lookupCount == 0 && qwenCount == 0 ? "ready" : "blocked"),
    ];

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
