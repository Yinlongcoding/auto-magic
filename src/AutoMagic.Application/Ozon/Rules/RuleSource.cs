using System.Text.Json;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Application.Ozon.Rules;

/// <summary>Source facts for one collected 1688 product: product-level facts plus one entry per spec combination.</summary>
public sealed record RuleSourceProduct(
    string OfferId,
    string? Title,
    IReadOnlyDictionary<string, string> Facts,
    IReadOnlyList<RuleSourceSku> Skus);

/// <summary>Spec-axis values of one combination (empty when the product has no spec axes).</summary>
public sealed record RuleSourceSku(string Key, IReadOnlyDictionary<string, string> SpecValues);

public static class RuleSourceBuilder
{
    public static RuleSourceProduct Create(DetailCollectionResultDto detail)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in detail.Facts)
        {
            var label = fact.Label?.Trim();
            var value = fact.Value?.Trim();
            if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(value)) continue;
            facts.TryAdd(label, value); // first occurrence wins; duplicates are collector noise
        }

        var offerId = string.Empty;
        var skus = new List<RuleSourceSku>();
        if (detail.Raw is { ValueKind: JsonValueKind.Object } raw)
        {
            if (raw.TryGetProperty("offerId", out var id))
                offerId = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? string.Empty;
            if (raw.TryGetProperty("skuCombinations", out var combinations) && combinations.ValueKind == JsonValueKind.Array)
            {
                foreach (var combination in combinations.EnumerateArray())
                {
                    if (!combination.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object) continue;
                    var specValues = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var option in options.EnumerateObject())
                        if (option.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(option.Value.GetString()))
                            specValues[option.Name.Trim()] = option.Value.GetString()!.Trim();
                    var key = combination.TryGetProperty("combinationKey", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString()! : string.Join("|", specValues.Select(p => $"{p.Key}={p.Value}"));
                    skus.Add(new(key, specValues));
                }
            }
        }

        if (skus.Count == 0) skus.Add(new("single", new Dictionary<string, string>()));
        return new(offerId, detail.ProductTitle, facts, skus);
    }
}
