using System.Globalization;
using System.Text;

namespace AutoMagic.Application.Ozon.Mapping;

public static class RussianSizeConversionStatuses
{
    public const string Mapped = "mapped";
    public const string Ambiguous = "ambiguous";
    public const string MissingRule = "missing_rule";
}

public sealed record RussianSizeRuleSet(
    string Id,
    string DisplayName,
    string SourceReference,
    IReadOnlyList<RussianSizeRule> Rules);

public sealed record RussianSizeRule(
    IReadOnlyList<string> SourceAliases,
    string RussianValue,
    string? InternationalValue,
    decimal? PrimaryMinimum,
    decimal? PrimaryMaximum,
    string PrimaryMeasurement,
    string? SecondaryMinimum,
    string? SecondaryMaximum);

/// <summary>
/// Conversion for one source SKU option. This is never the complete product
/// value when the Ozon attribute is a collection.
/// </summary>
public sealed record RussianSizeConversionResult(
    string Status,
    string RuleSetId,
    string SourceValue,
    IReadOnlyList<string> CandidateRussianValues,
    string Reason)
{
    public bool IsMapped => Status == RussianSizeConversionStatuses.Mapped;
}

public sealed record RussianSizeSourceOption(
    string SkuKey,
    string SourceValue);

public sealed record RussianSizeOptionMapping(
    string SkuKey,
    RussianSizeConversionResult Conversion);

/// <summary>
/// A product-level conversion keeps every source SKU/size option. It must not
/// be collapsed into a single product-wide Russian size.
/// </summary>
public sealed record RussianSizeBatchConversionResult(
    string Status,
    string RuleSetId,
    IReadOnlyList<RussianSizeOptionMapping> Options,
    string Reason)
{
    public bool IsMapped => Status == RussianSizeConversionStatuses.Mapped;
}

/// <summary>
/// Reference rules supplied by the user. These are intentionally scoped by
/// garment/shoe family; a letter such as M is never globally converted.
/// </summary>
public static class RussianSizeRuleCatalog
{
    public const string Version = "ru-size-reference-2026-09-02.v1";

    public static IReadOnlyList<RussianSizeRuleSet> BuiltInV1 { get; } =
    [
        new(
            "women-upper-dress",
            "女装上衣/连衣裙/外套",
            "用户提供的女装对照表（图片 1）",
            [
                Rule(["S", "S(160/84A)"], "42", "XS", 84, 84, "bust_cm", "66", "92"),
                Rule(["M", "M(165/88A)"], "44", "S", 88, 88, "bust_cm", "70", "96"),
                Rule(["L", "L(170/92A)"], "46", "M", 92, 92, "bust_cm", "74", "100"),
                Rule(["XL", "XL(175/96A)"], "48", "L", 96, 96, "bust_cm", "78", "104"),
                Rule(["2XL", "2XL(180/100A)"], "50", "XL", 100, 100, "bust_cm", "82", "108"),
                Rule(["3XL", "3XL(185/104A)"], "52", "XXL", 104, 104, "bust_cm", "86", "112"),
            ]),
        new(
            "women-bottoms",
            "女裤/半身裙",
            "用户提供的女裤/半身裙对照表（图片 2）",
            [
                Rule(["26", "26(1尺9)"], "40-42", "XS", 60, 64, "waist_cm", "86", "90"),
                Rule(["27", "27(2尺0)"], "42-44", "S", 64, 68, "waist_cm", "90", "94"),
                Rule(["28", "28(2尺1)"], "44-46", "M", 68, 72, "waist_cm", "94", "98"),
                Rule(["29", "29(2尺2)"], "46-48", "L", 72, 76, "waist_cm", "98", "102"),
                Rule(["30", "30(2尺3)"], "48-50", "XL", 76, 80, "waist_cm", "102", "106"),
                Rule(["31", "31(2尺4)"], "50-52", "XXL", 80, 84, "waist_cm", "106", "110"),
            ]),
        new(
            "men-upper",
            "男装上衣",
            "用户提供的男装对照表（图片 3）",
            [
                Rule(["M", "M(170/88A)"], "46", "XS", 92, 92, "bust_cm", "38", "38"),
                Rule(["L", "L(175/92A)"], "48", "S", 96, 96, "bust_cm", "39", "39"),
                Rule(["XL", "XL(180/96A)"], "50", "M", 100, 100, "bust_cm", "40", "40"),
                Rule(["2XL", "2XL(185/100A)"], "52", "L", 104, 104, "bust_cm", "41", "41"),
                Rule(["3XL", "3XL(190/104A)"], "54", "XL", 108, 108, "bust_cm", "42", "42"),
                Rule(["4XL", "4XL(195/108A)"], "56", "XXL", 112, 112, "bust_cm", "43", "43"),
            ]),
        new(
            "women-shoes",
            "女鞋",
            "用户提供的女鞋对照表（图片 4）",
            [
                Rule(["225", "22.5"], "35", "36", 22.5m, 22.5m, "foot_length_cm", null, null),
                Rule(["230", "23.0"], "36", "37", 23.0m, 23.0m, "foot_length_cm", null, null),
                Rule(["235", "23.5"], "37", "38", 23.5m, 23.5m, "foot_length_cm", null, null),
                Rule(["240", "24.0"], "38", "39", 24.0m, 24.0m, "foot_length_cm", null, null),
                Rule(["245", "24.5"], "39", "40", 24.5m, 24.5m, "foot_length_cm", null, null),
                Rule(["250", "25.0"], "40", "41", 25.0m, 25.0m, "foot_length_cm", null, null),
            ]),
        new(
            "men-shoes",
            "男鞋",
            "用户提供的男鞋对照表（图片 5）",
            [
                Rule(["245", "24.5"], "39", "40", 24.5m, 24.5m, "foot_length_cm", null, null),
                Rule(["250", "25.0"], "40", "41", 25.0m, 25.0m, "foot_length_cm", null, null),
                Rule(["255", "25.5"], "41", "42", 25.5m, 25.5m, "foot_length_cm", null, null),
                Rule(["260", "26.0"], "42", "43", 26.0m, 26.0m, "foot_length_cm", null, null),
                Rule(["265", "26.5"], "43", "44", 26.5m, 26.5m, "foot_length_cm", null, null),
                Rule(["270", "27.0"], "44", "45", 27.0m, 27.0m, "foot_length_cm", null, null),
            ]),
        new(
            "children-height",
            "童装身高",
            "用户提供的童装身高对照表（图片 6）",
            [
                Rule(["98-104", "98–104"], "30", null, 98, 104, "height_cm", "3", "4"),
                Rule(["110-116", "110–116"], "32", null, 110, 116, "height_cm", "5", "6"),
                Rule(["122-128", "122–128"], "34", null, 122, 128, "height_cm", "7", "8"),
                Rule(["134-140", "134–140"], "36", null, 134, 140, "height_cm", "9", "10"),
                Rule(["146-152", "146–152"], "38", null, 146, 152, "height_cm", "11", "12"),
            ]),
    ];

