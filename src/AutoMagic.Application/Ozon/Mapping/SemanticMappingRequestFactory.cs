using System.Text.RegularExpressions;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Application.Ozon.Mapping;

public static partial class SemanticMappingRequestFactory
{
    private static readonly HashSet<string> TitleLabels = new(StringComparer.Ordinal)
    {
        "商品名称",
        "商品标题",
        "标题",
        "产品标题",
    };

    public static SemanticMappingRequest CreateRequiredAttributeRequest(
        string requestId,
        string selectedCategoryPath,
        OzonCategorySchema schema,
        DetailFactSnapshotDto snapshot,
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>? dictionaryCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(snapshot);

        var sourceFacts = BuildSourceFacts(snapshot);
        var targetAttributes = schema.Attributes
            .Where(attribute => attribute.IsRequired)
            .Select(attribute => new SemanticTargetAttribute(
                attribute.Id,
                attribute.AttributeComplexId,
                attribute.Name,
                attribute.Description,
                attribute.Type,
                attribute.IsCollection,
                attribute.IsRequired,
                attribute.MaxValueCount,
                attribute.DictionaryId,
                GetDictionaryCandidates(attribute.Id, dictionaryCandidates)))
            .ToArray();

        var request = new SemanticMappingRequest(
            requestId.Trim(),
            SemanticMappingPurposes.EvaluateCandidates,
            new SemanticMappingContext(
                "1688",
                "Ozon",
                schema.DescriptionCategoryId,
                schema.TypeId,
                selectedCategoryPath.Trim(),
                true,
                ExtractOfferId(snapshot.DetailUrl),
                snapshot.DetailUrl,
                snapshot.CapturedAt,
                sourceFacts.Count,
                targetAttributes.Any(attribute => attribute.DictionaryCandidates.Count > 0)),
            targetAttributes,
            sourceFacts,
            SemanticMappingOutputContract.Default);

        var validation = SemanticMappingResponseValidator.ValidateRequest(request);
        if (validation.Count > 0)
        {
            throw new ArgumentException(
                $"语义映射请求无效：{string.Join("；", validation.Select(issue => issue.Message))}");
        }

        return request;
    }

    private static IReadOnlyList<SemanticSourceFact> BuildSourceFacts(DetailFactSnapshotDto snapshot)
    {
        var facts = new List<(string Label, string Value, string Source)>();
        if (!string.IsNullOrWhiteSpace(snapshot.PageTitle) &&
            !snapshot.Facts.Any(fact =>
                TitleLabels.Contains(fact.Label.Trim()) &&
                string.Equals(fact.Value.Trim(), snapshot.PageTitle.Trim(), StringComparison.Ordinal)))
        {
            facts.Add(("商品名称", snapshot.PageTitle.Trim(), "document.title"));
        }

        facts.AddRange(snapshot.Facts.Select(fact =>
            (fact.Label.Trim(), fact.Value.Trim(), fact.Source.Trim())));

        return facts
            .Where(fact =>
                !string.IsNullOrWhiteSpace(fact.Item1) &&
                !string.IsNullOrWhiteSpace(fact.Item2) &&
                !string.IsNullOrWhiteSpace(fact.Item3))
            .Select((fact, index) => new SemanticSourceFact(
                $"f{index + 1:000}",
                fact.Item1,
                fact.Item2,
                fact.Item3))
            .ToArray();
    }

    private static IReadOnlyList<SemanticDictionaryCandidate> GetDictionaryCandidates(
        long attributeId,
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>? dictionaryCandidates) =>
        dictionaryCandidates is not null && dictionaryCandidates.TryGetValue(attributeId, out var candidates)
            ? candidates
            : [];

    private static string ExtractOfferId(string detailUrl)
    {
        var match = OfferIdRegex().Match(detailUrl);
        return match.Success
            ? match.Groups[1].Value
            : string.Empty;
    }

    [GeneratedRegex(@"/offer/(\d+)(?:\.html)?(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfferIdRegex();
}
