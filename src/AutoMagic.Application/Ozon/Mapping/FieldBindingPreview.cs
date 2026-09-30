namespace AutoMagic.Application.Ozon.Mapping;

public sealed record FieldBindingFile(long DescriptionCategoryId, long? TypeId, IReadOnlyList<FieldBinding> Attributes);
public sealed record FieldBinding(long Id, string Name, IReadOnlyList<string>? Match = null, string? DefaultValue = null,
    IReadOnlyList<FieldCondition>? Conditions = null, string? FallbackValue = null);
public sealed record FieldCondition(string Field, string Operator, string Value, string Result);
public sealed class FieldBindingRow
{
    public long AttributeId { get; init; }
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string SourceValues { get; init; } = "";
    public bool UsedDefault { get; init; }
    public string Resolution { get; init; } = "source";
    public string InputText { get; set; } = "";
    public bool Reviewed { get; set; }
    public bool SchemaValid { get; init; }
    public long DictionaryId { get; init; }
    public string[] ScopeKeys { get; init; } = [];
    public string[] FactIds { get; init; } = [];
    public string ScopeDisplay => ScopeKeys.SequenceEqual(["product"]) ? "商品公共属性" : $"共享值 · {ScopeKeys.Length} 个 SKU";
}

/// <summary>Local field binding only; dictionary resolution happens after human confirmation.</summary>
public static class FieldBindingPreview
{
    public static IReadOnlyList<FieldBindingRow> CreateRequired(
        FieldMatchingInput source, IReadOnlyList<FieldBinding> bindings)
    {
        var required = source.Target.Attributes.Where(attribute => attribute.IsRequired).ToArray();
        var requiredIds = required.Select(attribute => attribute.AttributeId).ToHashSet();
        var configured = bindings.Where(binding => requiredIds.Contains(binding.Id)).ToArray();
        var rows = Create(source, configured).ToList();
        var configuredIds = configured.Select(binding => binding.Id).ToHashSet();

        foreach (var attribute in required.Where(attribute => !configuredIds.Contains(attribute.AttributeId)))
            rows.Add(new FieldBindingRow {
                AttributeId = attribute.AttributeId,
                Name = attribute.Name,
                Status = attribute.AttributeComplexId == 0
                    ? "必填项未配置映射，待填写"
                    : "必填复杂属性尚不支持",
                SchemaValid = attribute.AttributeComplexId == 0,
                DictionaryId = attribute.DictionaryId,
                Resolution = attribute.AttributeComplexId == 0 ? "unresolved" : "schema_invalid",
                ScopeKeys = ["product"]
            });

        var order = required.Select((attribute, index) => (attribute.AttributeId, index))
            .ToDictionary(item => item.AttributeId, item => item.index);
        return rows.OrderBy(row => order[row.AttributeId]).ToArray();
    }

