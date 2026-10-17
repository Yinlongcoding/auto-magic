namespace AutoMagic.Application.Ozon.Rules;

/// <summary>Rule for one Ozon attribute after merging general rule, category override and type override.</summary>
public sealed record AttributeRule
{
    public required int AttributeId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>Source field names, checked in order: spec axis first (SKU level), then product facts.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Used only when every source field is missing.</summary>
    public string? Fallback { get; init; }

    /// <summary>"card-group" (8292) or "type-id" (8229).</summary>
    public string? System { get; init; }

    /// <summary>Conversion table applied to the source value; unlisted values become blank.</summary>
    public string? Conversion { get; init; }

    /// <summary>Evaluated in order; the first matching clause wins.</summary>
    public IReadOnlyList<WhenClause> When { get; init; } = [];

    /// <summary>Value when no "when" clause matches; blank when absent.</summary>
    public string? Otherwise { get; init; }
}

/// <summary>
/// Condition on a source field or another attribute's cleaned value. On match, returns <see cref="Value"/>,
/// or the rule's own source value converted by <see cref="Conversion"/>.
/// </summary>
public sealed record WhenClause
{
    public string? Source { get; init; }

    public int? Attribute { get; init; }

    public required string EqualTo { get; init; }

    public string? Value { get; init; }

    public string? Conversion { get; init; }
}

public sealed record RuleOverride(IReadOnlyDictionary<int, AttributeRule> Attributes);

public sealed record ConversionTable(string Name, IReadOnlyDictionary<string, string> Mappings);

public sealed record DictionarySynonyms(long DictionaryId, IReadOnlyList<DictionaryEntry> Entries);

/// <summary>One verified dictionary value; humans maintain only <see cref="Match"/>.</summary>
public sealed record DictionaryEntry(long ValueId, string Label, IReadOnlyList<string> Match);

public interface IRuleStore
{
    Task<AttributeRule?> LoadAttributeRuleAsync(int attributeId, CancellationToken cancellationToken);
    Task<RuleOverride?> LoadCategoryOverrideAsync(long categoryId, CancellationToken cancellationToken);
    Task<RuleOverride?> LoadTypeOverrideAsync(long typeId, CancellationToken cancellationToken);
    Task<ConversionTable?> LoadConversionTableAsync(string name, CancellationToken cancellationToken);
    Task<DictionarySynonyms?> LoadDictionarySynonymsAsync(long dictionaryId, CancellationToken cancellationToken);
}
