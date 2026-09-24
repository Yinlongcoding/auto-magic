namespace AutoMagic.Application.Ozon.Mapping;

public sealed record CleanedProduct(
    CleanedSourceProduct SourceProduct,
    IReadOnlyList<CleanedConfirmedFact> ConfirmedFacts,
    IReadOnlyList<CleanedUncertainFact> UncertainFacts,
    IReadOnlyList<CleanedSkuCombination> SkuCombinations);

public sealed record CleanedSourceProduct(
    string Platform,
    string OfferId,
    string Title,
    string DetailUrl,
    string? CapturedAt,
    string CaptureStatus);

public sealed record CleanedConfirmedFact(
    string? FactId,
    string Label,
    object RawValue,
    string SourcePath);

public sealed record CleanedUncertainFact(
    string Label,
    string? CandidateValue,
    string Reason,
    IReadOnlyList<string> EvidenceFactIds);

public sealed record CleanedSkuCombination(
    string CombinationKey,
    string Verification,
    IReadOnlyDictionary<string, string?> Options);

/// <summary>
/// Presents captured source facts separately from interpretations. This view does not
/// depend on an Ozon schema and does not resolve or request any AI mapping.
/// </summary>
public static class CleanedProductBuilder
{
    public static CleanedProduct Create(FieldMatchingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var dimensions = input.Source.SkuDimensions
            .Select((dimension, index) => (dimension, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.dimension.Name) && item.dimension.Options.Count > 0)
            .ToArray();
        var confirmed = new List<CleanedConfirmedFact>();
        foreach (var fact in input.Source.Facts.Where(fact => fact.Kind is "title" or "attribute"))
        {
            var dimension = dimensions.FirstOrDefault(item =>
                string.Equals(item.dimension.Name.Trim(), fact.Label.Trim(), StringComparison.Ordinal));
            object value = dimension.dimension is null
                ? fact.Value
                : dimension.dimension.Options.Select(option => option.SourceValue).ToArray();
            confirmed.Add(new CleanedConfirmedFact(fact.FactId, fact.Label, value, fact.SourcePath));
        }

        foreach (var (dimension, index) in dimensions)
        {
            if (confirmed.Any(fact => string.Equals(fact.Label.Trim(), dimension.Name.Trim(), StringComparison.Ordinal)))
                continue;
            confirmed.Add(new CleanedConfirmedFact(
                null,
                dimension.Name,
                dimension.Options.Select(option => option.SourceValue).ToArray(),
                $"$.raw.skuDimensions[{index}]"));
        }

        var uncertain = input.Source.Facts
            .Where(fact => fact.Kind is "derived" or "unresolved")
            .Where(fact => fact.Source is not "structured-sku-dimension" and not "normalization:color-options")
            .Select(fact => new CleanedUncertainFact(
                fact.Label,
                fact.Kind == "unresolved" ? null : fact.Value,
                fact.Kind == "unresolved"
                    ? $"无法从源选项“{fact.Value}”确认该值。"
                    : $"由 {fact.Source} 得到的候选值，尚不是直接采集的事实。",
                fact.DerivedFromFactIds))
            .ToArray();

        return new CleanedProduct(
            new CleanedSourceProduct(
                input.Source.Platform,
                input.ProductRef.OfferId,
                input.Source.Title,
                input.ProductRef.DetailUrl,
                input.ProductRef.CapturedAt,
                input.ProductRef.CaptureStatus),
            confirmed,
            uncertain,
            input.Source.SkuCombinations
                .Select(combination => new CleanedSkuCombination(
                    combination.CombinationKey,
                    combination.Verification,
                    combination.Options.ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal)))
                .ToArray());
    }
}
