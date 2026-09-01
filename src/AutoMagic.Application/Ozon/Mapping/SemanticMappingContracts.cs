using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoMagic.Application.Ozon.Mapping;

public static class SemanticMappingPurposes
{
    public const string EvaluateCandidates = "evaluate_semantic_mapping_candidates";
}

public static class SemanticMappingStatuses
{
    public const string Mapped = "mapped";
    public const string DictionaryPending = "dictionary_pending";
    public const string MissingEvidence = "missing_evidence";
    public const string PolicyRequired = "policy_required";
    public const string ConversionRequired = "conversion_required";
    public const string Ambiguous = "ambiguous";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Mapped,
        DictionaryPending,
        MissingEvidence,
        PolicyRequired,
        ConversionRequired,
        Ambiguous,
    };
}

public static class SemanticMappingMethods
{
    public const string ExactLabel = "exact_label";
    public const string SemanticLabel = "semantic_label";
    public const string ExplicitTitle = "explicit_title";
    public const string CategoryContext = "category_context";
    public const string Policy = "policy";
    public const string Conversion = "conversion";
    public const string None = "none";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ExactLabel,
        SemanticLabel,
        ExplicitTitle,
        CategoryContext,
        Policy,
        Conversion,
        None,
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticMappingRequest(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Purpose,
    [property: JsonRequired] SemanticMappingContext MappingContext,
    [property: JsonRequired] IReadOnlyList<SemanticTargetAttribute> TargetAttributes,
    [property: JsonRequired] IReadOnlyList<SemanticSourceFact> SourceFacts,
    [property: JsonRequired] SemanticMappingOutputContract OutputContract);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticMappingContext(
    [property: JsonRequired] string SourcePlatform,
    [property: JsonRequired] string TargetPlatform,
    long DescriptionCategoryId,
    long TypeId,
    [property: JsonRequired] string SelectedCategoryPath,
    bool CategoryAndTypeConfirmedByUser,
    [property: JsonRequired] string OfferId,
    [property: JsonRequired] string DetailUrl,
    [property: JsonRequired] string DetailCapturedAt,
    int SourceFactCount,
    bool DictionaryCandidatesProvided);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticTargetAttribute(
    long AttributeId,
    long AttributeComplexId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Description,
    [property: JsonRequired] string Type,
    bool IsCollection,
    bool IsRequired,
    int MaxValueCount,
    long DictionaryId,
    [property: JsonRequired] IReadOnlyList<SemanticDictionaryCandidate> DictionaryCandidates);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticDictionaryCandidate(
    long ValueId,
    [property: JsonRequired] string Value);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticSourceFact(
    [property: JsonRequired] string FactId,
    [property: JsonRequired] string Label,
    [property: JsonRequired] string Value,
    [property: JsonRequired] string Source);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticMappingOutputContract(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] IReadOnlyList<SemanticTargetMappingOutputContract> TargetMappings,
    [property: JsonRequired] IReadOnlyList<string> UnmappedSourceFactIds,
    [property: JsonRequired] IReadOnlyList<string> Warnings)
{
    public static SemanticMappingOutputContract Default { get; } = new(
        "string，必须原样返回输入requestId",
        [new SemanticTargetMappingOutputContract(
            "integer",
            "string",
            "mapped | dictionary_pending | missing_evidence | policy_required | conversion_required | ambiguous",
            ["string"],
            ["string"],
            ["string"],
            ["string"],
            ["integer，只能来自dictionaryCandidates"],
            "exact_label | semantic_label | explicit_title | category_context | policy | conversion | none",
            "number，0到1",
            "boolean",
            "boolean",
            "string，简洁中文")],
        ["string"],
        ["string"]);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticTargetMappingOutputContract(
    [property: JsonRequired] string AttributeId,
    [property: JsonRequired] string AttributeName,
    [property: JsonRequired] string Status,
    [property: JsonRequired] IReadOnlyList<string> SourceFactIds,
    [property: JsonRequired] IReadOnlyList<string> SourceLabels,
    [property: JsonRequired] IReadOnlyList<string> SourceValues,
    [property: JsonRequired] IReadOnlyList<string> CandidateTextValues,
    [property: JsonRequired] IReadOnlyList<string> SelectedDictionaryValueIds,
    [property: JsonRequired] string MappingMethod,
    [property: JsonRequired] string Confidence,
    [property: JsonRequired] string DictionaryResolutionRequired,
    [property: JsonRequired] string ExternalRuleRequired,
    [property: JsonRequired] string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticMappingResponse(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] IReadOnlyList<SemanticTargetMapping> TargetMappings,
    [property: JsonRequired] IReadOnlyList<string> UnmappedSourceFactIds,
    [property: JsonRequired] IReadOnlyList<string> Warnings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticTargetMapping(
    long AttributeId,
    [property: JsonRequired] string AttributeName,
    [property: JsonRequired] string Status,
    [property: JsonRequired] IReadOnlyList<string> SourceFactIds,
    [property: JsonRequired] IReadOnlyList<string> SourceLabels,
    [property: JsonRequired] IReadOnlyList<string> SourceValues,
    [property: JsonRequired] IReadOnlyList<string> CandidateTextValues,
    [property: JsonRequired] IReadOnlyList<long> SelectedDictionaryValueIds,
    [property: JsonRequired] string MappingMethod,
    decimal Confidence,
    bool DictionaryResolutionRequired,
    bool ExternalRuleRequired,
    [property: JsonRequired] string Reason);

public sealed record SemanticMappingValidationIssue(
    string Code,
    string Path,
    string Message);

public sealed record SemanticMappingValidationResult(
    SemanticMappingResponse? Response,
    IReadOnlyList<SemanticMappingValidationIssue> Issues)
{
    public bool IsValid => Response is not null && Issues.Count == 0;
}

public static class SemanticMappingJson
{
    public static JsonSerializerOptions StrictOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static JsonSerializerOptions IndentedOptions { get; } = new(StrictOptions)
    {
        WriteIndented = true,
    };
}
