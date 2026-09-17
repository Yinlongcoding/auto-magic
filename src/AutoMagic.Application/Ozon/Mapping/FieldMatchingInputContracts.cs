using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Application.Ozon.Mapping;

public sealed record FieldMatchingInput(
    string ContractVersion,
    string MappingJobId,
    string CollectionId,
    FieldMatchingProductRef ProductRef,
    FieldMatchingSource Source,
    FieldMatchingTarget Target,
    FieldMatchingRules Rules);

public sealed record FieldMatchingProductRef(
    int ItemIndex,
    int ItemPosition,
    string OfferId,
    string DetailUrl,
    string? CapturedAt,
    string CaptureStatus);

public sealed record FieldMatchingSource(
    string Platform,
    string Language,
    string Title,
    IReadOnlyList<FieldMatchingSourceFact> Facts,
    IReadOnlyList<FieldMatchingMediaEvidence> Media,
    IReadOnlyList<FieldMatchingTextEvidence> PriceEvidence,
    IReadOnlyList<FieldMatchingTextEvidence> SkuEvidence,
    IReadOnlyList<FieldMatchingSkuDimension> SkuDimensions,
    IReadOnlyList<FieldMatchingSkuCombination> SkuCombinations,
    string SkuMatrixStatus);

public sealed record FieldMatchingSkuDimension(
    string Name,
    string Source,
    IReadOnlyList<FieldMatchingSkuOption> Options);

public sealed record FieldMatchingSkuOption(
    string OptionKey,
    string SourceValue,
    string? NormalizedValue,
    string Status);

public sealed record FieldMatchingSkuCombination(
    string CombinationKey,
    string Verification,
    IReadOnlyDictionary<string, string?> Options,
    decimal? Price,
    long? Stock);

public sealed record FieldMatchingSourceFact(
    string FactId,
    string Kind,
    string Label,
    string Value,
    string Source,
    string SourcePath)
{
    public IReadOnlyList<string> DerivedFromFactIds { get; init; } = [];
}

public sealed record FieldMatchingMediaEvidence(
    string MediaId,
    string Kind,
    string Url,
    int Position,
    string SourcePath);

public sealed record FieldMatchingTextEvidence(
    string EvidenceId,
    string Text,
    string SourcePath);

public sealed record FieldMatchingTarget(
    string Platform,
    long? DescriptionCategoryId,
    long? TypeId,
    string? CategoryPath,
    bool CategoryAndTypeConfirmedByUser,
    IReadOnlyList<FieldMatchingTargetAttribute> Attributes);

public sealed record FieldMatchingTargetAttribute(
    long AttributeId,
    long AttributeComplexId,
    string Name,
    string Description,
    string Type,
    string GroupName,
    bool IsCollection,
    bool IsRequired,
    int MaxValueCount,
    long DictionaryId,
    IReadOnlyList<SemanticDictionaryCandidate> DictionaryCandidates);

public sealed record FieldMatchingRules(
    bool PreserveSourceLanguage,
    bool AllowSyntheticFacts,
    bool AllowSyntheticSku,
    bool RequireEvidenceForMappedValue,
    string DefaultOriginCountry,
    string CardGroupingStrategy);

