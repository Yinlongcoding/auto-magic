using System.Text.Json.Serialization;

namespace AutoMagic.Application.Ozon.Mapping;

/// <summary>
/// Minimal model-facing contract. Internal collection, audit, identity and source-path fields never leave AM.
/// Short aliases are correlation handles only; the adjacent text is the complete semantic evidence.
/// </summary>
public sealed record QwenProductMappingTransport(
    string RequestId,
    string Category,
    IReadOnlyList<QwenProductFact> ProductFacts,
    IReadOnlyList<QwenProductVariant> Variants,
    IReadOnlyList<QwenTargetAttribute> TargetAttributes)
{
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> EvidenceAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> VariantAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static QwenProductMappingTransport Create(ProductMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evidenceAliases = request.Facts.Select((fact, index) => (Alias: $"e{index + 1}", fact.FactId))
            .ToDictionary(item => item.Alias, item => item.FactId, StringComparer.Ordinal);
        var aliasesByFact = evidenceAliases.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var variantAliases = request.Skus.Select((sku, index) => (Alias: $"v{index + 1}", sku.VariantKey))
            .ToDictionary(item => item.Alias, item => item.VariantKey, StringComparer.Ordinal);

        QwenProductFact Fact(ProductMappingFact fact) =>
            new(aliasesByFact[fact.FactId], $"{fact.Label}：{fact.Value}");

        return new(request.RequestId, request.CategoryPath,
            request.Facts.Where(fact => fact.ScopeKey == ProductMappingInputBuilder.ProductScope).Select(Fact).ToArray(),
            variantAliases.Select(item => new QwenProductVariant(item.Key,
                request.Facts.Where(fact => fact.ScopeKey == item.Value).Select(Fact).ToArray())).ToArray(),
            request.Attributes.Select(attribute => new QwenTargetAttribute(
                attribute.AttributeId,
                attribute.AttributeComplexId,
                attribute.Name,
                attribute.Description,
                attribute.Type,
                attribute.IsCollection,
                attribute.IsRequired,
                attribute.MaxValueCount,
                attribute.DictionaryId > 0,
                attribute.DictionaryCandidates)).ToArray())
        {
            EvidenceAliases = evidenceAliases,
            VariantAliases = variantAliases,
        };
    }

    public ProductMappingResponse RestoreAliases(ProductMappingResponse response)
    {
        var evidenceAliases = EvidenceAliases.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        var variantAliases = VariantAliases.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        string RestoreEvidence(string id) => evidenceAliases.TryGetValue(id, out var factId) ? factId : id;
        var product = response.ProductMappings.Select(RestoreMapping).ToArray();
        var variants = response.Variants.Select(variant => new ProductVariantSuggestion(
            variantAliases.TryGetValue(variant.VariantKey, out var key) ? key : variant.VariantKey,
            variant.Mappings.Select(mapping => ApplyEvidenceAliases(mapping, RestoreEvidence)).ToArray())).ToArray();
        return response with { ProductMappings = product, Variants = variants };

        ProductAttributeSuggestion RestoreMapping(ProductAttributeSuggestion mapping) =>
            ApplyEvidenceAliases(mapping, RestoreEvidence);
    }

    private static ProductAttributeSuggestion ApplyEvidenceAliases(ProductAttributeSuggestion mapping,
        Func<string, string> restoreEvidence) => mapping with
    {
        EvidenceFactIds = mapping.EvidenceFactIds
            .Select(restoreEvidence).ToArray(),
    };
}

public sealed record QwenProductFact(string EvidenceId, string Text);

public sealed record QwenProductVariant(
    string VariantId,
    IReadOnlyList<QwenProductFact> Facts);

public sealed record QwenTargetAttribute(
    long AttributeId,
    long ComplexId,
    string Name,
    string Description,
    string Type,
    bool IsCollection,
    bool IsRequired,
    int MaxValueCount,
    bool UsesDictionary,
    IReadOnlyList<ProductDictionaryCandidate> DictionaryCandidates);
