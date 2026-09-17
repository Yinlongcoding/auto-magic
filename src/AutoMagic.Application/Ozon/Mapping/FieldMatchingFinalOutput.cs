using System.Text.Json.Serialization;

namespace AutoMagic.Application.Ozon.Mapping;

public static class FieldMatchingFinalStatuses
{
    public const string Mapped = "mapped";
    public const string DictionaryPending = "dictionary_pending";
    public const string MissingEvidence = "missing_evidence";
    public const string Ambiguous = "ambiguous";
    public const string ConversionRequired = "conversion_required";
    public const string PolicyRequired = "policy_required";
}

public static class FieldMatchingProductStatuses
{
    public const string Mapped = "mapped";
    public const string ReviewRequired = "review_required";
    public const string Blocked = "blocked";
}

public static class FieldMatchingDecisionSources
{
    public const string Deterministic = "deterministic";
    public const string Qwen = "qwen";
    public const string Conversion = "conversion";
    public const string Unresolved = "unresolved";
}

public static class FieldConversionStatuses
{
    public const string Mapped = "mapped";
    public const string Ambiguous = "ambiguous";
    public const string MissingRule = "missing_rule";
    public const string MissingDictionaryValue = "missing_dictionary_value";
    public const string Dropped = "dropped";
}

/// <summary>
/// Output of one versioned conversion strategy. Dictionary candidates must be
/// copied from Ozon after converting the source value to a target text value.
/// </summary>
public sealed record FieldConversionDecision(
    long AttributeId,
    string Status,
    IReadOnlyList<string> SourceFactIds,
    IReadOnlyList<string> TextValues,
    IReadOnlyList<SemanticDictionaryCandidate> DictionaryCandidates,
    IReadOnlyList<long> SelectedDictionaryValueIds,
    string RuleId,
    decimal Confidence,
    string Reason,
    IReadOnlyList<FieldConversionTrace> Traces);

public sealed record FieldConversionTrace(
    string ItemKey,
    string SourceValue,
    string TargetValue,
    long? DictionaryValueId,
    string Status);

public sealed record FieldMatchingFinalOutput(
    string ContractVersion,
    string MappingJobId,
    string CollectionId,
    FieldMatchingFinalProductRef ProductRef,
    string Status,
    IReadOnlyList<FieldMatchingFinalTargetMapping> TargetMappings,
    IReadOnlyList<FieldMatchingExcludedSourceOption> ExcludedSourceOptions,
    IReadOnlyList<string> UnmappedEvidenceIds,
    FieldMatchingFinalValidation Validation,
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAt);

public sealed record FieldMatchingFinalProductRef(
    string OfferId,
    int ItemPosition);

public sealed record FieldMatchingExcludedSourceOption(
    long AttributeId,
    string AttributeName,
    string ItemKey,
    string SourceValue,
    string Reason);

public sealed record FieldMatchingFinalTargetMapping(
    long AttributeId,
    string AttributeName,
    bool IsRequired,
    string Status,
    IReadOnlyList<FieldMatchingFinalEvidence> Evidence,
    IReadOnlyList<FieldMatchingFinalCandidateValue> CandidateValues,
    FieldMatchingFinalValue FinalValue,
    string MappingMethod,
    decimal Confidence,
    string DecisionSource,
    bool RequiresHumanReview,
    string Reason,
    IReadOnlyList<FieldConversionTrace> ConversionTraces)
{
    [JsonIgnore]
    public string FinalTextDisplay => string.Join("；", FinalValue.Texts);

    [JsonIgnore]
    public string FinalDictionaryValueIdDisplay => string.Join(", ", FinalValue.DictionaryValueIds);
}

public sealed record FieldMatchingFinalEvidence(
    string FactId,
    string Label,
    string Value,
    string Source,
    string SourcePath);

public sealed record FieldMatchingFinalCandidateValue(
    string Text,
    long? DictionaryValueId,
    string Info = "",
    string Picture = "");

