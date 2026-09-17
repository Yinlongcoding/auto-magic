using System.Text.RegularExpressions;

namespace AutoMagic.Application.Ozon.Mapping;

public static class ConversionRuleSelectionStatuses
{
    public const string Selected = "selected";
    public const string Ambiguous = "ambiguous";
    public const string MissingContext = "missing_context";
    public const string MissingSourceOptions = "missing_source_options";
    public const string NotApplicable = "not_applicable";
}

public sealed record ConversionRuleSelection(
    long AttributeId,
    string Status,
    string? RuleSetId,
    IReadOnlyList<string> SourceFactIds,
    IReadOnlyList<RussianSizeSourceOption> SourceOptions,
    string Reason)
{
    public bool IsSelected => Status == ConversionRuleSelectionStatuses.Selected;
}

/// <summary>
/// Selects a versioned conversion rule from explicit product context. It does
/// not use model inference and does not convert free-form SKU text into facts.
/// </summary>
public static partial class ConversionRuleSelector
{
    public const long RussianSizeAttributeId = 4295;

    private static readonly HashSet<string> SizeLabels = new(StringComparer.Ordinal)
    {
        "尺码", "服装尺码", "可选尺码", "鞋码", "鞋子尺码", "适用身高", "建议身高",
    };

    private static readonly HashSet<string> GenderLabels = new(StringComparer.Ordinal)
    {
        "性别", "适用性别", "适用人群",
    };

    public static ConversionRuleSelection SelectRussianSize(FieldMatchingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Target.Attributes.Any(attribute => attribute.AttributeId == RussianSizeAttributeId))
        {
            return new ConversionRuleSelection(
                RussianSizeAttributeId,
                ConversionRuleSelectionStatuses.NotApplicable,
                null,
                [],
                [],
                "当前Ozon Schema不包含俄罗斯尺码字段。");
        }

        // Product family is category context, not an arbitrary attribute value.
        // For example, a dress whose length is "短裙" must not become both a
        // dress and a bottoms product.
        var familyContext = $"{input.Target.CategoryPath} {input.Source.Title}";
        var family = ClassifyFamily(familyContext);
        var gender = ClassifyGender(input, familyContext);
        var ruleSetId = SelectRuleSet(family, gender);
        if (ruleSetId is null)
        {
            var ambiguous = family == ProductFamily.Ambiguous || gender == ProductGender.Ambiguous;
            return new ConversionRuleSelection(
                RussianSizeAttributeId,
                ambiguous
                    ? ConversionRuleSelectionStatuses.Ambiguous
                    : ConversionRuleSelectionStatuses.MissingContext,
                null,
                [],
                [],
                ambiguous
                    ? "类目族或性别证据存在冲突，不能唯一选择尺码规则集。"
                    : "当前类目族与性别证据不足，无法自动选择已确认的尺码规则集。");
        }

        var ruleSet = RussianSizeRuleCatalog.BuiltInV1.Single(set => set.Id == ruleSetId);
        var evidence = input.Source.Facts
            .Where(fact => SizeLabels.Contains(fact.Label.Trim()))
            .Select(fact => new
            {
                Fact = fact,
                Values = ExtractSizeValues(fact.Value, ruleSet),
            })
            .Where(item => item.Values.Count > 0)
            .ToArray();
        var options = evidence
            .SelectMany(item => item.Values.Select((value, index) => new RussianSizeSourceOption(
                $"{item.Fact.FactId}:{index + 1:00}",
                value)))
            .GroupBy(option => option.SourceValue, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (options.Length == 0)
        {
            return new ConversionRuleSelection(
                RussianSizeAttributeId,
                ConversionRuleSelectionStatuses.MissingSourceOptions,
                ruleSetId,
                evidence.Select(item => item.Fact.FactId).Distinct(StringComparer.Ordinal).ToArray(),
                [],
                $"已选择规则集{ruleSet.DisplayName}，但结构化商品事实中没有可安全识别的尺码选项。");
        }

        return new ConversionRuleSelection(
            RussianSizeAttributeId,
            ConversionRuleSelectionStatuses.Selected,
            ruleSetId,
            evidence.Select(item => item.Fact.FactId).Distinct(StringComparer.Ordinal).ToArray(),
            options,
            $"根据已确认类目族和性别事实选择规则集{ruleSet.DisplayName}（{RussianSizeRuleCatalog.Version}）。");
    }