public static partial class FieldMatchingInputBuilder
{
    public static FieldMatchingInput Create(
        string collectionId,
        string mappingJobId,
        DetailCollectionResultDto detail,
        OzonCategorySchema? targetSchema = null,
        string? categoryPath = null,
        bool categoryAndTypeConfirmedByUser = false)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var detailUrl = detail.FinalUrl ?? detail.DetailUrl ?? string.Empty;
        var rawFacts = detail.Facts.Select((fact, index) => new FieldMatchingSourceFact(
            $"f{index + 1:000}",
            IsTitle(fact.Label) ? "title" : "attribute",
            fact.Label,
            fact.Value,
            fact.Source,
            $"$.facts[{index}]")).ToList();
        var titleContext = string.Join(" ", new[] { detail.ProductTitle, detail.PageTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var facts = AddDerivedFacts(rawFacts, titleContext, ReadColorOptions(detail.Raw, rawFacts));

        var media = ReadStringArray(detail.Raw, "imageUrls")
            .Select((url, index) => new FieldMatchingMediaEvidence(
                $"m{index + 1:000}",
                "source_image",
                url,
                index + 1,
                $"$.raw.imageUrls[{index}]"))
            .ToArray();
        var prices = ReadStringArray(detail.Raw, "priceTexts")
            .Select((text, index) => new FieldMatchingTextEvidence(
                $"p{index + 1:000}", text, $"$.raw.priceTexts[{index}]"))
            .ToArray();
        var skus = ReadStringArray(detail.Raw, "skuTexts")
            .Select((text, index) => new FieldMatchingTextEvidence(
                $"s{index + 1:000}", text, $"$.raw.skuTexts[{index}]"))
            .ToArray();
        var skuDimensions = ReadSkuDimensions(detail.Raw);
        var skuCombinations = ReadSkuCombinations(detail.Raw);
        var skuMatrixStatus = ReadString(detail.Raw, "skuMatrixStatus") ?? "not_collected";

        var targetAttributes = targetSchema?.Attributes.Select(attribute =>
            new FieldMatchingTargetAttribute(
                attribute.Id,
                attribute.AttributeComplexId,
                attribute.Name,
                attribute.Description,
                attribute.Type,
                attribute.GroupName,
                attribute.IsCollection,
                attribute.IsRequired,
                attribute.MaxValueCount,
                attribute.DictionaryId,
                [])).ToArray() ?? [];

        return new FieldMatchingInput(
            "1.0",
            mappingJobId.Trim(),
            collectionId.Trim(),
            new FieldMatchingProductRef(
                detail.ItemIndex,
                detail.ItemPosition,
                ExtractOfferId(detailUrl),
                detailUrl,
                detail.CapturedAt,
                detail.Status),
            new FieldMatchingSource(
                "1688",
                "zh-CN",
                detail.ProductTitle ?? detail.PageTitle ?? string.Empty,
                facts,
                media,
                prices,
                skus,
                skuDimensions,
                skuCombinations,
                skuMatrixStatus),
            new FieldMatchingTarget(
                "Ozon",
                targetSchema?.DescriptionCategoryId,
                targetSchema?.TypeId,
                string.IsNullOrWhiteSpace(categoryPath) ? null : categoryPath.Trim(),
                categoryAndTypeConfirmedByUser,
                targetAttributes),
            new FieldMatchingRules(
                true,
                false,
                false,
                true,
                "China",
                "sourceOfferId"));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement? raw, string propertyName)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string? ReadString(JsonElement? raw, string propertyName) =>
        raw is { ValueKind: JsonValueKind.Object } root &&
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IReadOnlyList<FieldMatchingSkuDimension> ReadSkuDimensions(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("skuDimensions", out var dimensions) ||
            dimensions.ValueKind != JsonValueKind.Array) return [];

        return dimensions.EnumerateArray().Select(dimension => new FieldMatchingSkuDimension(
                dimension.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                dimension.TryGetProperty("source", out var source) ? source.GetString() ?? string.Empty : string.Empty,
                dimension.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                    ? options.EnumerateArray().Select(option => new FieldMatchingSkuOption(
                        option.TryGetProperty("optionKey", out var key) ? key.GetString() ?? string.Empty : string.Empty,
                        option.TryGetProperty("sourceValue", out var sourceValue) ? sourceValue.GetString() ?? string.Empty : string.Empty,
                        option.TryGetProperty("normalizedValue", out var normalizedValue) && normalizedValue.ValueKind == JsonValueKind.String
                            ? normalizedValue.GetString()
                            : null,
                        option.TryGetProperty("status", out var status) ? status.GetString() ?? string.Empty : string.Empty)).ToArray()
                    : []))
            .Where(dimension => !string.IsNullOrWhiteSpace(dimension.Name))
            .ToArray();
    }

    private static IReadOnlyList<FieldMatchingSkuCombination> ReadSkuCombinations(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("skuCombinations", out var combinations) ||
            combinations.ValueKind != JsonValueKind.Array) return [];

