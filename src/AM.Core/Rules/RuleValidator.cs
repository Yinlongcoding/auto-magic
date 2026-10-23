namespace AM.Core.Rules;

/// <summary>
/// 规则对某个类型的校验（18 号"目标 valueId 必须在该类型缓存的字典里"）。
/// 只看对这个类型生效的行：type:本类型、category:本类目、global。
/// 没有整本字典的属性（品牌）无法离线校验，列为"未核验"，不算错。
/// </summary>
public static class RuleValidator
{
    public sealed record Issue(string File, RuleRow Row, string Problem);

    public sealed record Result(IReadOnlyList<Issue> Errors, IReadOnlyList<(RuleRow Row, string File)> Unverified);

    /// <param name="knownAttributes">该类型的全部属性 ID。</param>
    public static Result Validate(RuleSet rules, long descriptionCategoryId, long typeId,
        IReadOnlySet<long> knownAttributes, IReadOnlyDictionary<long, IReadOnlyList<DictionaryEntry>> dictionaries)
    {
        var scopes = new HashSet<string>
        {
            RuleSet.TypeScope(typeId), RuleSet.CategoryScope(descriptionCategoryId), RuleSet.GlobalScope,
        };

        var errors = new List<Issue>();
        var unverified = new List<(RuleRow, string)>();
        foreach (var (row, file) in rules.Rows.Where(r => scopes.Contains(r.Row.Scope)))
        {
            if (!knownAttributes.Contains(row.Attribute))
            {
                errors.Add(new(file, row, $"类型 {typeId} 没有属性 {row.Attribute}"));
                continue;
            }

            if (!dictionaries.TryGetValue(row.Attribute, out var dictionary))
            {
                unverified.Add((row, file));
                continue;
            }

            var entry = dictionary.FirstOrDefault(e => e.Id == row.ValueId);
            if (entry is null)
            {
                errors.Add(new(file, row, $"valueId {row.ValueId} 不在类型 {typeId} 的字典里"));
            }
            else if (entry.Value != row.Label)
            {
                // 名称不符多半是抄错了 id，宁可报出来
                errors.Add(new(file, row, $"valueId {row.ValueId} 在字典里叫\"{entry.Value}\"，规则写的是\"{row.Label}\""));
            }
        }

        return new(errors, unverified);
    }
}
