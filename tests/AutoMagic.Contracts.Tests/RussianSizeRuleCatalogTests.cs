using AutoMagic.Application.Ozon.Mapping;

namespace AutoMagic.Contracts.Tests;

public sealed class RussianSizeRuleCatalogTests
{
    [Fact]
    public void Convert_IsScopedByGarmentFamily()
    {
        var women = RussianSizeRuleCatalog.Convert("women-upper-dress", "M(165/88A)");
        var men = RussianSizeRuleCatalog.Convert("men-upper", "M(170/88A)");

        Assert.Equal(RussianSizeConversionStatuses.Mapped, women.Status);
        Assert.Equal(["44"], women.CandidateRussianValues);
        Assert.Equal(RussianSizeConversionStatuses.Mapped, men.Status);
        Assert.Equal(["46"], men.CandidateRussianValues);
    }

    [Fact]
    public void Convert_HandlesShoesAndChildren()
    {
        var shoe = RussianSizeRuleCatalog.Convert("women-shoes", "235");
        var child = RussianSizeRuleCatalog.Convert("children-height", "122–128");

        Assert.Equal(["37"], shoe.CandidateRussianValues);
        Assert.Equal(["34"], child.CandidateRussianValues);
    }

    [Fact]
    public void Convert_UnknownValueRemainsBlocked()
    {
        var result = RussianSizeRuleCatalog.Convert("women-upper-dress", "S/M");

        Assert.Equal(RussianSizeConversionStatuses.MissingRule, result.Status);
        Assert.Empty(result.CandidateRussianValues);
    }

    [Fact]
    public void ConvertMany_PreservesEverySkuSizeOption()
    {
        var result = RussianSizeRuleCatalog.ConvertMany(
            "women-upper-dress",
            [
                new RussianSizeSourceOption("sku-s", "S"),
                new RussianSizeSourceOption("sku-m", "M"),
                new RussianSizeSourceOption("sku-l", "L"),
                new RussianSizeSourceOption("sku-xl", "XL"),
            ]);

        Assert.True(result.IsMapped);
        Assert.Equal(4, result.Options.Count);
        Assert.Equal("42", result.Options[0].Conversion.CandidateRussianValues.Single());
        Assert.Equal("44", result.Options[1].Conversion.CandidateRussianValues.Single());
        Assert.Equal("46", result.Options[2].Conversion.CandidateRussianValues.Single());
        Assert.Equal("48", result.Options[3].Conversion.CandidateRussianValues.Single());
    }

    [Fact]
    public void Decision_DropsUnsupportedOptionWhenKnownOptionsHaveDictionaryValues()
    {
        var batch = RussianSizeRuleCatalog.ConvertMany(
            "women-upper-dress",
            [
                new RussianSizeSourceOption("sku-xs", "XS"),
                new RussianSizeSourceOption("sku-s", "S"),
                new RussianSizeSourceOption("sku-m", "M"),
            ]);

        var decision = RussianSizeConversionDecisionFactory.Create(
            4295,
            ["f001"],
            batch,
            [new SemanticDictionaryCandidate(420, "42"), new SemanticDictionaryCandidate(440, "44")],
            true);

        Assert.Equal(FieldConversionStatuses.Mapped, decision.Status);
        Assert.Equal([420L, 440L], decision.SelectedDictionaryValueIds);
        Assert.Equal(FieldConversionStatuses.Dropped,
            decision.Traces.Single(trace => trace.SourceValue == "XS").Status);
        Assert.Contains("1个无可靠目标值", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Decision_DistinguishesMissingDictionaryValueFromMissingConversionRule()
    {
        var batch = RussianSizeRuleCatalog.ConvertMany(
            "women-upper-dress",
            [new RussianSizeSourceOption("sku-s", "S")]);

        var decision = RussianSizeConversionDecisionFactory.Create(
            4295, ["f001"], batch, [], true);

        Assert.Equal(FieldConversionStatuses.MissingDictionaryValue, decision.Status);
        Assert.Equal(FieldConversionStatuses.MissingDictionaryValue, Assert.Single(decision.Traces).Status);
    }
}
