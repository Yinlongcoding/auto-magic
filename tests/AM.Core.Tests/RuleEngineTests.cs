using AM.Core.Rules;

namespace AM.Core.Tests;

/// <summary>
/// 规则引擎的行为检查。源值取自 2026-10-23 采集的真实 1688 数据（无袖连衣裙 93211）。
/// </summary>
public class RuleEngineTests
{
    private const long Category = 200000933;
    private const long Type = 93211;
    private const long Color = 10096;
    private const long Size = 4295;
    private const long Brand = 31;

    private static readonly AttributeDeclaration[] Declarations =
    [
        new(Color, "商品颜色", ["规格:颜色"]),
        new(Size, "俄罗斯尺码", ["规格:尺码"]),
        new(Brand, "品牌", []),
    ];

    private static readonly IReadOnlyDictionary<long, IReadOnlyList<DictionaryEntry>> Dictionaries =
        new Dictionary<long, IReadOnlyList<DictionaryEntry>>
        {
            [Color] =
            [
                new(61574, "黑色"), new(61571, "白色"), new(61580, "粉红色"), new(61575, "棕色"),
                new(61584, "天蓝色"), new(972075681, "天蓝色"),
            ],
            [Size] = [new(35545, "42"), new(35428, "44"), new(35429, "46"), new(35646, "通用")],
        };

    private static RuleSet Rules(params RuleRow[] rows) => new(Declarations, rows.Select(r => (r, "test.jsonl")));

    private static readonly RuleSet Default = Rules(
        new("type:93211", Size, "S", 35545, "42"),
        new("type:93211", Size, "M", 35428, "44"),
        new("type:93211", Size, "均码", 35646, "通用"),
        new("global", Brand, RuleRow.Missing, 126745801, "Нет бренда"));

    private static AttributeMatch Match(RuleSet rules, long attribute, string? color = null, string? size = null,
        params ManualEdit[] edits)
    {
        var fields = new Dictionary<string, string>();
        if (color is not null) fields["规格:颜色"] = color;
        if (size is not null) fields["规格:尺码"] = size;
        return RuleEngine.Resolve(rules, Category, Type, new SkuSource(fields), Dictionaries, edits)
            .Single(m => m.AttributeId == attribute);
    }

    [Theory]
    [InlineData("M(建议95-110斤）")]
    [InlineData("M【80-100斤】")]
    [InlineData("M80-95斤")]
    [InlineData("M 建议95-105斤")]
    [InlineData("M（模特尺码）")]
    [InlineData("ｍ")]
    [InlineData("M码")]
    public void SizeWithNotesMatchesLetterRule(string source)
    {
        var match = Match(Default, Size, size: source);

        Assert.Equal(35428, match.ValueId);
        Assert.Equal(MatchLayer.Type, match.Layer);
        Assert.Equal("M", match.MatchedText);
    }

    [Fact]
    public void NumbersInNotesAreNotSplitOut()
    {
        // 字典里有俄码 80，但"M 80-110斤"的 80 是体重
        var dictionaries = new Dictionary<long, IReadOnlyList<DictionaryEntry>> { [Size] = [new(35662, "80")] };
        var fields = new Dictionary<string, string> { ["规格:尺码"] = "M 80-110斤" };

        var withRule = RuleEngine.Resolve(Default, Category, Type, new SkuSource(fields), dictionaries, []).Single(m => m.AttributeId == Size);
        Assert.Equal(35428, withRule.ValueId);

        var noRule = RuleEngine.Resolve(Rules(), Category, Type, new SkuSource(fields), dictionaries, []).Single(m => m.AttributeId == Size);
        Assert.False(noRule.IsMatched);
    }

    [Fact]
    public void OneSizeMatchesWholeValue()
    {
        Assert.Equal(35646, Match(Default, Size, size: "均码").ValueId);
    }

    [Fact]
    public void SizeWithoutRuleIsUnmatchedEvenIfDigitsAppearInNotes()
    {
        // "XL(120-150斤）"里的数字不能被当成俄码
        var match = Match(Default, Size, size: "XL(120-150斤）");

        Assert.False(match.IsMatched);
        Assert.NotNull(match.Failure);
    }

    [Theory]
    [InlineData("黑色", "黑色")]
    [InlineData("1绿色有现货", null)] // 绿色不在测试字典里
    [InlineData("2黑色有现货", "黑色")]
    [InlineData("白色（下摆包边）", "白色")]
    [InlineData("包边-黑色", "黑色")]
    [InlineData("黑色-加长款", "黑色")]
    public void ColorFallsBackToUniqueDictionaryName(string source, string? expected)
    {
        var match = Match(Default, Color, color: source);

        Assert.Equal(expected, match.Label);
        if (expected is not null)
        {
            Assert.Equal(MatchLayer.Dictionary, match.Layer);
        }
    }

    [Fact]
    public void DuplicateDictionaryNameNeedsRule()
    {
        // 字典里有两个"天蓝色"，不能自动选
        Assert.False(Match(Default, Color, color: "天蓝色").IsMatched);

        var rules = Rules(new RuleRow("category:200000933", Color, "天蓝色", 61584, "天蓝色"));
        var match = Match(rules, Color, color: "天蓝色");
        Assert.Equal(61584, match.ValueId);
        Assert.Equal(MatchLayer.Category, match.Layer);
    }

    [Fact]
    public void RuleBeatsDictionaryName()
    {
        var rules = Rules(new RuleRow("global", Color, "粉色", 61580, "粉红色"));

        var match = Match(rules, Color, color: "粉色");

        Assert.Equal(61580, match.ValueId);
        Assert.Equal(MatchLayer.Global, match.Layer);
        Assert.Equal("test.jsonl", match.RuleFile);
    }