        return combinations.EnumerateArray().Select(combination =>
        {
            var options = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (combination.TryGetProperty("options", out var optionObject) &&
                optionObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in optionObject.EnumerateObject())
                {
                    options[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                }
            }

            decimal? price = combination.TryGetProperty("price", out var priceValue) &&
                             priceValue.TryGetDecimal(out var parsedPrice) ? parsedPrice : null;
            long? stock = combination.TryGetProperty("stock", out var stockValue) &&
                          stockValue.TryGetInt64(out var parsedStock) ? parsedStock : null;
            return new FieldMatchingSkuCombination(
                combination.TryGetProperty("combinationKey", out var key) ? key.GetString() ?? string.Empty : string.Empty,
                combination.TryGetProperty("verification", out var verification) ? verification.GetString() ?? string.Empty : string.Empty,
                options,
                price,
                stock);
        }).Where(combination => !string.IsNullOrWhiteSpace(combination.CombinationKey)).ToArray();
    }

    private static bool IsTitle(string label) =>
        label.Trim() is "商品名称" or "商品标题" or "标题" or "产品标题";

    private static IReadOnlyList<FieldMatchingSourceFact> AddDerivedFacts(
        List<FieldMatchingSourceFact> facts,
        string title,
        IReadOnlyList<NormalizedColorOption> colorOptions)
    {
        var nextId = facts.Count + 1;
        if (!facts.Any(fact => fact.Label.Trim() is "性别" or "适用性别" or "适用人群"))
        {
            var gender = InferGender(title);
            if (gender is not null)
            {
                var titleFact = facts.FirstOrDefault(fact => fact.Kind == "title");
                facts.Add(new FieldMatchingSourceFact(
                    $"f{nextId++:000}", "derived", "适用性别", gender,
                    "inference:title", "$.derivedFacts.gender")
                {
                    DerivedFromFactIds = titleFact is null ? [] : [titleFact.FactId],
                });
            }
        }

        var origin = facts.FirstOrDefault(fact =>
            fact.Label.Trim() is "产地" or "原产地" or "原产国");
        if (origin is not null && IsChinaLocation(origin.Value) &&
            !string.Equals(origin.Value.Trim(), "China", StringComparison.OrdinalIgnoreCase))
        {
            facts.Add(new FieldMatchingSourceFact(
                $"f{nextId++:000}", "derived", "原产国", "China",
                "conversion:origin-country", "$.derivedFacts.originCountry")
            {
                DerivedFromFactIds = [origin.FactId],
            });
        }

        var colorFact = facts.FirstOrDefault(fact =>
            fact.Label.Trim() is "颜色" or "颜色分类" or "色彩");
        if (colorFact is not null)
        {
            var singleUnchanged = colorOptions.Count == 1 &&
                                  string.Equals(
                                      colorOptions[0].SourceValue,
                                      colorOptions[0].NormalizedValue,
                                      StringComparison.Ordinal);
            if (singleUnchanged) return facts;

            for (var index = 0; index < colorOptions.Count; index++)
            {
                var option = colorOptions[index];
                var normalized = !string.IsNullOrWhiteSpace(option.NormalizedValue);
                facts.Add(new FieldMatchingSourceFact(
                    $"f{nextId++:000}",
                    normalized ? "derived" : "unresolved",
                    normalized ? "颜色" : "未识别颜色选项",
                    normalized ? option.NormalizedValue! : option.SourceValue,
                    normalized ? "normalization:color-options" : "normalization:color-options-unresolved",
                    $"$.raw.colorOptions[{index}]")
                {
                    DerivedFromFactIds = [colorFact.FactId],
                });
            }
        }

        return facts;
    }

    private static IReadOnlyList<NormalizedColorOption> ReadColorOptions(
        JsonElement? raw,
        IReadOnlyList<FieldMatchingSourceFact> facts)
    {
        if (raw is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty("colorOptions", out var property) &&
            property.ValueKind == JsonValueKind.Array)
        {
            var parsed = property.EnumerateArray().Select(item => new NormalizedColorOption(
                    item.TryGetProperty("sourceValue", out var source) ? source.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("normalizedValue", out var normalized) && normalized.ValueKind == JsonValueKind.String
                        ? normalized.GetString()
                        : null))
                .Where(option => !string.IsNullOrWhiteSpace(option.SourceValue))
                .ToArray();
            if (parsed.Length > 0) return parsed;
        }

        var colorFact = facts.FirstOrDefault(fact => fact.Label.Trim() is "颜色" or "颜色分类" or "色彩");
        return colorFact is null ? [] : NormalizeColorOptions(colorFact.Value);
    }

    private static IReadOnlyList<NormalizedColorOption> NormalizeColorOptions(string rawValue) =>
        rawValue.Normalize(NormalizationForm.FormKC)
            .Split(['、', ',', '，', ';', '；', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(sourceValue =>
            {
                var match = ColorOptionRegex().Match(sourceValue);
                var normalized = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
                return new NormalizedColorOption(sourceValue.Trim(), normalized.Length == 0 ? null : normalized);
            })
            .ToArray();

    private static string? InferGender(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Contains("男女", StringComparison.Ordinal)) return null;
        var female = title.Contains('女');
        var male = title.Contains('男');
        return (female, male) switch
        {
            (true, false) => "女",
            (false, true) => "男",
            _ => null,
        };
    }

    private static bool IsChinaLocation(string value)
    {
        string[] regions =
        [
            "中国", "china", "浙江", "江苏", "广东", "福建", "山东", "河北", "河南", "湖北", "湖南",
            "安徽", "江西", "四川", "重庆", "上海", "北京", "天津", "辽宁", "吉林", "黑龙江", "陕西",
            "山西", "甘肃", "云南", "贵州", "广西", "海南", "青海", "宁夏", "新疆", "西藏", "内蒙古",
        ];
        return regions.Any(region => value.Contains(region, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractOfferId(string detailUrl)
    {
        var pathMatch = OfferPathRegex().Match(detailUrl);
        if (pathMatch.Success)
        {
            return pathMatch.Groups[1].Value;
        }

        if (Uri.TryCreate(detailUrl, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in query)
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 &&
                    string.Equals(pair[0], "offerId", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }
        }

        return string.Empty;
    }

    [GeneratedRegex(@"/offer/(\d+)(?:\.html)?(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfferPathRegex();

    [GeneratedRegex(@"^\s*\d*\s*(.*?)\s*(?:有现货|现货|缺货|无货|库存\s*\d+\s*(?:件|个)?)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorOptionRegex();

    private sealed record NormalizedColorOption(string SourceValue, string? NormalizedValue);
}