    public static IReadOnlyList<FieldBindingRow> Create(FieldMatchingInput source, IReadOnlyList<FieldBinding> bindings)
    {
        if (bindings.Select(b => b.Id).Distinct().Count() != bindings.Count)
            throw new InvalidDataException("字段映射存在重复属性 ID。");
        foreach (var b in bindings)
        {
            var conditional = b.Conditions is { Count: > 0 };
            if (b.Id <= 0 || (conditional
                ? b.Match is { Count: > 0 } ||
                  b.Conditions!.Any(c => c is null || string.IsNullOrWhiteSpace(c.Field) ||
                    c.Operator is not ("equals" or "notEquals" or "contains") || string.IsNullOrWhiteSpace(c.Value) ||
                    string.IsNullOrWhiteSpace(c.Result))
                : b.Match is not { Count: > 0 } || b.Match.Any(string.IsNullOrWhiteSpace) || b.FallbackValue is not null))
                throw new InvalidDataException("字段规则须选择 match 或 conditions；条件只支持 equals/notEquals/contains。");
        }
        var cleaned = CleanedProductBuilder.Create(source);
        var allowed = cleaned.ConfirmedFacts.Where(f => f.FactId is not null && f.RawValue is string)
            .Select(f => "product:" + f.FactId).ToHashSet(StringComparer.Ordinal);
        var request = ProductMappingInputBuilder.Create(source).Request;
        var facts = request.Facts.Where(f => f.ScopeKey != "product" || allowed.Contains(f.FactId)).ToArray();
        var rows = new List<FieldBindingRow>();
        foreach (var binding in bindings)
        {
            var attribute = request.Attributes.SingleOrDefault(a => a.AttributeId == binding.Id);
            var conditional = binding.Conditions is { Count: > 0 };
            var labels = conditional ? binding.Conditions!.Select(c => c.Field).Distinct().ToArray() : binding.Match!.ToArray();
            var related = facts.Where(f => labels.Contains(f.Label, StringComparer.Ordinal)).ToArray();
            var variants = related.Where(f => f.ScopeKey != "product").ToArray();
            var scopes = variants.Length > 0 ? request.Skus.Select(s => s.VariantKey).ToArray() : ["product"];
            var groups = scopes.Select(scope => new {
                Scope = scope,
                Facts = related.Where(f => f.ScopeKey == scope || (conditional && f.ScopeKey == "product" &&
                    !related.Any(v => v.ScopeKey == scope && v.Label == f.Label))).ToArray()
            }).GroupBy(item => System.Text.Json.JsonSerializer.Serialize(
                item.Facts.Select(f => f.Label + "\u001f" + f.Value).Distinct().OrderBy(v => v, StringComparer.Ordinal)));
            foreach (var group in groups)
            {
                var evidence = group.SelectMany(g => g.Facts).ToArray();
                var values = evidence.Select(f => f.Value).Distinct().ToArray();
                var valid = attribute is not null && attribute.AttributeComplexId == 0;
                var useDefault = valid && related.Length == 0 && values.Length == 0 &&
                    !string.IsNullOrWhiteSpace(binding.DefaultValue) &&
                    !cleaned.UncertainFacts.Any(f => labels.Contains(f.Label, StringComparer.Ordinal));
                string? conditionResult = null;
                if (valid && conditional)
                    foreach (var condition in binding.Conditions!)
                    {
                        var candidates = evidence.Where(f => f.Label == condition.Field).Select(f => f.Value).Distinct().ToArray();
                        if (candidates.Length != 1 || string.IsNullOrWhiteSpace(candidates[0])) continue;
                        // Substring conversion must not hide competing country/source values.
                        if (condition.Operator == "contains" && (values.Length != 1 ||
                            cleaned.UncertainFacts.Any(f => labels.Contains(f.Label, StringComparer.Ordinal)))) continue;
                        var matches = condition.Operator switch {
                            "equals" => candidates[0] == condition.Value,
                            "notEquals" => candidates[0] != condition.Value,
                            "contains" => candidates[0].Contains(condition.Value, StringComparison.Ordinal),
                            _ => false
                        };
                        if (matches)
                        { conditionResult = condition.Result; break; }
                    }
                var fallback = valid && conditional && conditionResult is null && !string.IsNullOrWhiteSpace(binding.FallbackValue);
                rows.Add(new FieldBindingRow {
                    AttributeId = binding.Id, Name = attribute?.Name ?? binding.Name,
                    SchemaValid = valid, DictionaryId = attribute?.DictionaryId ?? 0,
                    Status = !valid ? "Schema 缺失或复杂属性不支持" : useDefault ? "规则默认值，待确认" : conditional ?
                        conditionResult is not null ? "条件命中，待确认" : fallback ? "条件落空，使用兜底值，待确认" : "条件落空，待填写" :
                        useDefault ? "规则默认值，待确认" : values.Length == 0 ? "缺失来源" :
                        values.Length > 1 ? "多个来源值，待确认" : "已关联，待确认",
                    SourceValues = string.Join(" / ", values),
                    InputText = useDefault ? binding.DefaultValue! : conditional ? conditionResult ?? (fallback ? binding.FallbackValue! : "") :
                        useDefault ? binding.DefaultValue! : valid && values.Length == 1 ? values[0] : "",
                    UsedDefault = useDefault || fallback,
                    Resolution = !valid ? "schema_invalid" : useDefault ? "default" : conditional ?
                        conditionResult is not null ? "condition" : fallback ? "fallback" : "unresolved" :
                        useDefault ? "default" : values.Length == 1 ? "source" : "unresolved",
                    ScopeKeys = group.Select(g => g.Scope).ToArray(),
                    FactIds = evidence.Select(f => f.FactId).Distinct().ToArray()
                });
            }
        }
        return rows;
    }
}
