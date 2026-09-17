using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Contracts.Tests;

public sealed class ConversionRuleSelectorTests
{
    public static TheoryData<string, string, string, string, string, string[]> StableCategoryCases => new()
    {
        { "女装 > 连衣裙", "夏季女装吊带连衣裙", "尺码", "S、M、L", "women-upper-dress", ["S", "M", "L"] },
        { "男装 > 衬衫", "男士长袖衬衫", "尺码", "M/L/XL", "men-upper", ["M", "L", "XL"] },
        { "女装 > 半身裙", "女士高腰半身裙", "尺码", "26，27，28", "women-bottoms", ["26", "27", "28"] },
        { "鞋靴 > 女鞋", "女士休闲鞋", "鞋码", "225 230 235", "women-shoes", ["225", "230", "235"] },
        { "童装 > 儿童套装", "儿童夏季套装", "适用身高", "98-104、110-116", "children-height", ["98-104", "110-116"] },
    };

    [Theory]
    [MemberData(nameof(StableCategoryCases))]
    public void SelectRussianSize_SelectsUniqueRuleForDifferentCategoryFamilies(
        string categoryPath,
        string title,
        string sizeLabel,
        string sizeValue,
        string expectedRuleSet,
        string[] expectedOptions)
    {
        var input = CreateInput(categoryPath, title,
            [new DetailFactDto(sizeLabel, sizeValue, "dom-pair")]);

        var selection = ConversionRuleSelector.SelectRussianSize(input);

        Assert.True(selection.IsSelected);
        Assert.Equal(expectedRuleSet, selection.RuleSetId);
        Assert.Equal(expectedOptions, selection.SourceOptions.Select(option => option.SourceValue));
        Assert.Equal(["f001"], selection.SourceFactIds);
    }

    [Fact]
    public void SelectRussianSize_DoesNotChooseRuleForUnisexConflictingContext()
    {
        var input = CreateInput(
            "服装 > 上衣",
            "男女同款中性T恤",
            [
                new DetailFactDto("适用性别", "男女通用", "dom-pair"),
                new DetailFactDto("尺码", "S、M", "dom-pair"),
            ]);

        var selection = ConversionRuleSelector.SelectRussianSize(input);

        Assert.Equal(ConversionRuleSelectionStatuses.Ambiguous, selection.Status);
        Assert.Null(selection.RuleSetId);
        Assert.Empty(selection.SourceOptions);
    }

    [Fact]
    public void SelectRussianSize_KeepsSelectedRulePendingWhenNoStructuredSizeFactExists()
    {
        var input = CreateInput(
            "女装 > 连衣裙",
            "女士连衣裙",
            [new DetailFactDto("颜色", "黑色", "dom-pair")]);

        var selection = ConversionRuleSelector.SelectRussianSize(input);

        Assert.Equal(ConversionRuleSelectionStatuses.MissingSourceOptions, selection.Status);
        Assert.Equal("women-upper-dress", selection.RuleSetId);
        Assert.Empty(selection.SourceOptions);
    }

    [Fact]
    public void SelectRussianSize_UsesCategoryFamilyAndPreservesUnknownOptions()
    {
        var input = CreateInput(
            "女装 > 连衣裙",
            "夏季女装吊带连衣裙",
            [
                new DetailFactDto("裙长", "短裙", "dom-pair"),
                new DetailFactDto("尺码", "XS、S、M、L", "dom-pair"),
            ]);

        var selection = ConversionRuleSelector.SelectRussianSize(input);

        Assert.True(selection.IsSelected);
        Assert.Equal("women-upper-dress", selection.RuleSetId);
        Assert.Equal(["S", "M", "L", "XS"], selection.SourceOptions.Select(option => option.SourceValue));
    }

    private static FieldMatchingInput CreateInput(
        string categoryPath,
        string title,
        IReadOnlyList<DetailFactDto> facts)
    {
        var detail = new DetailCollectionResultDto(
            0,
            1,
            title,
            "https://detail.1688.com/offer/123456.html",
            "success",
            "https://detail.1688.com/offer/123456.html",
            "2026-09-17T00:00:00Z",
            title,
            facts,
            [],
            []);
        var schema = new OzonCategorySchema(
            10,
            20,
            DateTimeOffset.UtcNow,
            [new OzonAttributeDefinition(
                4295, 0, "俄罗斯尺码", string.Empty, "String", true, true, 835, 20, "Основные")]);
        return FieldMatchingInputBuilder.Create(
            "COL-1", "MAP-1", detail, schema, categoryPath, true);
    }
}