    private static ProductFamily ClassifyFamily(string context)
    {
        var matches = new HashSet<ProductFamily>();
        if (ContainsAny(context, "童装", "儿童服装", "男童", "女童")) matches.Add(ProductFamily.Children);
        if (ContainsAny(context, "连衣裙", "上衣", "衬衫", "T恤", "外套", "夹克", "大衣")) matches.Add(ProductFamily.UpperOrDress);
        if (ContainsAny(context, "女裤", "裤装", "半身裙", "短裙")) matches.Add(ProductFamily.Bottoms);
        if (ContainsAny(context, "女鞋", "男鞋", "鞋靴", "鞋子", "运动鞋", "凉鞋")) matches.Add(ProductFamily.Shoes);
        if (matches.Contains(ProductFamily.Children)) return ProductFamily.Children;
        return matches.Count switch
        {
            1 => matches.Single(),
            > 1 => ProductFamily.Ambiguous,
            _ => ProductFamily.Unknown,
        };
    }

    private static ProductGender ClassifyGender(FieldMatchingInput input, string context)
    {
        var explicitValues = input.Source.Facts
            .Where(fact => GenderLabels.Contains(fact.Label.Trim()))
            .Select(fact => fact.Value)
            .ToArray();
        var genderText = explicitValues.Length > 0 ? string.Join(" ", explicitValues) : context;
        var female = ContainsAny(genderText, "女性", "女士", "女装", "女款", "女鞋") ||
                     StandaloneGenderRegex().IsMatch(genderText);
        var male = ContainsAny(genderText, "男性", "男士", "男装", "男款", "男鞋") ||
                   StandaloneMaleRegex().IsMatch(genderText);
        if (ContainsAny(genderText, "男女", "中性", "通用")) return ProductGender.Ambiguous;
        return (female, male) switch
        {
            (true, false) => ProductGender.Female,
            (false, true) => ProductGender.Male,
            (true, true) => ProductGender.Ambiguous,
            _ => ProductGender.Unknown,
        };
    }

    private static string? SelectRuleSet(ProductFamily family, ProductGender gender) =>
        (family, gender) switch
        {
            (ProductFamily.Children, _) => "children-height",
            (ProductFamily.UpperOrDress, ProductGender.Female) => "women-upper-dress",
            (ProductFamily.UpperOrDress, ProductGender.Male) => "men-upper",
            (ProductFamily.Bottoms, ProductGender.Female) => "women-bottoms",
            (ProductFamily.Shoes, ProductGender.Female) => "women-shoes",
            (ProductFamily.Shoes, ProductGender.Male) => "men-shoes",
            _ => null,
        };

    private static IReadOnlyList<string> ExtractSizeValues(
        string source,
        RussianSizeRuleSet ruleSet)
    {
        var matches = new List<string>();
        var residue = source;
        foreach (var rule in ruleSet.Rules)
        {
            var matchedAlias = rule.SourceAliases
                .OrderByDescending(alias => alias.Length)
                .FirstOrDefault(alias => AliasPattern(alias).IsMatch(source));
            if (matchedAlias is not null)
            {
                matches.Add(matchedAlias);
                residue = AliasPattern(matchedAlias).Replace(residue, " ");
            }
        }

        // Preserve unrecognised structured options so the conversion stage can
        // explicitly return missing_rule instead of silently dropping them.
        matches.AddRange(residue.Split(
            [',', '，', '、', ';', '；', '/', '|', ' ', '\t', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Regex AliasPattern(string alias) => new(
        $@"(?<![A-Za-z0-9]){Regex.Escape(alias)}(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<![男女])女(?![男女])", RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneGenderRegex();

    [GeneratedRegex(@"(?<![男女])男(?![男女])", RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneMaleRegex();

    private enum ProductFamily
    {
        Unknown,
        UpperOrDress,
        Bottoms,
        Shoes,
        Children,
        Ambiguous,
    }

    private enum ProductGender
    {
        Unknown,
        Female,
        Male,
        Ambiguous,
    }
}
