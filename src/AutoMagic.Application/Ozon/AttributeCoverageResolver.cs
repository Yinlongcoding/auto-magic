using System.Globalization;
using System.Text;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Application.Ozon;

public static class AttributeCoverageResolver
{
    private const decimal MinimumAutomaticScore = 0.90m;
    private const decimal MinimumReviewScore = 0.70m;

    private static readonly HashSet<string> GenderAliases =
        Aliases("gender", "适用性别", "性别", "пол");

    private static readonly IReadOnlyList<HashSet<string>> AliasGroups =
    [
        Aliases("material", "材质", "面料", "面料名称", "主面料成分", "материал"),
        Aliases("brand", "品牌", "品牌名称", "服装和鞋类品牌", "бренд", "торговаямарка"),
        Aliases("color", "colour", "颜色", "商品颜色", "色彩", "цвет", "цветтовара"),
        Aliases("size", "尺寸", "尺码", "размер"),
        Aliases("countryoforigin", "origin", "产地", "原产地", "странапроисхождения"),
        GenderAliases,
        Aliases("season", "季节", "适用季节", "сезон"),
        Aliases("shelflife", "保质期", "срокгодности"),
        Aliases("productiondate", "生产日期", "生成日期", "датапроизводства"),
        Aliases("standard", "执行标准", "产品标准", "стандарт"),
        Aliases("composition", "成分", "配料", "配料表", "состав", "составпродукта"),
        Aliases("dresslength", "裙长", "裙子长度", "连衣裙长度", "裙子连衣裙长度", "длинаплатья"),
        Aliases("collar", "衣领", "领型", "领口", "领口类型", "воротник"),
        Aliases("sleevetype", "袖型", "袖子类型", "套筒类型", "типрукава"),
        Aliases("sleevelength", "袖长", "袖子长度", "длинарукава"),
        Aliases("style", "风格", "款式", "стиль"),
        Aliases("pattern", "图案", "花型", "узор"),
        Aliases("fit", "版型", "剪裁", "посадка"),
        Aliases("closure", "闭合方式", "门襟", "开合方式", "застежка"),
        Aliases("purpose", "用途", "适用场景", "使用场景", "场合", "назначение"),
        Aliases("height", "身高", "适用身高", "建议身高", "рост"),
        Aliases("warranty", "保证", "质保", "保修", "保修期", "гарантия"),
        Aliases("factorypackagequantity", "原厂包装数量", "包装数量", "单包装数量", "装箱数"),
        Aliases(
            "quantityinunifiedunit",
            "统一计量单位中的商品数量",
            "计量单位中的商品数量",
            "计量单位商品数量"),
    ];

    private static readonly HashSet<string> UnsafeContainmentTerms = Aliases(
        "类型", "名称", "商品", "产品", "其他", "信息", "说明", "参数", "规格", "数值", "单位");

    public static AttributeCoverageReport Resolve(
        OzonCategorySchema schema,
        DetailFactSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(snapshot);

        var usableFacts = BuildFacts(snapshot)
            .Where(fact => !string.IsNullOrWhiteSpace(fact.Label) && !string.IsNullOrWhiteSpace(fact.Value))
            .Select(fact => new IndexedFact(fact, Normalize(fact.Label)))
            .Where(fact => fact.NormalizedLabel.Length > 0)
            .ToArray();

        var resolutions = schema.Attributes.Select(attribute =>
        {
            var match = FindBestMatch(attribute.Name, usableFacts);
            if (match is null || match.Score < MinimumReviewScore)
            {
                return Resolution(
                    attribute,
                    AttributeResolutionStatuses.Missing,
                    match,
                    match?.Score ?? 0m);
            }

            if (match.Score < MinimumAutomaticScore)
            {
                return Resolution(
                    attribute,
                    AttributeResolutionStatuses.ReviewRequired,
                    match,
                    match.Score);
            }

            var status = attribute.DictionaryId > 0
                ? AttributeResolutionStatuses.DictionaryValueRequired
                : AttributeResolutionStatuses.Resolved;
            return Resolution(attribute, status, match, match.Score);
        }).ToArray();

        return new AttributeCoverageReport(
            schema.DescriptionCategoryId,
            schema.TypeId,
            snapshot.DetailUrl,
            DateTimeOffset.UtcNow,
            resolutions);
    }

    private static AttributeResolution Resolution(
        OzonAttributeDefinition attribute,
        string status,
        Match? match,
        decimal confidence) =>
        new(
            attribute.Id,
            attribute.Name,
            attribute.IsRequired,
            attribute.DictionaryId,
            status,
            match?.Fact.Label,
            match?.Fact.Value,
            match?.Fact.Source,
            confidence,
            match?.Method);

    private static IEnumerable<DetailFactDto> BuildFacts(DetailFactSnapshotDto snapshot)
    {
        foreach (var fact in snapshot.Facts)
        {
            yield return fact;
        }

        if (snapshot.Facts.Any(fact =>
                GenderAliases.Contains(Normalize(fact.Label)) &&
                !string.IsNullOrWhiteSpace(fact.Value)))
        {
            yield break;
        }

        var title = snapshot.PageTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = snapshot.Facts.FirstOrDefault(fact =>
                Normalize(fact.Label) is "商品标题" or "商品名称" or "标题" or "产品标题")?.Value;
        }