public sealed record FieldMatchingFinalValue(
    IReadOnlyList<string> Texts,
    IReadOnlyList<long> DictionaryValueIds);

public sealed record FieldMatchingFinalValidation(
    bool SchemaValid,
    int RequiredAttributeCount,
    int MappedRequiredCount,
    int MissingRequiredCount,
    int ReviewRequiredCount,
    bool ReadyForNextStage,
    bool ReadyForListing,
    IReadOnlyList<string> BlockingReasons);

/// <summary>
/// Merges deterministic, Qwen and conversion decisions without allowing a
/// lower-authority stage to overwrite an earlier verified decision.
/// </summary>
public static class FieldMatchingFinalOutputMerger
{
    public static FieldMatchingFinalOutput Merge(
        FieldMatchingInput input,
        FieldMatchingEnginePlan plan,
        SemanticMappingResponse? qwenResponse = null,
        IReadOnlyCollection<FieldConversionDecision>? conversionResults = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(input.MappingJobId, plan.MappingJobId, StringComparison.Ordinal))
        {
            throw new ArgumentException("引擎计划与字段匹配输入不属于同一个mappingJobId。", nameof(plan));
        }

        var targets = input.Target.Attributes.ToDictionary(attribute => attribute.AttributeId);
        var facts = input.Source.Facts.ToDictionary(fact => fact.FactId, StringComparer.Ordinal);
        ValidatePlan(plan, targets, facts);
        var qwenById = IndexQwen(qwenResponse, plan, targets, facts);
        var conversionsById = IndexConversions(conversionResults, plan, targets, facts);

        var mappings = plan.Attributes.Select(attribute =>
        {
            var target = targets[attribute.AttributeId];
            if (attribute.Status == FieldMatchingEngineStatuses.Resolved)
            {
                return FromDeterministic(attribute, target, facts);
            }

            if (attribute.Status == FieldMatchingEngineStatuses.ConversionRequired &&
                conversionsById.TryGetValue(attribute.AttributeId, out var conversion))
            {
                return FromConversion(attribute, target, conversion, facts);
            }

            if (attribute.Status == FieldMatchingEngineStatuses.AiRequired &&
                qwenById.TryGetValue(attribute.AttributeId, out var qwen))
            {
                return FromQwen(attribute, target, qwen, facts);
            }

            return FromUnresolved(attribute, target, facts);
        }).ToArray();

