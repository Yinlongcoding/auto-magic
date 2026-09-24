using System.Text.Json.Serialization;

namespace AutoMagic.Application.Ozon.Mapping;

// Phase 1 is a preview contract. Neither the model nor this pipeline can publish.
public static class ProductMappingStatuses
{
    public const string ManualRequired = "manual_required";
    public const string Suggested = "suggested";
    public const string DictionaryPending = "dictionary_pending";
    public const string MissingEvidence = "missing_evidence";
    public const string Ambiguous = "ambiguous";
    public const string ConversionRequired = "conversion_required";
    public const string PolicyRequired = "policy_required";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Suggested, DictionaryPending, MissingEvidence, Ambiguous, ConversionRequired, PolicyRequired, ManualRequired,
    };
}

public sealed record ProductMappingRequest(
    string ContractVersion,
    string RequestId,
    string CollectionId,
    string SourceOfferId,
    string ProductGroupKey,
    string? SourceCapturedAt,
    string InputFingerprint,
    long DescriptionCategoryId,
    long TypeId,
    string CategoryPath,
    IReadOnlyList<ProductMappingFact> Facts,
    IReadOnlyList<ProductMappingSku> Skus,
    IReadOnlyList<ProductMappingAttribute> Attributes);

public sealed record ProductMappingFact(
    string FactId, string ScopeKey, string Label, string Value, string Source, string SourcePath);

public sealed record ProductMappingSku(
    string VariantKey, string MerchantSku, string IdentityStrategy,
    string SourceCombinationKey, string? SourceSkuId,
    IReadOnlyDictionary<string, string?> Options,
    IReadOnlyList<string> FactIds);

public sealed record ProductSkuIdentityPlan(
    string ProductGroupKey,
    string SourceOfferId,
    IReadOnlyList<ProductSkuIdentityPlanItem> Variants);

public sealed record ProductSkuIdentityPlanItem(
    string VariantKey,
    string MerchantSku,
    string IdentityStrategy,
    string SourceCombinationKey,
    string? SourceSkuId,
    IReadOnlyDictionary<string, string?> Options)
{
    [JsonIgnore]
    public string SourceSkuDisplay => SourceSkuId ?? "无源 SKU ID";

    [JsonIgnore]
    public string OptionsDisplay => string.Join(" / ", Options.Select(option => $"{option.Key}={option.Value}"));
}

public sealed record ProductMappingAttribute(
    long AttributeId, long AttributeComplexId, string Name, string Description, string Type,
    bool IsCollection, bool IsRequired, int MaxValueCount, long DictionaryId,
    IReadOnlyList<ProductDictionaryCandidate> DictionaryCandidates);

public sealed record ProductDictionaryCandidate(long ValueId, string Text);

public sealed record ProductMappingInput(
    ProductMappingRequest Request, IReadOnlyList<ProductMappingIssue> SourceIssues);

// JsonRequired also covers numeric/null-valued fields: missing is not the same as null.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductMappingResponse(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] IReadOnlyList<ProductAttributeSuggestion> ProductMappings,
    [property: JsonRequired] IReadOnlyList<ProductVariantSuggestion> Variants,
    [property: JsonRequired] IReadOnlyList<string> Warnings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductVariantSuggestion(
    [property: JsonRequired] string VariantKey,
    [property: JsonRequired] IReadOnlyList<ProductAttributeSuggestion> Mappings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductAttributeSuggestion(
    [property: JsonRequired] long AttributeId,
    [property: JsonRequired] string? ComplexInstanceKey,
    [property: JsonRequired] string Status,
    [property: JsonRequired] IReadOnlyList<ProductMappingValue> Values,
    [property: JsonRequired] IReadOnlyList<string> EvidenceFactIds,
    [property: JsonRequired] string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductMappingValue(
    [property: JsonRequired] string Text,
    [property: JsonRequired] long? DictionaryValueId);

public sealed record ProductMappingIssue(
    string Severity, string Code, string ScopeKey, long? AttributeId, string Message);

public sealed record ProductMappingRow(
    string ScopeKey, string ScopeDisplay, long AttributeId, string AttributeName,
    bool IsRequired, string Status, string ValueDisplay, string DictionaryIdDisplay,
    string EvidenceDisplay, string CheckDisplay, string Reason);

public sealed record ProductMappingValidation(
    bool ContractValid, int UnresolvedRequiredCount,
    IReadOnlyList<ProductMappingIssue> Issues, IReadOnlyList<ProductMappingRow> Rows);

public sealed record ProductMappingCallResult(
    string ProviderRequestId, string ModelId, string PromptVersion, string RawContent,
    QwenTokenUsage Usage, ProductMappingResponse? Response,
    IReadOnlyList<ProductMappingIssue> ParseIssues);

public interface IProductSemanticMapper
{
    Task<ProductMappingCallResult> MapAsync(
        QwenApiCredentials credentials, ProductMappingRequest request, CancellationToken cancellationToken);
}

public sealed record ProductMappingRun(
    ProductMappingRequest Request, ProductMappingResponse? Response,
    ProductMappingValidation Validation, IReadOnlyList<ProductMappingCallResult> Calls)
{
    public bool ReadyForListing => false;
    public int TotalTokens => Calls.Sum(call => call.Usage.TotalTokens);
    public ProductSkuIdentityPlan SkuIdentityPlan => new(
        Request.ProductGroupKey,
        Request.SourceOfferId,
        Request.Skus.Select(sku => new ProductSkuIdentityPlanItem(
            sku.VariantKey,
            sku.MerchantSku,
            sku.IdentityStrategy,
            sku.SourceCombinationKey,
            sku.SourceSkuId,
            sku.Options)).ToArray());
}
