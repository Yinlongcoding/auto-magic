namespace AutoMagic.Application.Ozon.Rules;

/// <summary>Produces Chinese values per SKU from source facts, following the merged attribute rules.</summary>
public sealed class CleaningEngine(RuleLoader ruleLoader, IRuleStore store)
{
    public async Task<CleanedProduct> CleanAsync(
        RuleSourceProduct source,
        OzonCategorySchema schema,
        CancellationToken cancellationToken = default)
    {
        var rules = await ruleLoader.LoadAsync(
            schema.Attributes.Select(a => (int)a.Id), schema.DescriptionCategoryId, schema.TypeId, cancellationToken);

        // Step E: a required attribute without any rule is a configuration error, not a product problem.
        var uncovered = schema.Attributes.Where(a => a.IsRequired && !rules.ContainsKey((int)a.Id)).ToArray();
        if (uncovered.Length > 0)
            throw new RuleConfigurationException(
                $"类型 {schema.TypeId} 的必填属性缺少规则：{string.Join("、", uncovered.Select(a => $"{a.Id} {a.Name}"))}");

        var order = DependencyResolver.Order(rules);
        var conversions = new Dictionary<string, ConversionTable>(StringComparer.Ordinal);
        foreach (var name in rules.Values.SelectMany(r => r.When.Select(w => w.Conversion).Append(r.Conversion)).OfType<string>().Distinct())
            conversions[name] = await store.LoadConversionTableAsync(name, cancellationToken)
                ?? throw new RuleConfigurationException($"换算表 {name} 不存在。");

        var skus = new List<CleanedSku>();
        foreach (var sku in source.Skus)
        {
            var values = new Dictionary<int, CleanedValue>();
            foreach (var id in order) values[id] = Clean(rules[id], source, sku, schema.TypeId, values, conversions);
            skus.Add(new(sku.Key, sku.SpecValues, values));
        }
        return new(source.OfferId, schema.DescriptionCategoryId, schema.TypeId, rules, skus);
    }

    private static CleanedValue Clean(
        AttributeRule rule,
        RuleSourceProduct source,
        RuleSourceSku sku,
        long typeId,
        IReadOnlyDictionary<int, CleanedValue> resolved,
        IReadOnlyDictionary<string, ConversionTable> conversions)
    {
        switch (rule.System)
        {
            case "card-group":
                return string.IsNullOrEmpty(source.OfferId)
                    ? CleanedValue.Blank("源商品缺少 offerId")
                    : new($"1688-{source.OfferId}", CleanOrigin.System);
            case "type-id":
                // Dictionary 1960 valueIds equal type_id, so 8229 carries its valueId directly.
                return new(typeId.ToString(), CleanOrigin.System) { PresetValueId = typeId };
            case not null:
                throw new RuleConfigurationException($"属性 {rule.AttributeId} 的 system 值 {rule.System} 无法识别。");
        }

        if (rule.When.Count > 0)
        {
            foreach (var clause in rule.When)
            {
                var checkValue = clause.Attribute is { } dependency
                    ? resolved[dependency].Value
                    : Read([clause.Source!], source, sku)?.Value;
                if (checkValue != clause.EqualTo) continue;

                var condition = clause.Attribute is { } a ? $"{a} = {clause.EqualTo}" : $"{clause.Source} = {clause.EqualTo}";
                if (clause.Value is not null) return new(clause.Value, CleanOrigin.Condition) { Note = condition };
                var read = ReadWithFallback(rule, source, sku);
                return Convert(read, clause.Conversion ?? rule.Conversion, conversions) with { Note = condition };
            }
            return rule.Otherwise is null
                ? CleanedValue.Blank("没有命中任何条件")
                : new(rule.Otherwise, CleanOrigin.Otherwise);
        }

        return Convert(ReadWithFallback(rule, source, sku), rule.Conversion, conversions);
    }

    private static CleanedValue ReadWithFallback(AttributeRule rule, RuleSourceProduct source, RuleSourceSku sku)
    {
        var read = Read(rule.Fields, source, sku);
        if (read is not null) return read;
        return rule.Fallback is null ? CleanedValue.Blank("源字段缺失") : new(rule.Fallback, CleanOrigin.Fallback);
    }

    private static CleanedValue Convert(CleanedValue value, string? conversion, IReadOnlyDictionary<string, ConversionTable> conversions)
    {
        if (conversion is null || value.Value is null) return value;
        // Unlisted values stay blank for human review instead of passing the raw source size through.
        return conversions[conversion].Mappings.TryGetValue(value.Value, out var converted)
            ? value with { Value = converted, Conversion = conversion }
            : value with { Value = null, Conversion = conversion, Origin = CleanOrigin.Blank, Note = $"换算表 {conversion} 没有 {value.Value}" };
    }

    private static CleanedValue? Read(IEnumerable<string> fields, RuleSourceProduct source, RuleSourceSku sku)
    {
        foreach (var field in fields)
        {
            if (sku.SpecValues.TryGetValue(field, out var spec))
                return new(spec, CleanOrigin.Source) { SourceField = field, SourceValue = spec, FromSpecAxis = true };
            if (source.Facts.TryGetValue(field, out var fact))
                return new(fact, CleanOrigin.Source) { SourceField = field, SourceValue = fact };
        }
        return null;
    }
}

public sealed class RuleConfigurationException(string message) : Exception(message);

public enum CleanOrigin { System, Source, Fallback, Condition, Otherwise, Blank }

/// <summary>Cleaned Chinese value; values read from the spec axis are SKU level, everything else product level.</summary>
public sealed record CleanedValue(string? Value, CleanOrigin Origin)
{
    public string? SourceField { get; init; }
    public string? SourceValue { get; init; }
    public bool FromSpecAxis { get; init; }
    public string? Conversion { get; init; }
    public long? PresetValueId { get; init; }
    public string? Note { get; init; }

    public static CleanedValue Blank(string note) => new(null, CleanOrigin.Blank) { Note = note };
}

public sealed record CleanedSku(string Key, IReadOnlyDictionary<string, string> SpecValues, IReadOnlyDictionary<int, CleanedValue> Values);

public sealed record CleanedProduct(
    string OfferId,
    long CategoryId,
    long TypeId,
    IReadOnlyDictionary<int, AttributeRule> Rules,
    IReadOnlyList<CleanedSku> Skus);