    [Fact]
    public void TypeLayerOverridesCategoryAndGlobal()
    {
        var rules = Rules(
            new("global", Color, "褐色", 61575, "棕色"),
            new("category:200000933", Color, "褐色", 61574, "黑色"),
            new("type:93211", Color, "褐色", 61571, "白色"));

        Assert.Equal(MatchLayer.Type, Match(rules, Color, color: "褐色").Layer);
    }

    [Fact]
    public void RuleForOtherTypeIsIgnored()
    {
        var rules = Rules(new RuleRow("type:93216", Color, "褐色", 61575, "棕色"));

        Assert.False(Match(rules, Color, color: "褐色").IsMatched);
    }

    [Fact]
    public void WholeValueRuleBeatsStrippedPart()
    {
        // 整值写了规则，就不再剥出"粉色"去查
        var rules = Rules(
            new("global", Color, "可爱粉-下摆包边", 61580, "粉红色"),
            new("global", Color, "粉色", 61571, "白色"));

        var match = Match(rules, Color, color: "可爱粉-下摆包边");

        Assert.Equal(61580, match.ValueId);
        Assert.Equal("可爱粉-下摆包边", match.MatchedText);
    }

    [Fact]
    public void PartsPointingToDifferentValuesAreUnmatched()
    {
        var match = Match(Default, Color, color: "黑色-白色");

        Assert.False(match.IsMatched);
        Assert.Contains("不同的值", match.Failure);
    }

    [Fact]
    public void NoiseColorValueIsUnmatched()
    {
        Assert.False(Match(Default, Color, color: "尺码表").IsMatched);
        Assert.False(Match(Default, Color, color: "重量：0.14KG").IsMatched);
    }

    [Fact]
    public void AttributeWithoutSourcesUsesMissingRow()
    {
        var match = Match(Default, Brand);

        Assert.Equal(126745801, match.ValueId);
        Assert.Null(match.SourceValue);
        Assert.Equal(MatchLayer.Global, match.Layer);
    }

    [Fact]
    public void MissingSourceWithoutRuleFails()
    {
        var match = Match(Default, Color);

        Assert.False(match.IsMatched);
        Assert.Contains("没有这个字段", match.Failure);
    }

    [Fact]
    public void ManualEditWinsOverEverything()
    {
        var match = Match(Default, Color, "黑色", null, new ManualEdit(Color, "黑色", 61575, "棕色"));

        Assert.Equal(61575, match.ValueId);
        Assert.Equal(MatchLayer.Manual, match.Layer);
    }

    [Fact]
    public void ManualEditCanFillMissingSource()
    {
        var match = Match(Default, Color, null, null, new ManualEdit(Color, RuleRow.Missing, 61574, "黑色"));

        Assert.Equal(61574, match.ValueId);
        Assert.Equal(MatchLayer.Manual, match.Layer);
        Assert.Null(match.SourceValue);
    }

    [Fact]
    public void ManualEditMatchesWholeSourceOnly()
    {
        // 对"包边-粉色"的修订不影响"粉色"
        var edit = new ManualEdit(Color, "包边-粉色", 61571, "白色");

        Assert.Equal(MatchLayer.Manual, Match(Default, Color, "包边-粉色", null, edit).Layer);
        Assert.NotEqual(MatchLayer.Manual, Match(Default, Color, "粉色", null, edit).Layer);
    }

    [Fact]
    public void DuplicateRuleInSameScopeIsRejected()
    {
        var error = Assert.Throws<RuleFileException>(() => Rules(
            new("global", Color, "粉色", 61580, "粉红色"),
            new("global", Color, "粉色 ", 61571, "白色")));

        Assert.Contains("规则重复", error.Message);
    }

    private static readonly RuleSet PrefixRules = new(
        [new(Color, "商品颜色", ["规格:颜色"], PrefixMatch: true)],
        new[] { (new RuleRow("global", Color, "暖白色", 61571, "白色"), "c.jsonl") });

    [Theory]
    [InlineData("白色玫瑰碎花", "白色", MatchLayer.Dictionary)]
    [InlineData("粉红色花朵", "粉红色", MatchLayer.Dictionary)] // 最长：粉红色，不是"粉红"之类更短的
    [InlineData("暖白色小个子版【裙长115cm+】", "暖白色", MatchLayer.Global)]
    public void PrefixMatchTakesLongestKnownStart(string source, string matched, MatchLayer layer)
    {
        var fields = new Dictionary<string, string> { ["规格:颜色"] = source };

        var match = RuleEngine.Resolve(PrefixRules, Category, Type, new SkuSource(fields), Dictionaries, []).Single();

        Assert.Equal(matched, match.MatchedText);
        Assert.Equal(layer, match.Layer);
    }

    [Theory]
    [InlineData("短袖长裙")]
    [InlineData("尺码表")]
    public void PrefixMatchDoesNotInventColors(string source)
    {
        var fields = new Dictionary<string, string> { ["规格:颜色"] = source };

        Assert.False(RuleEngine.Resolve(PrefixRules, Category, Type, new SkuSource(fields), Dictionaries, []).Single().IsMatched);
    }

    [Fact]
    public void PrefixMatchIsOffUnlessDeclared()
    {
        Assert.False(Match(Default, Color, color: "白色玫瑰碎花").IsMatched);
    }

    [Theory]
    [InlineData("SRY")]
    [InlineData("SMALL")]
    public void WordsStartingWithSizeLettersAreNotSizes(string source)
    {
        Assert.False(Match(Default, Size, size: source).IsMatched);
    }
}
