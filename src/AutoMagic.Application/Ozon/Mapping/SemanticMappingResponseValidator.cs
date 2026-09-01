using System.Text.Json;

namespace AutoMagic.Application.Ozon.Mapping;

public static class SemanticMappingResponseValidator
{
    public static SemanticMappingValidationResult ParseAndValidate(
        SemanticMappingRequest request,
        string responseJson)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return Invalid("json.empty", "$", "模型响应为空。");
        }

        try
        {
            var response = JsonSerializer.Deserialize<SemanticMappingResponse>(
                responseJson,
                SemanticMappingJson.StrictOptions);
            return response is null
                ? Invalid("json.null", "$", "模型响应不能是 null。")
                : ValidateResponse(request, response);
        }
        catch (JsonException error)
        {
            var path = string.IsNullOrWhiteSpace(error.Path) ? "$" : error.Path;
            return Invalid("json.invalid", path!, "模型响应不符合JSON结构合同。");
        }
    }

    public static IReadOnlyList<SemanticMappingValidationIssue> ValidateRequest(
        SemanticMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<SemanticMappingValidationIssue>();

        Required(request.RequestId, "requestId", "$.requestId", issues);
        if (!string.Equals(
                request.Purpose,
                SemanticMappingPurposes.EvaluateCandidates,
                StringComparison.Ordinal))
        {
            Add(issues, "request.purpose", "$.purpose", "请求purpose不受支持。");
        }

        var context = request.MappingContext;
        if (context is null)
        {
            Add(issues, "request.context", "$.mappingContext", "缺少映射上下文。");
            return issues;
        }

        if (context.DescriptionCategoryId <= 0)
        {
            Add(issues, "request.categoryId", "$.mappingContext.descriptionCategoryId", "descriptionCategoryId必须为正整数。");
        }

        if (context.TypeId <= 0)
        {
            Add(issues, "request.typeId", "$.mappingContext.typeId", "typeId必须为正整数。");
        }

        Required(context.SelectedCategoryPath, "selectedCategoryPath", "$.mappingContext.selectedCategoryPath", issues);
        Required(context.OfferId, "offerId", "$.mappingContext.offerId", issues);
        Required(context.DetailUrl, "detailUrl", "$.mappingContext.detailUrl", issues);
        if (!DateTimeOffset.TryParse(
                context.DetailCapturedAt,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _))
        {
            Add(issues, "request.capturedAt", "$.mappingContext.detailCapturedAt", "detailCapturedAt必须是有效时间。");
        }

        if (!context.CategoryAndTypeConfirmedByUser)
        {
            Add(issues, "request.unconfirmed", "$.mappingContext.categoryAndTypeConfirmedByUser", "类目与类型必须由用户确认。");
        }

        var targetAttributes = request.TargetAttributes ?? [];
        if (targetAttributes.Count == 0)
        {
            Add(issues, "request.targets.empty", "$.targetAttributes", "至少需要一个目标属性。");
        }

        foreach (var duplicateId in targetAttributes.GroupBy(attribute => attribute.AttributeId)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            Add(issues, "request.targets.duplicate", "$.targetAttributes", $"目标属性ID {duplicateId} 重复。");
        }

        for (var index = 0; index < targetAttributes.Count; index++)
        {
            var attribute = targetAttributes[index];
            var path = $"$.targetAttributes[{index}]";
            if (attribute.AttributeId <= 0)
            {
                Add(issues, "request.target.id", $"{path}.attributeId", "attributeId必须为正整数。");
            }

            Required(attribute.Name, "name", $"{path}.name", issues);
            var candidates = attribute.DictionaryCandidates ?? [];
            if (attribute.DictionaryId <= 0 && candidates.Count > 0)
            {
                Add(issues, "request.target.dictionary", $"{path}.dictionaryCandidates", "无字典属性不能携带字典候选。");
            }

            if (candidates.Any(candidate => candidate.ValueId <= 0 || string.IsNullOrWhiteSpace(candidate.Value)))
            {
                Add(issues, "request.target.candidate", $"{path}.dictionaryCandidates", "字典候选必须包含正整数valueId和非空value。");
            }

            if (candidates.Select(candidate => candidate.ValueId).Distinct().Count() != candidates.Count)
            {
                Add(issues, "request.target.candidateDuplicate", $"{path}.dictionaryCandidates", "字典候选valueId不能重复。");
            }
        }

        var sourceFacts = request.SourceFacts ?? [];
        if (sourceFacts.Count == 0)
        {
            Add(issues, "request.facts.empty", "$.sourceFacts", "至少需要一个源事实。");
        }

        if (context.SourceFactCount != sourceFacts.Count)
        {
            Add(issues, "request.facts.count", "$.mappingContext.sourceFactCount", "sourceFactCount与sourceFacts数量不一致。");
        }

        if (context.DictionaryCandidatesProvided != targetAttributes.Any(attribute =>
                (attribute.DictionaryCandidates?.Count ?? 0) > 0))
        {
            Add(issues, "request.dictionaryFlag", "$.mappingContext.dictionaryCandidatesProvided", "字典候选标记与实际输入不一致。");
        }

        foreach (var duplicateId in sourceFacts.GroupBy(fact => fact.FactId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            Add(issues, "request.facts.duplicate", "$.sourceFacts", $"源事实ID {duplicateId} 重复。");
        }

        for (var index = 0; index < sourceFacts.Count; index++)
        {
            var fact = sourceFacts[index];
            var path = $"$.sourceFacts[{index}]";
            Required(fact.FactId, "factId", $"{path}.factId", issues);
            Required(fact.Label, "label", $"{path}.label", issues);
            Required(fact.Value, "value", $"{path}.value", issues);
            Required(fact.Source, "source", $"{path}.source", issues);
        }

        return issues;
    }

    public static SemanticMappingValidationResult ValidateResponse(
        SemanticMappingRequest request,
        SemanticMappingResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var issues = ValidateRequest(request).ToList();
        if (issues.Count > 0)
        {
            return new SemanticMappingValidationResult(response, issues);
        }

        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
        {
            Add(issues, "response.requestId", "$.requestId", "响应requestId必须与请求完全一致。");
        }

        var mappings = response.TargetMappings ?? [];
        if (mappings.Count != request.TargetAttributes.Count)
        {
            Add(issues, "response.mappingCount", "$.targetMappings", "每个目标属性必须且只能返回一次。");
        }

        var factsById = request.SourceFacts.ToDictionary(fact => fact.FactId, StringComparer.Ordinal);
        var referencedFactIds = new HashSet<string>(StringComparer.Ordinal);
        var mappingCount = Math.Min(mappings.Count, request.TargetAttributes.Count);
        for (var index = 0; index < mappingCount; index++)
        {
            ValidateMapping(
                request.TargetAttributes[index],
                mappings[index],
                factsById,
                referencedFactIds,
                index,
                issues);
        }

        if (mappings.Select(mapping => mapping.AttributeId).Distinct().Count() != mappings.Count)
        {
            Add(issues, "response.mappingDuplicate", "$.targetMappings", "响应包含重复目标属性。");
        }

        ValidateUnmappedFacts(request.SourceFacts, response.UnmappedSourceFactIds, referencedFactIds, issues);

        if (response.Warnings is null)
        {
            Add(issues, "response.warnings", "$.warnings", "warnings必须是数组。");
        }
        else if (response.Warnings.Any(string.IsNullOrWhiteSpace))
        {
            Add(issues, "response.warningEmpty", "$.warnings", "warnings不能包含空文本。");
        }

        return new SemanticMappingValidationResult(response, issues);
    }

    private static void ValidateMapping(
        SemanticTargetAttribute target,
        SemanticTargetMapping mapping,
        IReadOnlyDictionary<string, SemanticSourceFact> factsById,
        ISet<string> referencedFactIds,
        int index,
        ICollection<SemanticMappingValidationIssue> issues)
    {
        var path = $"$.targetMappings[{index}]";
        if (mapping.AttributeId != target.AttributeId ||
            !string.Equals(mapping.AttributeName, target.Name, StringComparison.Ordinal))
        {
            Add(issues, "mapping.targetOrder", path, "目标属性ID、名称或顺序与请求不一致。");
        }

        if (!SemanticMappingStatuses.All.Contains(mapping.Status))
        {
            Add(issues, "mapping.status", $"{path}.status", "映射状态不受支持。");
        }

        if (!SemanticMappingMethods.All.Contains(mapping.MappingMethod))
        {
            Add(issues, "mapping.method", $"{path}.mappingMethod", "映射方法不受支持。");
        }

        if (mapping.Confidence is < 0m or > 1m)
        {
            Add(issues, "mapping.confidence", $"{path}.confidence", "置信度必须在0到1之间。");
        }

        Required(mapping.Reason, "reason", $"{path}.reason", issues);
        var factIds = mapping.SourceFactIds ?? [];
        var labels = mapping.SourceLabels ?? [];
        var values = mapping.SourceValues ?? [];
        var candidateTexts = mapping.CandidateTextValues ?? [];
        var selectedValueIds = mapping.SelectedDictionaryValueIds ?? [];

        if (factIds.Count != labels.Count || factIds.Count != values.Count)
        {
            Add(issues, "mapping.evidenceLength", path, "三个证据数组长度必须相同。");
        }

        if (factIds.Distinct(StringComparer.Ordinal).Count() != factIds.Count)
        {
            Add(issues, "mapping.evidenceDuplicate", $"{path}.sourceFactIds", "同一映射不能重复引用源事实。");
        }

        for (var evidenceIndex = 0; evidenceIndex < factIds.Count; evidenceIndex++)
        {
            var factId = factIds[evidenceIndex];
            if (!factsById.TryGetValue(factId, out var fact))
            {
                Add(issues, "mapping.fabricatedFact", $"{path}.sourceFactIds[{evidenceIndex}]", "响应引用了请求中不存在的源事实。");
                continue;
            }

            referencedFactIds.Add(factId);
            if (evidenceIndex >= labels.Count || evidenceIndex >= values.Count)
            {
                continue;
            }

            if (!string.Equals(labels[evidenceIndex], fact.Label, StringComparison.Ordinal) ||
                !string.Equals(values[evidenceIndex], fact.Value, StringComparison.Ordinal))
            {
                Add(issues, "mapping.evidenceChanged", $"{path}.sourceLabels[{evidenceIndex}]", "证据字段名或字段值与原始事实不一致。");
            }
        }

        if (candidateTexts.Any(string.IsNullOrWhiteSpace) ||
            candidateTexts.Distinct(StringComparer.Ordinal).Count() != candidateTexts.Count)
        {
            Add(issues, "mapping.candidateText", $"{path}.candidateTextValues", "文本候选必须非空且不能重复。");
        }

        var allowedValueIds = (target.DictionaryCandidates ?? [])
            .Select(candidate => candidate.ValueId)
            .ToHashSet();
        if (selectedValueIds.Any(valueId => !allowedValueIds.Contains(valueId)))
        {
            Add(issues, "mapping.fabricatedDictionaryId", $"{path}.selectedDictionaryValueIds", "响应选择了输入字典候选之外的valueId。");
        }

        if (selectedValueIds.Distinct().Count() != selectedValueIds.Count)
        {
            Add(issues, "mapping.dictionaryDuplicate", $"{path}.selectedDictionaryValueIds", "字典valueId不能重复。");
        }

        ValidateStatusConsistency(target, mapping, factIds, candidateTexts, selectedValueIds, path, issues);
    }

    private static void ValidateStatusConsistency(
        SemanticTargetAttribute target,
        SemanticTargetMapping mapping,
        IReadOnlyCollection<string> factIds,
        IReadOnlyCollection<string> candidateTexts,
        IReadOnlyCollection<long> selectedValueIds,
        string path,
        ICollection<SemanticMappingValidationIssue> issues)
    {
        switch (mapping.Status)
        {
            case SemanticMappingStatuses.Mapped:
                if (mapping.DictionaryResolutionRequired || mapping.ExternalRuleRequired)
                {
                    Add(issues, "mapping.mappedDependencies", path, "mapped状态不能保留未解决依赖。");
                }

                if (target.DictionaryId > 0 && selectedValueIds.Count == 0)
                {
                    Add(issues, "mapping.mappedDictionary", path, "字典属性标记为mapped时必须选择已提供的valueId。");
                }

                if (target.DictionaryId <= 0 && candidateTexts.Count == 0)
                {
                    Add(issues, "mapping.mappedValue", path, "非字典属性标记为mapped时必须提供文本候选。");
                }
                break;

            case SemanticMappingStatuses.DictionaryPending:
                if (target.DictionaryId <= 0 || !mapping.DictionaryResolutionRequired ||
                    mapping.ExternalRuleRequired || selectedValueIds.Count > 0 || candidateTexts.Count == 0)
                {
                    Add(issues, "mapping.dictionaryPending", path, "dictionary_pending状态、候选和依赖标记不一致。");
                }
                break;

            case SemanticMappingStatuses.PolicyRequired:
                if (mapping.MappingMethod != SemanticMappingMethods.Policy ||
                    !mapping.ExternalRuleRequired || factIds.Count > 0 || selectedValueIds.Count > 0)
                {
                    Add(issues, "mapping.policy", path, "policy_required必须使用policy方法、保留外部依赖且不能伪装商品证据。");
                }
                break;

            case SemanticMappingStatuses.ConversionRequired:
                if (mapping.MappingMethod != SemanticMappingMethods.Conversion ||
                    !mapping.ExternalRuleRequired ||
                    mapping.DictionaryResolutionRequired != (target.DictionaryId > 0) ||
                    factIds.Count == 0 || selectedValueIds.Count > 0)
                {
                    Add(issues, "mapping.conversion", path, "conversion_required状态、证据和依赖标记不一致。");
                }
                break;

            case SemanticMappingStatuses.MissingEvidence:
                if (mapping.MappingMethod != SemanticMappingMethods.None ||
                    factIds.Count > 0 || candidateTexts.Count > 0 || selectedValueIds.Count > 0 ||
                    mapping.DictionaryResolutionRequired || mapping.ExternalRuleRequired)
                {
                    Add(issues, "mapping.missing", path, "missing_evidence不能携带证据、候选或未解决依赖。");
                }
                break;

            case SemanticMappingStatuses.Ambiguous:
                if (factIds.Count == 0 || selectedValueIds.Count > 0 || mapping.ExternalRuleRequired)
                {
                    Add(issues, "mapping.ambiguous", path, "ambiguous必须引用相关证据，且不能选择最终字典值或声明外部规则依赖。");
                }

                if (mapping.DictionaryResolutionRequired != (target.DictionaryId > 0))
                {
                    Add(issues, "mapping.ambiguousDictionary", path, "ambiguous的字典依赖标记必须与目标属性一致。");
                }
                break;
        }
    }

    private static void ValidateUnmappedFacts(
        IReadOnlyList<SemanticSourceFact> sourceFacts,
        IReadOnlyList<string>? unmappedSourceFactIds,
        IReadOnlySet<string> referencedFactIds,
        ICollection<SemanticMappingValidationIssue> issues)
    {
        if (unmappedSourceFactIds is null)
        {
            Add(issues, "response.unmapped", "$.unmappedSourceFactIds", "unmappedSourceFactIds必须是数组。");
            return;
        }

        var expected = sourceFacts
            .Select(fact => fact.FactId)
            .Where(factId => !referencedFactIds.Contains(factId))
            .ToHashSet(StringComparer.Ordinal);
        var actual = unmappedSourceFactIds.ToHashSet(StringComparer.Ordinal);
        if (actual.Count != unmappedSourceFactIds.Count || !actual.SetEquals(expected))
        {
            Add(issues, "response.unmappedComplement", "$.unmappedSourceFactIds", "未映射事实必须是所有已引用事实的精确补集，且不能重复。");
        }
    }

    private static SemanticMappingValidationResult Invalid(string code, string path, string message) =>
        new(null, [new SemanticMappingValidationIssue(code, path, message)]);

    private static void Required(
        string? value,
        string field,
        string path,
        ICollection<SemanticMappingValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(issues, "value.required", path, $"{field}不能为空。");
        }
    }

    private static void Add(
        ICollection<SemanticMappingValidationIssue> issues,
        string code,
        string path,
        string message) =>
        issues.Add(new SemanticMappingValidationIssue(code, path, message));
}
