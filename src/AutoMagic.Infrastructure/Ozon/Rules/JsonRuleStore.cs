using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMagic.Application.Ozon.Rules;

namespace AutoMagic.Infrastructure.Ozon.Rules;

/// <summary>
/// Reads rules/attributes, rules/overrides/{categories,types}, rules/conversions and rules/dictionaries.
/// Files are re-read on every call so manual JSON edits take effect on the next clean.
/// </summary>
public sealed class JsonRuleStore(string rulesRoot) : IRuleStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<AttributeRule?> LoadAttributeRuleAsync(int attributeId, CancellationToken cancellationToken)
    {
        var file = await ReadAsync<AttributeRuleJson>(cancellationToken, "attributes", $"{attributeId}.json");
        if (file is null) return null;
        if (file.AttributeId != attributeId)
            throw new RuleConfigurationException($"attributes/{attributeId}.json 的 attributeId 为 {file.AttributeId}，与文件名不一致。");
        return file.ToRule(attributeId, $"attributes/{attributeId}.json");
    }

    public async Task<RuleOverride?> LoadCategoryOverrideAsync(long categoryId, CancellationToken cancellationToken)
    {
        var file = await ReadAsync<OverrideJson>(cancellationToken, "overrides", "categories", $"{categoryId}.json");
        if (file is null) return null;
        if (file.CategoryId != categoryId || file.TypeId is not null)
            throw new RuleConfigurationException($"overrides/categories/{categoryId}.json 的 categoryId 与文件名不一致。");
        return file.ToOverride($"overrides/categories/{categoryId}.json");
    }

    public async Task<RuleOverride?> LoadTypeOverrideAsync(long typeId, CancellationToken cancellationToken)
    {
        var file = await ReadAsync<OverrideJson>(cancellationToken, "overrides", "types", $"{typeId}.json");
        if (file is null) return null;
        if (file.TypeId != typeId || file.CategoryId is not null)
            throw new RuleConfigurationException($"overrides/types/{typeId}.json 的 typeId 与文件名不一致。");
        return file.ToOverride($"overrides/types/{typeId}.json");
    }

    public async Task<ConversionTable?> LoadConversionTableAsync(string name, CancellationToken cancellationToken)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new RuleConfigurationException($"换算表名称 {name} 只能包含字母、数字和连字符。");
        var file = await ReadAsync<ConversionJson>(cancellationToken, "conversions", $"{name}.json");
        if (file is null) return null;
        if (file.Name != name) throw new RuleConfigurationException($"conversions/{name}.json 的 name 与文件名不一致。");
        return new(name, file.Mappings ?? []);
    }

    public async Task<DictionarySynonyms?> LoadDictionarySynonymsAsync(long dictionaryId, CancellationToken cancellationToken)
    {
        var file = await ReadAsync<DictionaryJson>(cancellationToken, "dictionaries", $"{dictionaryId}.json");
        if (file is null) return null;
        if (file.DictionaryId != dictionaryId)
            throw new RuleConfigurationException($"dictionaries/{dictionaryId}.json 的 dictionaryId 与文件名不一致。");
        var entries = (file.Entries ?? []).Select(e => new DictionaryEntry(e.ValueId, e.Label.Trim(), e.Match ?? [])).ToArray();
        var duplicate = entries.SelectMany(e => e.Match.Append(e.Label).Distinct().Select(text => (text, e.ValueId)))
            .GroupBy(p => p.text).FirstOrDefault(g => g.Select(p => p.ValueId).Distinct().Count() > 1);
        if (duplicate is not null)
            throw new RuleConfigurationException($"dictionaries/{dictionaryId}.json 中“{duplicate.Key}”同时指向多个 valueId。");
        return new(dictionaryId, entries);
    }

    private async Task<T?> ReadAsync<T>(CancellationToken cancellationToken, params string[] parts) where T : class
    {
        var path = Path.Combine([rulesRoot, .. parts]);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken)
                ?? throw new RuleConfigurationException($"{string.Join('/', parts)} 内容为空。");
        }
        catch (JsonException error)
        {
            throw new RuleConfigurationException($"{string.Join('/', parts)} 格式错误：{error.Message}");
        }
    }

    private sealed record AttributeRuleJson(
        int AttributeId,
        string? Name,
        string[]? Fields,
        string? Fallback,
        string? System,
        string? Conversion,
        WhenJson[]? When,
        string? Otherwise)
    {
        public AttributeRule ToRule(int attributeId, string file)
        {
            var when = (When ?? []).Select(w => w.ToClause(file)).ToArray();
            if (System is not null && (Fields is { Length: > 0 } || when.Length > 0 || Fallback is not null || Conversion is not null))
                throw new RuleConfigurationException($"{file} 属性 {attributeId}：system 规则不能同时配置 fields、when、fallback 或 conversion。");
            return new()
            {
                AttributeId = attributeId,
                Name = Name?.Trim() ?? string.Empty,
                Fields = Fields ?? [],
                Fallback = Fallback,
                System = System,
                Conversion = Conversion,
                When = when,
                Otherwise = Otherwise,
            };
        }
    }

    private sealed record WhenJson(
        string? Source,
        int? Attribute,
        [property: JsonPropertyName("equals")] string? EqualTo,
        string? Value,
        string? Conversion)
    {
        public WhenClause ToClause(string file)
        {
            if ((Source is null) == (Attribute is null))
                throw new RuleConfigurationException($"{file}：when 条件必须且只能指定 source 或 attribute 之一。");
            if (string.IsNullOrEmpty(EqualTo))
                throw new RuleConfigurationException($"{file}：when 条件缺少 equals。");
            if ((Value is null) == (Conversion is null))
                throw new RuleConfigurationException($"{file}：when 条件必须且只能指定 value 或 conversion 之一。");
            return new() { Source = Source, Attribute = Attribute, EqualTo = EqualTo, Value = Value, Conversion = Conversion };
        }
    }

    private sealed record OverrideJson(long? CategoryId, long? TypeId, Dictionary<string, OverrideAttributeJson>? Attributes)
    {
        public RuleOverride ToOverride(string file) => new((Attributes ?? []).ToDictionary(
            p => int.TryParse(p.Key, out var id) && id > 0 ? id : throw new RuleConfigurationException($"{file}：属性键 {p.Key} 不是有效的属性 ID。"),
            p => new AttributeRuleJson(int.Parse(p.Key), p.Value.Name, p.Value.Fields, p.Value.Fallback, p.Value.System,
                p.Value.Conversion, p.Value.When, p.Value.Otherwise).ToRule(int.Parse(p.Key), file)));
    }

    private sealed record OverrideAttributeJson(
        string? Name, string[]? Fields, string? Fallback, string? System, string? Conversion, WhenJson[]? When, string? Otherwise);

    private sealed record ConversionJson(string Name, Dictionary<string, string>? Mappings);

    private sealed record DictionaryJson(long DictionaryId, DictionaryEntryJson[]? Entries);

    private sealed record DictionaryEntryJson(long ValueId, string Label, string[]? Match);
}