        var referencedFacts = mappings.SelectMany(mapping => mapping.Evidence)
            .Select(evidence => evidence.FactId)
            .ToHashSet(StringComparer.Ordinal);
        var unmappedFacts = input.Source.Facts.Select(fact => fact.FactId)
            .Where(factId => !referencedFacts.Contains(factId))
            .ToArray();
        var excludedSourceOptions = mappings.SelectMany(mapping => mapping.ConversionTraces
                .Where(trace => trace.Status == FieldConversionStatuses.Dropped)
                .Select(trace => new FieldMatchingExcludedSourceOption(
                    mapping.AttributeId,
                    mapping.AttributeName,
                    trace.ItemKey,
                    trace.SourceValue,
                    "没有可靠目标值，后续生成商品变体时必须排除包含该源选项的SKU。")))
            .Concat(plan.Attributes.SelectMany(attribute => attribute.ExcludedSourceValues.Select((value, index) =>
                new FieldMatchingExcludedSourceOption(
                    attribute.AttributeId,
                    attribute.AttributeName,
                    $"attribute:{attribute.AttributeId}:excluded:{index + 1}",
                    value,
                    "规范化后没有可用颜色名称，后续生成商品变体时必须排除包含该源选项的SKU。"))))
            .ToArray();
        var validation = BuildValidation(mappings);
        var status = validation.MissingRequiredCount > 0
            ? FieldMatchingProductStatuses.Blocked
            : validation.ReviewRequiredCount > 0
                ? FieldMatchingProductStatuses.ReviewRequired
                : FieldMatchingProductStatuses.Mapped;
        var warnings = (qwenResponse?.Warnings ?? [])
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Concat(mappings.SelectMany(mapping => mapping.ConversionTraces
                .Where(trace => trace.Status == FieldConversionStatuses.Dropped)
                .Select(trace =>
                    $"{mapping.AttributeName}：源选项“{trace.SourceValue}”没有可靠目标值，已按策略舍弃。")))
            .Concat(plan.Attributes.SelectMany(attribute => attribute.ExcludedSourceValues.Select(value =>
                $"{attribute.AttributeName}：源选项“{value}”规范化后没有可用颜色名称，已按策略舍弃。")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new FieldMatchingFinalOutput(
            "1.0",
            input.MappingJobId,
            input.CollectionId,
            new FieldMatchingFinalProductRef(input.ProductRef.OfferId, input.ProductRef.ItemPosition),
            status,
            mappings,
            excludedSourceOptions,
            unmappedFacts,
            validation,
            warnings,
            DateTimeOffset.UtcNow);
    }

    private static FieldMatchingFinalTargetMapping FromDeterministic(
        FieldMatchingEngineAttribute attribute,
        FieldMatchingTargetAttribute target,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts)
    {
        var texts = ResolveTexts(attribute.CandidateTextValues, attribute.DictionaryCandidates,
            attribute.SelectedDictionaryValueIds);
        return Mapping(
            attribute,
            target,
            FieldMatchingFinalStatuses.Mapped,
            Evidence(attribute.SourceFactIds, facts),
            Candidates(attribute.CandidateTextValues, attribute.DictionaryCandidates),
            new FieldMatchingFinalValue(texts, attribute.SelectedDictionaryValueIds),
            attribute.MappingMethod,
            attribute.Confidence,
            FieldMatchingDecisionSources.Deterministic,
            false,
            attribute.Reason,
            []);
    }

    private static FieldMatchingFinalTargetMapping FromQwen(
        FieldMatchingEngineAttribute attribute,
        FieldMatchingTargetAttribute target,
        SemanticTargetMapping qwen,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts)
    {
        var status = qwen.Status;
        var mapped = status == SemanticMappingStatuses.Mapped;
        var texts = ResolveTexts(qwen.CandidateTextValues, attribute.DictionaryCandidates,
            qwen.SelectedDictionaryValueIds);
        return Mapping(
            attribute,
            target,
            status,
            Evidence(qwen.SourceFactIds, facts),
            Candidates(qwen.CandidateTextValues, attribute.DictionaryCandidates),
            new FieldMatchingFinalValue(mapped ? texts : [], mapped ? qwen.SelectedDictionaryValueIds : []),
            qwen.MappingMethod,
            qwen.Confidence,
            FieldMatchingDecisionSources.Qwen,
            !mapped,
            qwen.Reason,
            []);
    }

    private static FieldMatchingFinalTargetMapping FromConversion(
        FieldMatchingEngineAttribute attribute,
        FieldMatchingTargetAttribute target,
        FieldConversionDecision conversion,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts)
    {
        var mapped = conversion.Status == FieldConversionStatuses.Mapped;
        var status = conversion.Status switch
        {
            FieldConversionStatuses.Mapped => FieldMatchingFinalStatuses.Mapped,
            FieldConversionStatuses.Ambiguous => FieldMatchingFinalStatuses.Ambiguous,
            _ => FieldMatchingFinalStatuses.ConversionRequired,
        };
        var texts = ResolveTexts(conversion.TextValues, conversion.DictionaryCandidates,
            conversion.SelectedDictionaryValueIds);
        return Mapping(
            attribute,
            target,
            status,
            Evidence(conversion.SourceFactIds, facts),
            Candidates(conversion.TextValues, conversion.DictionaryCandidates),
            new FieldMatchingFinalValue(mapped ? texts : [], mapped ? conversion.SelectedDictionaryValueIds : []),
            conversion.RuleId,
            conversion.Confidence,
            FieldMatchingDecisionSources.Conversion,
            !mapped,
            conversion.Reason,
            conversion.Traces);
    }

    private static FieldMatchingFinalTargetMapping FromUnresolved(
        FieldMatchingEngineAttribute attribute,
        FieldMatchingTargetAttribute target,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts)
    {
        var status = attribute.Status switch
        {
            FieldMatchingEngineStatuses.DictionaryLookupRequired => FieldMatchingFinalStatuses.DictionaryPending,
            FieldMatchingEngineStatuses.ConversionRequired => FieldMatchingFinalStatuses.ConversionRequired,
            FieldMatchingEngineStatuses.PolicyRequired => FieldMatchingFinalStatuses.PolicyRequired,
            FieldMatchingEngineStatuses.MissingEvidence => FieldMatchingFinalStatuses.MissingEvidence,
            _ when attribute.SourceFactIds.Count > 0 => FieldMatchingFinalStatuses.Ambiguous,
            _ => FieldMatchingFinalStatuses.MissingEvidence,
        };
        return Mapping(
            attribute,
            target,
            status,
            Evidence(attribute.SourceFactIds, facts),
            Candidates(attribute.CandidateTextValues, attribute.DictionaryCandidates),
            new FieldMatchingFinalValue([], []),
            attribute.MappingMethod,
            attribute.Confidence,
            FieldMatchingDecisionSources.Unresolved,
            true,
            attribute.Reason,
            []);
    }

    private static FieldMatchingFinalTargetMapping Mapping(
        FieldMatchingEngineAttribute attribute,
        FieldMatchingTargetAttribute target,
        string status,
        IReadOnlyList<FieldMatchingFinalEvidence> evidence,
        IReadOnlyList<FieldMatchingFinalCandidateValue> candidates,
        FieldMatchingFinalValue finalValue,
        string method,
        decimal confidence,
        string decisionSource,
        bool requiresReview,
        string reason,
        IReadOnlyList<FieldConversionTrace> conversionTraces) =>
        new(
            target.AttributeId,
            target.Name,
            target.IsRequired,
            status,
            evidence,
            candidates,
            finalValue,
            method,
            confidence,
            decisionSource,
            requiresReview,
            reason,
            conversionTraces);

    private static FieldMatchingFinalValidation BuildValidation(
        IReadOnlyList<FieldMatchingFinalTargetMapping> mappings)
    {
        var required = mappings.Where(mapping => mapping.IsRequired).ToArray();
        var mapped = required.Count(mapping => mapping.Status == FieldMatchingFinalStatuses.Mapped);
        var missing = required.Count(mapping => mapping.Status == FieldMatchingFinalStatuses.MissingEvidence);
        var review = required.Length - mapped - missing;
        var blocking = required
            .Where(mapping => mapping.Status != FieldMatchingFinalStatuses.Mapped)
            .Select(mapping => $"{mapping.AttributeName}({mapping.AttributeId})：{mapping.Status}")
            .ToList();
        var ready = required.Length > 0 && mapped == required.Length;
        if (ready)
        {
            blocking.Add("字段映射已完成；自动上架前仍需独立校验结构化SKU、价格、图片和库存。");
        }

        return new FieldMatchingFinalValidation(
            true,
            required.Length,
            mapped,
            missing,
            review,
            ready,
            false,
            blocking);
    }

    private static IReadOnlyDictionary<long, SemanticTargetMapping> IndexQwen(
        SemanticMappingResponse? response,
        FieldMatchingEnginePlan plan,
        IReadOnlyDictionary<long, FieldMatchingTargetAttribute> targets,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts)
    {
        if (response is null)
        {
            return new Dictionary<long, SemanticTargetMapping>();
        }

        var result = new Dictionary<long, SemanticTargetMapping>();
        var planById = plan.Attributes.ToDictionary(attribute => attribute.AttributeId);
        foreach (var mapping in response.TargetMappings)
        {
            if (!targets.TryGetValue(mapping.AttributeId, out var target) ||
                !planById.TryGetValue(mapping.AttributeId, out var planned) ||
                planned.Status != FieldMatchingEngineStatuses.AiRequired)
            {
                throw new ArgumentException($"Qwen尝试处理未授权目标字段 {mapping.AttributeId}。", nameof(response));
            }

            if (!string.Equals(mapping.AttributeName, target.Name, StringComparison.Ordinal) ||
                mapping.SourceFactIds.Any(factId => !facts.ContainsKey(factId)))
            {
                throw new ArgumentException($"Qwen字段 {mapping.AttributeId} 的名称或证据无效。", nameof(response));
            }

            var allowedIds = planned.DictionaryCandidates.Select(candidate => candidate.ValueId).ToHashSet();
            if (mapping.SelectedDictionaryValueIds.Any(valueId => !allowedIds.Contains(valueId)))
            {
                throw new ArgumentException($"Qwen字段 {mapping.AttributeId} 选择了候选范围之外的valueId。", nameof(response));
            }

            if (mapping.SourceFactIds.Count != mapping.SourceLabels.Count ||
                mapping.SourceFactIds.Count != mapping.SourceValues.Count ||
                mapping.SourceFactIds.Select((factId, index) =>
                    facts.TryGetValue(factId, out var fact) &&
                    string.Equals(fact.Label, mapping.SourceLabels[index], StringComparison.Ordinal) &&
                    string.Equals(fact.Value, mapping.SourceValues[index], StringComparison.Ordinal))
                .Any(valid => !valid))
            {
                throw new ArgumentException($"Qwen字段 {mapping.AttributeId} 改写了源证据。", nameof(response));
            }

            if (mapping.Status == SemanticMappingStatuses.Mapped &&
                ((target.DictionaryId > 0 && mapping.SelectedDictionaryValueIds.Count == 0) ||
                 (target.DictionaryId <= 0 && mapping.CandidateTextValues.Count == 0)))
            {
                throw new ArgumentException($"Qwen字段 {mapping.AttributeId} 没有可提交的最终值。", nameof(response));
            }

            if (!result.TryAdd(mapping.AttributeId, mapping))
            {
                throw new ArgumentException($"Qwen字段 {mapping.AttributeId} 重复。", nameof(response));
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<long, FieldConversionDecision> IndexConversions(
        IReadOnlyCollection<FieldConversionDecision>? conversions,
        FieldMatchingEnginePlan plan,
        IReadOnlyDictionary<long, FieldMatchingTargetAttribute> targets,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts)
    {
        var result = new Dictionary<long, FieldConversionDecision>();
        var planById = plan.Attributes.ToDictionary(attribute => attribute.AttributeId);
        foreach (var conversion in conversions ?? [])
        {
            if (!targets.TryGetValue(conversion.AttributeId, out var target) ||
                !planById.TryGetValue(conversion.AttributeId, out var planned) ||
                planned.Status != FieldMatchingEngineStatuses.ConversionRequired)
            {
                throw new ArgumentException($"转换结果尝试处理未授权目标字段 {conversion.AttributeId}。", nameof(conversions));
            }

            if (conversion.SourceFactIds.Any(factId => !facts.ContainsKey(factId)))
            {
                throw new ArgumentException($"转换字段 {conversion.AttributeId} 引用了不存在的事实。", nameof(conversions));
            }

            var allowedIds = conversion.DictionaryCandidates.Select(candidate => candidate.ValueId).ToHashSet();
            if (conversion.SelectedDictionaryValueIds.Any(valueId => !allowedIds.Contains(valueId)))
            {
                throw new ArgumentException($"转换字段 {conversion.AttributeId} 选择了候选范围之外的valueId。", nameof(conversions));
            }

            if (conversion.Status == FieldConversionStatuses.Mapped &&
                target.DictionaryId > 0 && conversion.SelectedDictionaryValueIds.Count == 0)
            {
                throw new ArgumentException($"转换字段 {conversion.AttributeId} 尚未解析Ozon valueId。", nameof(conversions));
            }

            if (conversion.Status == FieldConversionStatuses.Mapped &&
                target.DictionaryId <= 0 && conversion.TextValues.Count == 0)
            {
                throw new ArgumentException($"转换字段 {conversion.AttributeId} 没有可提交的最终文本。", nameof(conversions));
            }

            if (!result.TryAdd(conversion.AttributeId, conversion))
            {
                throw new ArgumentException($"转换字段 {conversion.AttributeId} 重复。", nameof(conversions));
            }
        }

        return result;
    }

    private static void ValidatePlan(
        FieldMatchingEnginePlan plan,
        IReadOnlyDictionary<long, FieldMatchingTargetAttribute> targets,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts)
    {
        if (plan.Attributes.Count != targets.Count ||
            plan.Attributes.Select(attribute => attribute.AttributeId).Distinct().Count() != plan.Attributes.Count)
        {
            throw new ArgumentException("引擎计划必须与输入目标字段一一对应。", nameof(plan));
        }

        foreach (var attribute in plan.Attributes)
        {
            if (!targets.ContainsKey(attribute.AttributeId) ||
                attribute.SourceFactIds.Any(factId => !facts.ContainsKey(factId)))
            {
                throw new ArgumentException($"引擎计划字段 {attribute.AttributeId} 无效。", nameof(plan));
            }

            var allowed = attribute.DictionaryCandidates.Select(candidate => candidate.ValueId).ToHashSet();
            if (attribute.SelectedDictionaryValueIds.Any(valueId => !allowed.Contains(valueId)))
            {
                throw new ArgumentException($"引擎计划字段 {attribute.AttributeId} 包含无效valueId。", nameof(plan));
            }


            var target = targets[attribute.AttributeId];
            if (attribute.Status == FieldMatchingEngineStatuses.Resolved &&
                ((target.DictionaryId > 0 && attribute.SelectedDictionaryValueIds.Count == 0) ||
                 (target.DictionaryId <= 0 && attribute.CandidateTextValues.Count == 0)))
            {
                throw new ArgumentException($"引擎计划字段 {attribute.AttributeId} 被标记为resolved但没有最终值。", nameof(plan));
            }
        }
    }

    private static IReadOnlyList<FieldMatchingFinalEvidence> Evidence(
        IReadOnlyList<string> factIds,
        IReadOnlyDictionary<string, FieldMatchingSourceFact> facts) =>
        factIds.Distinct(StringComparer.Ordinal)
            .Select(factId => facts[factId])
            .Select(fact => new FieldMatchingFinalEvidence(
                fact.FactId, fact.Label, fact.Value, fact.Source, fact.SourcePath))
            .ToArray();

    private static IReadOnlyList<FieldMatchingFinalCandidateValue> Candidates(
        IReadOnlyList<string> texts,
        IReadOnlyList<SemanticDictionaryCandidate> dictionaryCandidates)
    {
        var result = dictionaryCandidates
            .Select(candidate => new FieldMatchingFinalCandidateValue(
                candidate.Value, candidate.ValueId, candidate.Info, candidate.Picture))
            .ToList();
        result.AddRange(texts
            .Where(text => !result.Any(candidate => string.Equals(candidate.Text, text, StringComparison.Ordinal)))
            .Select(text => new FieldMatchingFinalCandidateValue(text, null)));
        return result;
    }

    private static IReadOnlyList<string> ResolveTexts(
        IReadOnlyList<string> texts,
        IReadOnlyList<SemanticDictionaryCandidate> candidates,
        IReadOnlyList<long> selectedIds)
    {
        if (selectedIds.Count == 0)
        {
            return texts.Distinct(StringComparer.Ordinal).ToArray();
        }

        var byId = candidates.ToDictionary(candidate => candidate.ValueId);
        return selectedIds.Select(valueId => byId[valueId].Value).Distinct(StringComparer.Ordinal).ToArray();
    }
}
