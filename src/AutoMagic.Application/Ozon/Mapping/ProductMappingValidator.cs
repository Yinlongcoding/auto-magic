using System.Globalization;
using System.Text.Json;

namespace AutoMagic.Application.Ozon.Mapping;

public static class ProductMappingValidator
{
    public static ProductMappingValidation Validate(ProductMappingRequest request, ProductMappingResponse? response,
        IReadOnlyList<ProductMappingIssue>? externalIssues = null)
    {
        var issues = new List<ProductMappingIssue>(externalIssues ?? []);
        var rows = new List<ProductMappingRow>();
        void Error(string code, string scope, long? attribute, string message) =>
            issues.Add(new("error", code, scope, attribute, message));
        if (response is null)
        {
            Error("response.missing", "product", null, "没有可校验的 AI 映射结果。");
            return new(false, 0, issues, rows);
        }
        if (response.RequestId != request.RequestId)
            Error("response.request_id", "product", null, "响应不属于当前商品映射请求。");
        if (response.ProductMappings is null || response.Variants is null || response.Warnings is null ||
            response.ProductMappings.Any(m => m is null) || response.Variants.Any(v => v is null) ||
            response.Warnings.Any(string.IsNullOrWhiteSpace))
        {
            Error("response.structure", "product", null, "响应数组不能为 null，也不能包含空元素或空警告。");
            return new(false, 0, issues, rows);
        }

        var attributes = request.Attributes.ToDictionary(a => a.AttributeId);
        var facts = request.Facts.ToDictionary(f => f.FactId, StringComparer.Ordinal);
        var expectedSkus = request.Skus.ToDictionary(s => s.VariantKey, StringComparer.Ordinal);
        var variants = new Dictionary<string, ProductVariantSuggestion>(StringComparer.Ordinal);
        ValidateScope("product", response.ProductMappings);
        foreach (var variant in response.Variants)
        {
            if (string.IsNullOrWhiteSpace(variant.VariantKey) || !expectedSkus.ContainsKey(variant.VariantKey))
            {
                Error("sku.fabricated", variant.VariantKey ?? "?", null, "响应包含不在本次已确认 SKU 集合中的变体。");
                continue;
            }
            if (!variants.TryAdd(variant.VariantKey, variant))
                Error("sku.duplicate", variant.VariantKey, null, "同一个真实 SKU 只能返回一次。");
            if (variant.Mappings is null || variant.Mappings.Any(m => m is null))
                Error("sku.structure", variant.VariantKey, null, "SKU 属性数组不能为空对象或包含 null。");
            else
                ValidateScope(variant.VariantKey, variant.Mappings);
        }
        foreach (var sku in request.Skus.Where(s => !variants.ContainsKey(s.VariantKey)))
            Error("sku.omitted", sku.VariantKey, null, "AI 未返回这个真实 SKU 的映射建议。");

        var unresolvedRequired = 0;
        var signatures = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var scopes = request.Skus.Count == 0 ? new[] { "product" } : request.Skus.Select(s => s.VariantKey).ToArray();
        foreach (var scope in scopes)
        {
            var local = variants.GetValueOrDefault(scope)?.Mappings ?? [];
            var overrides = local.Where(m => m is not null)
                .Select(m => (m.AttributeId, m.ComplexInstanceKey)).ToHashSet();
            // A SKU overrides one complex instance, not every instance of the attribute.
            // An unresolved whole-attribute override (null instance) suppresses all inherited values.
            var effective = response.ProductMappings.Where(m =>
                    !overrides.Contains((m.AttributeId, m.ComplexInstanceKey)) &&
                    !overrides.Contains((m.AttributeId, (string?)null)))
                .Concat(local.Where(m => m is not null)).ToArray();
            // Check group completeness before counting unresolved required attributes and drawing rows.
            foreach (var group in effective.Where(m => attributes.ContainsKey(m.AttributeId) &&
                         attributes[m.AttributeId].AttributeComplexId > 0 && !string.IsNullOrWhiteSpace(m.ComplexInstanceKey))
                         .GroupBy(m => (attributes[m.AttributeId].AttributeComplexId, m.ComplexInstanceKey)))
            {
                foreach (var required in request.Attributes.Where(a => a.IsRequired &&
                             a.AttributeComplexId == group.Key.AttributeComplexId))
                    if (!group.Any(m => m.AttributeId == required.AttributeId))
                        Error("complex.required_member", scope, required.AttributeId,
                            $"复杂属性组 {group.Key.ComplexInstanceKey} 缺少必填成员“{required.Name}”。");
            }
            foreach (var attribute in request.Attributes.Where(a => a.IsRequired))
            {
                var mappings = effective.Where(m => m.AttributeId == attribute.AttributeId).ToArray();
                if (mappings.Length == 0)
                {
                    unresolvedRequired++;
                    Error("required.omitted", scope, attribute.AttributeId,
                        $"必填属性“{attribute.Name}”没有返回建议或明确的未解决原因。");
                    rows.Add(new(scope, ScopeDisplay(scope), attribute.AttributeId, attribute.Name, true,
                        "missing_evidence", "", "", "", "校验失败", "AI 遗漏了必填属性。"));
                }
                else if (mappings.Any(m => m.Status != ProductMappingStatuses.Suggested) ||
                    issues.Any(i => i.Severity == "error" && (i.AttributeId is null || i.AttributeId == attribute.AttributeId) &&
                        (i.ScopeKey == scope || i.ScopeKey == "product")))
                {
                    unresolvedRequired++;
                }
            }
            AddRows(scope, effective);
            if (scope != "product" && effective.Length > 0 && effective.All(m => m.Status == ProductMappingStatuses.Suggested))
            {
                var signature = JsonSerializer.Serialize(effective.OrderBy(m => m.AttributeId)
                    .ThenBy(m => m.ComplexInstanceKey, StringComparer.Ordinal)
                    .Select(m => new { m.AttributeId, m.ComplexInstanceKey, m.Values }), ProductMappingJson.StrictOptions);
                if (!signatures.TryGetValue(signature, out var matchingScopes)) signatures[signature] = matchingScopes = [];
                matchingScopes.Add(scope);
            }
        }
        foreach (var same in signatures.Values.Where(scopes => scopes.Count > 1))
            issues.Add(new("warning", "sku.same_suggestions", "product", null,
                $"不同源 SKU 的建议属性完全相同：{string.Join("、", same)}。请检查规格区别是否尚未映射；程序不会合并或删除这些 SKU。"));
        if (request.Skus.Count > 0) AddRows("product", response.ProductMappings);
        foreach (var warning in response.Warnings)
            issues.Add(new("warning", "model.warning", "product", null, warning));
        return new(!issues.Any(i => i.Severity == "error"), unresolvedRequired, issues, rows);

        string ScopeDisplay(string scope) => scope == "product" ? "商品公共属性" :
            $"{expectedSkus[scope].MerchantSku} · 源SKU {expectedSkus[scope].SourceSkuId ?? "无"} · " +
            string.Join(" / ", expectedSkus[scope].Options.Select(o => $"{o.Key}={o.Value}"));

        void ValidateScope(string scope, IReadOnlyList<ProductAttributeSuggestion> mappings)
        {
            foreach (var duplicate in mappings.GroupBy(m => (m.AttributeId, m.ComplexInstanceKey)).Where(g => g.Count() > 1))
                Error("attribute.duplicate", scope, duplicate.Key.AttributeId, "同一范围和复杂组实例内的属性重复。");
            foreach (var mapping in mappings)
            {
                var id = mapping.AttributeId;
                if (!attributes.TryGetValue(id, out var target))
                {
                    Error("attribute.fabricated", scope, id, "属性 ID 不属于本次 Ozon Schema。");
                    continue;
                }
                if (mapping.Status is null || !ProductMappingStatuses.All.Contains(mapping.Status) ||
                    string.IsNullOrWhiteSpace(mapping.Reason) || mapping.Values is null || mapping.EvidenceFactIds is null ||
                    mapping.Values.Any(v => v is null || string.IsNullOrWhiteSpace(v.Text)) ||
                    mapping.EvidenceFactIds.Any(string.IsNullOrWhiteSpace))
                {
                    Error("mapping.structure", scope, id, "映射状态、原因、值或证据数组不符合合同。");
                    continue;
                }
                if (mapping.EvidenceFactIds.Distinct(StringComparer.Ordinal).Count() != mapping.EvidenceFactIds.Count)
                    Error("evidence.duplicate", scope, id, "同一建议不能重复引用证据。");
                if (mapping.Status != ProductMappingStatuses.Suggested)
                    issues.Add(new("warning", $"mapping.{mapping.Status}", scope, id, mapping.Reason));
                foreach (var factId in mapping.EvidenceFactIds)
                {
                    if (!facts.TryGetValue(factId, out var fact))
                        Error("evidence.fabricated", scope, id, $"引用的事实 {factId} 不存在。");
                    else if (fact.ScopeKey != "product" && fact.ScopeKey != scope)
                        Error("evidence.wrong_sku", scope, id, $"事实 {factId} 属于其他 SKU，不能用于当前范围。");
                }
                var hasValues = mapping.Status is ProductMappingStatuses.Suggested or ProductMappingStatuses.DictionaryPending;
                if (hasValues && (mapping.Values.Count == 0 || mapping.EvidenceFactIds.Count == 0))
                    Error("evidence.required", scope, id, "提供属性值必须引用当前商品或当前 SKU 的事实证据。");
                if (!hasValues && mapping.Values.Count != 0)
                    Error("status.values", scope, id, "缺失、歧义、待转换或待策略状态不得携带已选值。");
                if (mapping.Status == ProductMappingStatuses.MissingEvidence && mapping.EvidenceFactIds.Count != 0)
                    Error("status.missing", scope, id, "没有源证据的状态不能同时声称引用了证据。");
                if (mapping.Status == ProductMappingStatuses.PolicyRequired && mapping.EvidenceFactIds.Count != 0)
                    Error("status.policy", scope, id, "业务策略不能伪装为商品事实。");
                if (mapping.Status is ProductMappingStatuses.Ambiguous or ProductMappingStatuses.ConversionRequired &&
                    mapping.EvidenceFactIds.Count == 0)
                    Error("status.evidence", scope, id, "歧义或待转换状态须引用需要解释的源证据。");
                if (mapping.Status == ProductMappingStatuses.DictionaryPending && target.DictionaryId <= 0)
                    Error("dictionary.not_applicable", scope, id, "非字典属性不能等待字典解析。");
                if (target.AttributeComplexId == 0 && mapping.ComplexInstanceKey is not null)
                    Error("complex.unexpected", scope, id, "普通属性不能设置复杂组实例。");
                if (target.AttributeComplexId > 0 && hasValues && string.IsNullOrWhiteSpace(mapping.ComplexInstanceKey))
                    Error("complex.missing", scope, id, "复杂属性有值时必须指定实例键以保留成员关系。");
                if ((!target.IsCollection && mapping.Values.Count > 1) ||
                    (target.MaxValueCount > 0 && mapping.Values.Count > target.MaxValueCount))
                    Error("values.cardinality", scope, id, "属性值数量超过 Schema 的单值或多值上限。");
                if (mapping.Values.Select(v => (v.Text, v.DictionaryValueId)).Distinct().Count() != mapping.Values.Count)
                    Error("values.duplicate", scope, id, "属性值不能重复。");
                foreach (var value in mapping.Values)
                {
                    if (mapping.Status == ProductMappingStatuses.DictionaryPending)
                    {
                        if (value.DictionaryValueId is not null)
                            Error("dictionary.pending_id", scope, id, "字典待解析时不得选择最终 ID。");
                    }
                    else if (target.DictionaryId > 0)
                    {
                        var candidate = target.DictionaryCandidates.FirstOrDefault(c => c.ValueId == value.DictionaryValueId);
                        if (candidate is null || candidate.Text != value.Text)
                            Error("dictionary.invalid", scope, id, "字典 ID 和文本必须是当前属性候选中的同一条记录。");
                    }
                    else
                    {
                        if (value.DictionaryValueId is not null)
                            Error("dictionary.unexpected", scope, id, "非字典属性不能携带字典 ID。");
                        if (!ValidScalar(target.Type, value.Text))
                            Error("value.type", scope, id, $"值不符合 Schema 类型 {target.Type}，不能视作校验通过。");
                    }
                }
            }
        }

        void AddRows(string scope, IEnumerable<ProductAttributeSuggestion> mappings)
        {
            foreach (var mapping in mappings)
            {
                if (!attributes.TryGetValue(mapping.AttributeId, out var target)) continue;
                var error = issues.Any(i => i.Severity == "error" &&
                    (i.AttributeId is null || i.AttributeId == mapping.AttributeId) &&
                    (i.ScopeKey == scope || i.ScopeKey == "product"));
                var values = mapping.Values?.Where(v => v is not null).ToArray() ?? [];
                var evidence = (mapping.EvidenceFactIds ?? []).Where(id => id is not null && facts.ContainsKey(id))
                    .Select(id => $"{id}: {facts[id].Label}={facts[id].Value} [{facts[id].SourcePath}]");
                rows.Add(new(scope, ScopeDisplay(scope), target.AttributeId,
                    target.Name + (mapping.ComplexInstanceKey is null ? "" : $" [{mapping.ComplexInstanceKey}]"),
                    target.IsRequired, mapping.Status, string.Join("；", values.Select(v => v.Text)),
                    string.Join(", ", values.Where(v => v.DictionaryValueId.HasValue).Select(v => v.DictionaryValueId)),
                    string.Join("\n", evidence), error ? "校验失败" :
                        mapping.Status == ProductMappingStatuses.Suggested ? "约束通过·待语义复核" : "未解决",
                    mapping.Reason));
            }
        }
    }

    private static bool ValidScalar(string type, string value) => type.Trim().ToLowerInvariant() switch
    {
        "string" or "text" => !string.IsNullOrWhiteSpace(value),
        "integer" or "int" or "int32" or "int64" => long.TryParse(value, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out _),
        "decimal" or "number" or "float" or "double" => decimal.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out _),
        "boolean" or "bool" => value is "true" or "false",
        _ => false, // Unsupported types stay visible instead of pretending to have been validated.
    };
}