        var inferredGender = InferGender(title);
        if (inferredGender is not null)
        {
            yield return new DetailFactDto("适用性别", inferredGender, "inference:title");
        }
    }

    private static string? InferGender(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = Normalize(title);
        if (normalized.Contains("男女", StringComparison.Ordinal))
        {
            return null;
        }

        var hasFemale = normalized.Contains('女');
        var hasMale = normalized.Contains('男');
        return (hasFemale, hasMale) switch
        {
            (true, false) => "女",
            (false, true) => "男",
            _ => null,
        };
    }

    private static Match? FindBestMatch(string attributeName, IEnumerable<IndexedFact> facts)
    {
        var normalizedAttribute = Normalize(attributeName);
        if (normalizedAttribute.Length == 0)
        {
            return null;
        }

        Match? best = null;
        foreach (var fact in facts)
        {
            var labelMatch = Score(normalizedAttribute, fact.NormalizedLabel);
            if (labelMatch.Score == 0m)
            {
                continue;
            }

            var score = Math.Min(labelMatch.Score, EvidenceConfidence(fact.Fact.Source));
            if (best is null || score > best.Score)
            {
                best = new Match(fact.Fact, score, labelMatch.Method);
            }
        }

        return best;
    }

    private static LabelMatch Score(string attribute, string fact)
    {
        if (attribute == fact)
        {
            return new LabelMatch(1m, AttributeMatchMethods.Exact);
        }

        if (AliasGroups.Any(group => group.Contains(attribute) && group.Contains(fact)))
        {
            return new LabelMatch(0.97m, AttributeMatchMethods.Alias);
        }

        var shorter = attribute.Length <= fact.Length ? attribute : fact;
        if (shorter.Length >= 2 &&
            !UnsafeContainmentTerms.Contains(shorter) &&
            (attribute.Contains(fact, StringComparison.Ordinal) ||
             fact.Contains(attribute, StringComparison.Ordinal)))
        {
            return new LabelMatch(0.92m, AttributeMatchMethods.Containment);
        }

        var similarity = BigramDice(attribute, fact);
        if (similarity >= 0.35m)
        {
            return new LabelMatch(
                Math.Min(0.89m, 0.60m + (0.30m * similarity)),
                AttributeMatchMethods.BigramCandidate);
        }

        return new LabelMatch(0m, AttributeMatchMethods.None);
    }

    private static decimal BigramDice(string left, string right)
    {
        var leftBigrams = Bigrams(left);
        var rightBigrams = Bigrams(right);
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return 0m;
        }

        var overlap = leftBigrams.Intersect(rightBigrams, StringComparer.Ordinal).Count();
        return (2m * overlap) / (leftBigrams.Count + rightBigrams.Count);
    }

    private static HashSet<string> Bigrams(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < value.Length - 1; index++)
        {
            result.Add(value.Substring(index, 2));
        }

        return result;
    }

    private static decimal EvidenceConfidence(string source) =>
        source switch
        {
            "dom-pair" => 0.98m,
            "embedded-json" => 0.95m,
            "inference:title" => 0.92m,
            _ => 0.95m,
        };

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (char.IsLetterOrDigit(character) || category is UnicodeCategory.OtherLetter)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static HashSet<string> Aliases(params string[] values) =>
        values.Select(Normalize).ToHashSet(StringComparer.Ordinal);

    private sealed record IndexedFact(DetailFactDto Fact, string NormalizedLabel);

    private sealed record LabelMatch(decimal Score, string Method);

    private sealed record Match(DetailFactDto Fact, decimal Score, string Method);
}

public static class AttributeResolutionStatuses
{
    public const string Resolved = "resolved";
    public const string DictionaryValueRequired = "dictionaryValueRequired";
    public const string ReviewRequired = "reviewRequired";
    public const string Missing = "missing";
}

public static class AttributeMatchMethods
{
    public const string None = "none";
    public const string Exact = "exact";
    public const string Alias = "semanticAlias";
    public const string Containment = "safeContainment";
    public const string BigramCandidate = "bigramCandidate";
}

public sealed record AttributeCoverageReport(
    long DescriptionCategoryId,
    long TypeId,
    string DetailUrl,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AttributeResolution> Attributes)
{
    public int RequiredCount => Attributes.Count(attribute => attribute.IsRequired);

    public int ReadyRequiredCount => Attributes.Count(attribute =>
        attribute.IsRequired && attribute.Status == AttributeResolutionStatuses.Resolved);

    public int MissingRequiredCount => Attributes.Count(attribute =>
        attribute.IsRequired && attribute.Status == AttributeResolutionStatuses.Missing);

    public int DictionaryRequiredCount => Attributes.Count(attribute =>
        attribute.IsRequired && attribute.Status == AttributeResolutionStatuses.DictionaryValueRequired);

    public int ReviewRequiredCount => Attributes.Count(attribute =>
        attribute.IsRequired && attribute.Status == AttributeResolutionStatuses.ReviewRequired);

    public bool IsReadyForSubmission => RequiredCount > 0 && ReadyRequiredCount == RequiredCount;
}

public sealed record AttributeResolution(
    long AttributeId,
    string AttributeName,
    bool IsRequired,
    long DictionaryId,
    string Status,
    string? SourceLabel,
    string? SourceValue,
    string? Source,
    decimal Confidence,
    string? MatchMethod);