    public static RussianSizeConversionResult Convert(
        string ruleSetId,
        string sourceValue)
    {
        if (string.IsNullOrWhiteSpace(ruleSetId) || string.IsNullOrWhiteSpace(sourceValue))
        {
            return Missing(ruleSetId, sourceValue, "缺少规则集或源尺码。");
        }

        var ruleSet = BuiltInV1.FirstOrDefault(set =>
            string.Equals(set.Id, ruleSetId.Trim(), StringComparison.Ordinal));
        if (ruleSet is null)
        {
            return Missing(ruleSetId, sourceValue, "未找到已确认的尺码规则集。");
        }

        var matches = ruleSet.Rules
            .Where(rule => rule.SourceAliases.Any(alias => MatchesSource(alias, sourceValue)))
            .Select(rule => rule.RussianValue)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            1 => new RussianSizeConversionResult(
                RussianSizeConversionStatuses.Mapped,
                ruleSet.Id,
                sourceValue.Trim(),
                matches,
                $"命中已确认规则集 {ruleSet.DisplayName}（{Version}）。"),
            > 1 => new RussianSizeConversionResult(
                RussianSizeConversionStatuses.Ambiguous,
                ruleSet.Id,
                sourceValue.Trim(),
                matches,
                "同一源尺码命中多个俄罗斯尺码，需人工确认。"),
            _ => Missing(ruleSet.Id, sourceValue, "规则集中没有该源尺码，不能安全换算。"),
        };
    }

    public static RussianSizeBatchConversionResult ConvertMany(
        string ruleSetId,
        IReadOnlyCollection<RussianSizeSourceOption> sourceOptions)
    {
        ArgumentNullException.ThrowIfNull(sourceOptions);
        if (sourceOptions.Count == 0)
        {
            return new(
                RussianSizeConversionStatuses.MissingRule,
                ruleSetId?.Trim() ?? string.Empty,
                [],
                "商品没有可转换的 SKU 尺码选项。");
        }

        var options = sourceOptions
            .Select(option => new RussianSizeOptionMapping(
                option.SkuKey,
                Convert(ruleSetId, option.SourceValue)))
            .ToArray();
        var status = options.Any(option => option.Conversion.Status == RussianSizeConversionStatuses.MissingRule)
            ? RussianSizeConversionStatuses.MissingRule
            : options.Any(option => option.Conversion.Status == RussianSizeConversionStatuses.Ambiguous)
                ? RussianSizeConversionStatuses.Ambiguous
                : RussianSizeConversionStatuses.Mapped;
        var reason = status switch
        {
            RussianSizeConversionStatuses.Mapped => "所有 SKU 尺码均命中同一已确认规则集，仍须保留逐 SKU 映射。",
            RussianSizeConversionStatuses.Ambiguous => "至少一个 SKU 尺码存在多个候选，需人工确认。",
            _ => "至少一个 SKU 尺码没有安全的转换规则。",
        };
        return new(status, ruleSetId?.Trim() ?? string.Empty, options, reason);
    }

    private static RussianSizeRule Rule(
        IReadOnlyList<string> aliases,
        string russian,
        string? international,
        decimal? primaryMinimum,
        decimal? primaryMaximum,
        string primaryMeasurement,
        string? secondaryMinimum,
        string? secondaryMaximum) =>
        new(aliases, russian, international, primaryMinimum, primaryMaximum, primaryMeasurement, secondaryMinimum, secondaryMaximum);

    private static bool MatchesSource(string alias, string source)
    {
        var normalizedAlias = Normalize(alias);
        var normalizedSource = Normalize(source);
        return normalizedSource == normalizedAlias ||
               normalizedSource.StartsWith(normalizedAlias + "(", StringComparison.Ordinal);
    }

    private static RussianSizeConversionResult Missing(
        string ruleSetId,
        string sourceValue,
        string reason) =>
        new(
            RussianSizeConversionStatuses.MissingRule,
            ruleSetId?.Trim() ?? string.Empty,
            sourceValue?.Trim() ?? string.Empty,
            [],
            reason);

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Trim().ToLower(CultureInfo.InvariantCulture));
        var normalized = new StringBuilder(builder.Length);
        foreach (var character in builder.ToString().Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character))
            {
                continue;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }
}
