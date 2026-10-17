namespace AutoMagic.Application.Ozon.Rules;

/// <summary>Merges the fixed three layers: type override > category override > general attribute rule.</summary>
public sealed class RuleLoader(IRuleStore store)
{
    public async Task<IReadOnlyDictionary<int, AttributeRule>> LoadAsync(
        IEnumerable<int> attributeIds,
        long categoryId,
        long typeId,
        CancellationToken cancellationToken = default)
    {
        var categoryOverride = await store.LoadCategoryOverrideAsync(categoryId, cancellationToken);
        var typeOverride = await store.LoadTypeOverrideAsync(typeId, cancellationToken);
        var result = new Dictionary<int, AttributeRule>();
        foreach (var id in attributeIds)
        {
            var rule = await store.LoadAttributeRuleAsync(id, cancellationToken);
            if (categoryOverride?.Attributes.TryGetValue(id, out var category) == true) rule = Merge(rule, category);
            if (typeOverride?.Attributes.TryGetValue(id, out var type) == true) rule = Merge(rule, type);
            if (rule is not null) result[id] = rule;
        }
        return result;
    }

    // Override wins field by field; unset override fields keep the lower layer's value.
    private static AttributeRule Merge(AttributeRule? lower, AttributeRule upper) => lower is null ? upper : new()
    {
        AttributeId = lower.AttributeId,
        Name = string.IsNullOrWhiteSpace(upper.Name) ? lower.Name : upper.Name,
        Fields = upper.Fields.Count > 0 ? upper.Fields : lower.Fields,
        Fallback = upper.Fallback ?? lower.Fallback,
        System = upper.System ?? lower.System,
        Conversion = upper.Conversion ?? lower.Conversion,
        When = upper.When.Count > 0 ? upper.When : lower.When,
        Otherwise = upper.Otherwise ?? lower.Otherwise,
    };
}
