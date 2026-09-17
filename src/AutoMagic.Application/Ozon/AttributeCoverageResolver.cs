using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Application.Ozon;

public static class AttributeCoverageResolver
{
    private const decimal MinimumAutomaticScore = 0.90m;
    private const decimal MinimumReviewScore = 0.70m;

    private static readonly HashSet<string> GenderAliases =
        Aliases("gender", "适用性别", "性别", "пол");

    private static readonly HashSet<string> SizeAliases =
        Aliases("size", "尺寸", "尺码", "размер");

    private static readonly HashSet<string> OriginAliases =
        Aliases("countryoforigin", "origin", "产地", "原产地", "原产国", "странапроисхождения");

    private static readonly HashSet<string> BrandAliases =
        Aliases("brand", "品牌", "品牌名称", "服装和鞋类品牌", "бренд", "торговаямарка");

    private static readonly IReadOnlyList<HashSet<string>> AliasGroups =
    [
        Aliases("name", "名称", "商品名称", "商品标题", "标题", "产品名称", "产品标题"),
        Aliases("material", "材料", "材质", "面料", "面料名称", "主面料成分", "материал"),
        BrandAliases,
        Aliases("color", "colour", "颜色", "商品颜色", "色彩", "цвет", "цветтовара"),
        SizeAliases,
        OriginAliases,
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
            var policyResolution = ResolvePolicyAttribute(attribute, snapshot, usableFacts);
            if (policyResolution is not null)
            {
                return policyResolution;
            }

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

            if (IsUnknownBrand(attribute.Name, match.Fact.Value))
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

    private static AttributeResolution? ResolvePolicyAttribute(
        OzonAttributeDefinition attribute,
        DetailFactSnapshotDto snapshot,
        IReadOnlyList<IndexedFact> facts)
    {
        var target = Normalize(attribute.Name);
        if (attribute.Id == 8292 || target is "合并至一张卡片" or "合成至一张卡片")
        {
            var offerId = ExtractOfferId(snapshot.DetailUrl);
            return string.IsNullOrWhiteSpace(offerId)
                ? PolicyResolution(attribute, AttributeResolutionStatuses.PolicyRequired, null, 0m, "policy:ozon-card-group")
                : PolicyResolution(
                    attribute,
                    AttributeResolutionStatuses.Resolved,
                    $"AM-1688-{offerId}",
                    1m,
                    "policy:ozon-card-group");
        }

        if (attribute.Id == 4295 || target is "俄罗斯尺码" or "russiansize")
        {
            var sizeFact = facts.FirstOrDefault(fact => SizeAliases.Contains(fact.NormalizedLabel));
            return sizeFact is null
                ? null
                : new AttributeResolution(
                    attribute.Id,
                    attribute.Name,
                    attribute.IsRequired,
                    attribute.DictionaryId,
                    AttributeResolutionStatuses.ConversionRequired,
                    sizeFact.Fact.Label,
                    sizeFact.Fact.Value,
                    sizeFact.Fact.Source,
                    Math.Min(0.95m, EvidenceConfidence(sizeFact.Fact.Source)),
                    AttributeMatchMethods.Conversion);
        }

        if (OriginAliases.Contains(target))
        {
            var originFact = facts.FirstOrDefault(fact => OriginAliases.Contains(fact.NormalizedLabel));
            var value = originFact is null || IsChinaLocation(originFact.Fact.Value)
                ? "China"
                : originFact.Fact.Value;
            var source = originFact is null ? "policy:default-origin" : "conversion:origin-country";
            return new AttributeResolution(
                attribute.Id,
                attribute.Name,
                attribute.IsRequired,
                attribute.DictionaryId,
                attribute.DictionaryId > 0
                    ? AttributeResolutionStatuses.DictionaryValueRequired
                    : AttributeResolutionStatuses.Resolved,
                originFact?.Fact.Label ?? "默认原产国",
                value,
                source,
                originFact is null ? 0.90m : 0.98m,
                originFact is null ? AttributeMatchMethods.Policy : AttributeMatchMethods.Conversion);
        }

        if (attribute.Id == 8229)
        {
            return PolicyResolution(
                attribute,
                AttributeResolutionStatuses.PolicyRequired,
                null,
                0m,
                "policy:ozon-product-type");
        }

        return null;
    }

    private static AttributeResolution PolicyResolution(
        OzonAttributeDefinition attribute,
        string status,
        string? value,
        decimal confidence,
        string source) =>
        new(
            attribute.Id,
            attribute.Name,
            attribute.IsRequired,
            attribute.DictionaryId,
            status,
            value is null ? null : "系统策略值",
            value,
            source,
            confidence,
            AttributeMatchMethods.Policy);

    private static bool IsUnknownBrand(string attributeName, string sourceValue) =>
        BrandAliases.Contains(Normalize(attributeName)) &&
        Normalize(sourceValue) is "其他" or "其它" or "other" or "unknown";

    private static bool IsChinaLocation(string value)
    {
        var normalized = Normalize(value);
        string[] regions =
        [
            "中国", "china", "浙江", "江苏", "广东", "福建", "山东", "河北", "河南", "湖北", "湖南",
            "安徽", "江西", "四川", "重庆", "上海", "北京", "天津", "辽宁", "吉林", "黑龙江", "陕西",
            "山西", "甘肃", "云南", "贵州", "广西", "海南", "青海", "宁夏", "新疆", "西藏", "内蒙古",
        ];
        return regions.Any(normalized.Contains);
    }

    private static string ExtractOfferId(string detailUrl)
    {
        var pathMatch = Regex.Match(
            detailUrl,
            @"/offer/(\d+)(?:\.html)?(?:[/?#]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (pathMatch.Success)
        {
            return pathMatch.Groups[1].Value;
        }

        if (!Uri.TryCreate(detailUrl, UriKind.Absolute, out var uri)) return string.Empty;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], "offerId", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }
        return string.Empty;
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
            if (best is null ||
                labelMatch.Score > best.LabelScore ||
                (labelMatch.Score == best.LabelScore && score > best.Score))
            {
                best = new Match(fact.Fact, score, labelMatch.Score, labelMatch.Method);
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
        if (UnsafeContainmentTerms.Contains(attribute) || UnsafeContainmentTerms.Contains(fact))
        {
            return new LabelMatch(0m, AttributeMatchMethods.None);
        }
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

    private sealed record Match(DetailFactDto Fact, decimal Score, decimal LabelScore, string Method);
}

public static class AttributeResolutionStatuses
{
    public const string Resolved = "resolved";
    public const string DictionaryValueRequired = "dictionaryValueRequired";
    public const string ReviewRequired = "reviewRequired";
    public const string PolicyRequired = "policyRequired";
    public const string ConversionRequired = "conversionRequired";
    public const string Missing = "missing";
}

public static class AttributeMatchMethods
{
    public const string None = "none";
    public const string Exact = "exact";
    public const string Alias = "semanticAlias";
    public const string Containment = "safeContainment";
    public const string BigramCandidate = "bigramCandidate";
    public const string Policy = "policy";
    public const string Conversion = "conversion";
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

    public int PolicyRequiredCount => Attributes.Count(attribute =>
        attribute.IsRequired && attribute.Status == AttributeResolutionStatuses.PolicyRequired);

    public int ConversionRequiredCount => Attributes.Count(attribute =>
        attribute.IsRequired && attribute.Status == AttributeResolutionStatuses.ConversionRequired);

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
