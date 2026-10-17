namespace AutoMagic.Application.Ozon.Rules;

/// <summary>Maps cleaned Chinese values to Ozon valueIds using rules/dictionaries plus the per-type dictionary cache.</summary>
public sealed class MatchingEngine(IRuleStore store, IDictionaryValueCache dictionaryCache)
{
    public async Task<MatchedProduct> MatchAsync(
        CleanedProduct cleaned,
        OzonCategorySchema schema,
        CancellationToken cancellationToken = default)
    {
        var synonyms = new Dictionary<long, DictionarySynonyms?>();
        var memo = new Dictionary<(int, string), MatchedValue>();
        var skus = new List<MatchedSku>();
        foreach (var sku in cleaned.Skus)
        {
            var values = new Dictionary<int, MatchedValue>();
            foreach (var attribute in schema.Attributes)
            {
                var id = (int)attribute.Id;
                if (!sku.Values.TryGetValue(id, out var value))
                {
                    if (attribute.IsRequired) throw new RuleConfigurationException($"必填属性 {id} 没有清洗结果。");
                    continue; // no rule and not required: not submitted
                }
                var key = (id, value.Value ?? string.Empty);
                if (!memo.TryGetValue(key, out var matched))
                {
                    matched = await MatchValueAsync(attribute, value, cleaned, synonyms, cancellationToken);
                    memo[key] = matched;
                }
                values[id] = matched;
            }
            skus.Add(new(sku.Key, values));
        }
        return new(skus);
    }

    private async Task<MatchedValue> MatchValueAsync(
        OzonAttributeDefinition attribute,
        CleanedValue value,
        CleanedProduct cleaned,
        Dictionary<long, DictionarySynonyms?> synonyms,
        CancellationToken cancellationToken)
    {
        if (value.Value is null) return Result(attribute, null, null, MatchStatus.Blank, value.Note);
        if (value.PresetValueId is { } preset) return Result(attribute, value.Value, preset, MatchStatus.Matched, null);
        if (attribute.DictionaryId == 0) return Result(attribute, value.Value, null, MatchStatus.FreeText, null);

        if (!synonyms.TryGetValue(attribute.DictionaryId, out var dictionary))
            synonyms[attribute.DictionaryId] = dictionary = await store.LoadDictionarySynonymsAsync(attribute.DictionaryId, cancellationToken);
        var entry = dictionary?.Entries.FirstOrDefault(e => e.Label == value.Value || e.Match.Contains(value.Value));
        if (entry is null)
            return Result(attribute, value.Value, null, MatchStatus.Unmatched, $"字典 {attribute.DictionaryId} 没有“{value.Value}”");

        // Applicability: the valueId must exist in the dictionary fetched for this category and type.
        var applicable = await dictionaryCache.ContainsAsync(
            cleaned.CategoryId, cleaned.TypeId, attribute.DictionaryId, entry.ValueId, cancellationToken);
        return applicable switch
        {
            true => Result(attribute, value.Value, entry.ValueId, MatchStatus.Matched, entry.Label),
            false => Result(attribute, value.Value, null, MatchStatus.Unmatched, $"valueId {entry.ValueId} 不在类目 {cleaned.CategoryId} / 类型 {cleaned.TypeId} 的字典中"),
            null => Result(attribute, value.Value, null, MatchStatus.Unmatched, $"缺少字典 {attribute.DictionaryId} 的缓存，无法核对适用范围"),
        };
    }

    private static MatchedValue Result(OzonAttributeDefinition attribute, string? value, long? valueId, MatchStatus status, string? note) =>
        new(value, valueId, status, attribute.IsRequired && status is MatchStatus.Blank or MatchStatus.Unmatched, note);
}

public interface IDictionaryValueCache
{
    /// <summary>Null when no cache exists for this category, type and dictionary.</summary>
    Task<bool?> ContainsAsync(long categoryId, long typeId, long dictionaryId, long valueId, CancellationToken cancellationToken);
}

public enum MatchStatus { Matched, FreeText, Unmatched, Blank }

/// <summary>Blocking = required and without a value; non-required blanks are simply not submitted.</summary>
public sealed record MatchedValue(string? Value, long? ValueId, MatchStatus Status, bool IsBlocking, string? Note);

public sealed record MatchedSku(string Key, IReadOnlyDictionary<int, MatchedValue> Values);

public sealed record MatchedProduct(IReadOnlyList<MatchedSku> Skus)
{
    public bool HasBlockingItems => Skus.Any(s => s.Values.Values.Any(v => v.IsBlocking));
}
