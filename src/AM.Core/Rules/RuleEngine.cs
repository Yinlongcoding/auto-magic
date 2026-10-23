namespace AM.Core.Rules;

/// <summary>
/// 按规则给 SKU 的属性取值。只读规则和本地字典缓存，不联网（08 号问题 6）。
/// <para>
/// 查找顺序：人工修订 → 类型层 → 类目层 → 全局层 → 字典中文名。命中即停，记下命中的层（18 号）。
/// 源值先按 <see cref="SourceNormalizer.Candidates"/> 分档：前一档在所有层都没命中，才试下一档。
/// 这样"1绿色有现货"的整值规则（若有）优先于从中剥出的"绿色"。
/// </para>
/// <para>
/// 字典中文名一层（2026-10-23 加，18 号原设计没有）：规整后与字典里唯一的中文名完全相同就直接用，
/// 免得为"黑色→黑色"这种一一对应的值逐条写规则。重名的（颜色字典有 52 个）不走这层，必须写规则。
/// 源值缺失时不走这层。
/// </para>
/// </summary>
public static class RuleEngine
{
    /// <param name="dictionaries">属性 ID → 整本字典。大字典不传，那个属性只能靠规则。</param>
    /// <param name="manualEdits">本任务的人工修订。</param>
    public static IReadOnlyList<AttributeMatch> Resolve(
        RuleSet rules,
        long descriptionCategoryId,
        long typeId,
        SkuSource source,
        IReadOnlyDictionary<long, IReadOnlyList<DictionaryEntry>> dictionaries,
        IReadOnlyList<ManualEdit> manualEdits)
    {
        var scopes = new[] { RuleSet.TypeScope(typeId), RuleSet.CategoryScope(descriptionCategoryId), RuleSet.GlobalScope };
        return rules.Attributes
            .Select(declaration => ResolveOne(rules, declaration, scopes, source,
                dictionaries.GetValueOrDefault(declaration.AttributeId), manualEdits))
            .ToList();
    }

    private static AttributeMatch ResolveOne(RuleSet rules, AttributeDeclaration declaration, string[] scopes,
        SkuSource source, IReadOnlyList<DictionaryEntry>? dictionary, IReadOnlyList<ManualEdit> manualEdits)
    {
        var id = declaration.AttributeId;
        var value = declaration.Sources.Select(source.Get).FirstOrDefault(v => v is not null);

        // 人工修订按整个源值对；源值缺失时对 <缺失>，这样缺字段的 SKU 也能在待处理页补
        var key = SourceNormalizer.Normalize(value ?? RuleRow.Missing);
        if (manualEdits.FirstOrDefault(e => e.AttributeId == id && SourceNormalizer.Normalize(e.Source) == key) is { } edit)
        {
            return new(id, value, key, edit.ValueId, edit.Label, MatchLayer.Manual, null, null);
        }

        // 源值缺失：只查 <缺失> 行
        if (value is null)
        {
            return FindInScopes(rules, id, scopes, RuleRow.Missing) is { } missing
                ? Hit(id, null, RuleRow.Missing, missing)
                : new(id, null, null, null, null, MatchLayer.None, null,
                    declaration.Sources.Count == 0 ? "没有为它写规则" : "源数据里没有这个字段，也没有缺失时的规则");
        }

        var uniqueNames = dictionary is null ? null : UniqueNames.GetValue(dictionary, BuildUniqueNames);

        foreach (var tier in SourceNormalizer.Candidates(value))
        {
            var hits = new List<AttributeMatch>();
            foreach (var candidate in tier)
            {
                if (FindInScopes(rules, id, scopes, candidate) is { } rule)
                {
                    hits.Add(Hit(id, value, candidate, rule));
                }
                else if (uniqueNames?.GetValueOrDefault(candidate) is { } entry)
                {
                    hits.Add(new(id, value, candidate, entry.Id, entry.Value, MatchLayer.Dictionary, null, null));
                }
            }

            switch (hits.DistinctBy(h => h.ValueId).Count())
            {
                case 1:
                    // 拆出的几段指向同一个值（"黑色-黑"），取层级最高的那个作追溯
                    return hits.OrderBy(h => h.Layer).First();
                case > 1:
                    return new(id, value, null, null, null, MatchLayer.None, null,
                        $"拆出的几段指向不同的值：{string.Join("、", hits.Select(h => $"{h.MatchedText}→{h.Label}").Distinct())}");
            }
        }

        if (declaration.PrefixMatch && PrefixCandidate(rules, id, scopes, uniqueNames, value) is { } prefix)
        {
            if (FindInScopes(rules, id, scopes, prefix) is { } rule)
            {
                return Hit(id, value, prefix, rule);
            }

            var entry = uniqueNames![prefix];
            return new(id, value, prefix, entry.Id, entry.Value, MatchLayer.Dictionary, null, null);
        }

        return new(id, value, null, null, null, MatchLayer.None, null, "没有规则命中，字典里也没有同名的值");
    }

    /// <summary>去修饰后的源值以哪个已知值开头，取最长的。已知值 = 生效层的规则源值 + 字典里唯一的中文名。</summary>
    private static string? PrefixCandidate(RuleSet rules, long attributeId, string[] scopes,
        Dictionary<string, DictionaryEntry>? uniqueNames, string value)
    {
        var cleaned = SourceNormalizer.Candidates(value)[1] is [var c] ? c : SourceNormalizer.Normalize(value);
        return rules.SourcesIn(attributeId, scopes)
            .Concat(uniqueNames?.Keys ?? Enumerable.Empty<string>())
            .Where(k => k.Length >= 2 && k != RuleRow.Missing && !k.All(char.IsAsciiDigit) && k.Length < cleaned.Length
                        && cleaned.StartsWith(k, StringComparison.Ordinal))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
    }

    // 每个 SKU 都要查，同一本字典只建一次；字典对象回收后自动丢弃
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        IReadOnlyList<DictionaryEntry>, Dictionary<string, DictionaryEntry>> UniqueNames = new();

    private static Dictionary<string, DictionaryEntry> BuildUniqueNames(IReadOnlyList<DictionaryEntry> dictionary) =>
        dictionary
            .GroupBy(e => SourceNormalizer.Normalize(e.Value))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());

    private static (RuleRow Row, string File, MatchLayer Layer)? FindInScopes(RuleSet rules, long attributeId,
        string[] scopes, string normalizedSource)
    {
        for (var i = 0; i < scopes.Length; i++)
        {
            if (rules.Find(scopes[i], attributeId, normalizedSource) is { } hit)
            {
                return (hit.Row, hit.File, (MatchLayer)(i + 1));
            }
        }

        return null;
    }

    private static AttributeMatch Hit(long attributeId, string? sourceValue, string matched,
        (RuleRow Row, string File, MatchLayer Layer) rule) =>
        new(attributeId, sourceValue, matched, rule.Row.ValueId, rule.Row.Label, rule.Layer, rule.File, null);
}
