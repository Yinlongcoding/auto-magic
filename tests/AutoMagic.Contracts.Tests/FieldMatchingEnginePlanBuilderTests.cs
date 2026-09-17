using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Contracts.Tests;

public sealed class FieldMatchingEnginePlanBuilderTests
{
    [Fact]
    public void Create_RequiresOzonDictionaryBeforeAiAndNeverInventsValueIds()
    {
        var (input, coverage) = CreateDressCase();

        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage);

        var type = Assert.Single(plan.Attributes, attribute => attribute.AttributeId == 8229);
        Assert.Equal(FieldMatchingEngineStatuses.DictionaryLookupRequired, type.Status);
        Assert.Empty(type.SelectedDictionaryValueIds);
        var lookup = Assert.Single(plan.DictionaryLookups, item => item.AttributeId == 8229);
        Assert.Equal(DictionaryLookupStrategies.LoadAllValues, lookup.Strategy);
        Assert.DoesNotContain(8229, plan.QwenAttributeIds);
        Assert.Equal("pending", plan.Stages.Single(stage => stage.Name == "ozon_dictionary_lookup").Status);
    }

    [Fact]
    public void Create_ResolvesProductTypeFromConfirmedTypeIdAfterDictionaryLookup()
    {
        var (input, coverage) = CreateDressCase();
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>> candidates =
            new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>
            {
                [8229] =
                [
                    new(93182, "Платье"),
                    new(93211, "Сарафан"),
                ],
            };

        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage, candidates);

        var type = Assert.Single(plan.Attributes, attribute => attribute.AttributeId == 8229);
        Assert.Equal(FieldMatchingEngineStatuses.Resolved, type.Status);
        Assert.Equal([93211L], type.SelectedDictionaryValueIds);
        Assert.Equal(SemanticMappingMethods.CategoryContext, type.MappingMethod);
        Assert.DoesNotContain(plan.DictionaryLookups, item => item.AttributeId == 8229);
    }

    [Fact]
    public void Create_UsesExactAndControlledDictionaryMappingsWithoutQwen()
    {
        var (input, coverage) = CreateDressCase();
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>> candidates =
            new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>
            {
                [10096] = [new(1, "白色")],
                [9163] = [new(2, "Женский"), new(3, "Девочки")],
            };

        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage, candidates);

        var color = Assert.Single(plan.Attributes, attribute => attribute.AttributeId == 10096);
        Assert.Equal(FieldMatchingEngineStatuses.Resolved, color.Status);
        Assert.Equal([1L], color.SelectedDictionaryValueIds);
        var gender = Assert.Single(plan.Attributes, attribute => attribute.AttributeId == 9163);
        Assert.Equal(FieldMatchingEngineStatuses.Resolved, gender.Status);
        Assert.Equal([2L], gender.SelectedDictionaryValueIds);
        Assert.DoesNotContain(9163, plan.QwenAttributeIds);
    }

    [Fact]
    public void Create_LoadsCompleteSmallGenderDictionary()
    {
        var (input, coverage) = CreateDressCase();

        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage);

        var lookup = Assert.Single(plan.DictionaryLookups, item => item.AttributeId == 9163);
        Assert.Equal(DictionaryLookupStrategies.LoadAllValues, lookup.Strategy);
        Assert.Empty(lookup.SearchTexts);
        var gender = Assert.Single(plan.Attributes, attribute => attribute.AttributeId == 9163);
        Assert.NotEmpty(gender.SourceFactIds);
    }

    [Fact]
    public void Create_LooksUpOfficialNoBrandValueBeforeApplyingPolicy()
    {
        var detail = new DetailCollectionResultDto(
            0, 1, "连衣裙", "https://detail.1688.com/offer/123456.html", "success", null, null,
            "女士连衣裙", [new DetailFactDto("品牌", "其他", "dom-pair")], [], []);
        var schema = new OzonCategorySchema(10, 20, DateTimeOffset.UtcNow,
            [Attribute(31, "服装和鞋类品牌", 28732849)]);
        var input = FieldMatchingInputBuilder.Create("COL-1", "MAP-1", detail, schema, "女装 > 连衣裙", true);
        var coverage = AttributeCoverageResolver.Resolve(schema,
            new DetailFactSnapshotDto(detail.DetailUrl!, "", detail.PageTitle, detail.Facts, null));

        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage);

        var brand = Assert.Single(plan.Attributes);
        Assert.Equal(FieldMatchingEngineStatuses.DictionaryLookupRequired, brand.Status);
        var lookup = Assert.Single(plan.DictionaryLookups);
        Assert.Equal(DictionaryLookupStrategies.SearchByEvidence, lookup.Strategy);
        Assert.Contains("Нет бренда", lookup.SearchTexts);
        Assert.DoesNotContain(31, plan.QwenAttributeIds);

        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>> candidates =
            new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>
            {
                [31] = [new(777, "Нет бренда", "Официальное значение")],
            };
        var resolved = FieldMatchingEnginePlanBuilder.Create(input, coverage, candidates);
        var resolvedBrand = Assert.Single(resolved.Attributes);
        Assert.Equal(FieldMatchingEngineStatuses.Resolved, resolvedBrand.Status);
        Assert.Equal([777L], resolvedBrand.SelectedDictionaryValueIds);
        Assert.Empty(resolved.DictionaryLookups);
    }

    [Fact]
    public void Create_AggregatesAllMaterialFactsForCollectionTarget()
    {
        var detail = new DetailCollectionResultDto(
            0, 1, "连衣裙", "https://detail.1688.com/offer/123456.html", "success", null, null,
            "女士连衣裙",
            [
                new DetailFactDto("主面料成分", "涤纶（聚酯纤维）", "dom-pair"),
                new DetailFactDto("面料名称", "弹力涤纶", "dom-pair"),
                new DetailFactDto("主面料成分2", "氨纶", "dom-pair"),
                new DetailFactDto("主面料成分的含量", "95%", "dom-pair"),
            ], [], []);
        var material = new OzonAttributeDefinition(
            4496, 0, "面料主要成分", "", "String", true, false, 777, 3, "Основные");
        var schema = new OzonCategorySchema(10, 20, DateTimeOffset.UtcNow, [material]);
        var input = FieldMatchingInputBuilder.Create("COL-1", "MAP-1", detail, schema, "女装 > 连衣裙", true);
        var coverage = AttributeCoverageResolver.Resolve(schema,
            new DetailFactSnapshotDto(detail.DetailUrl!, "", detail.PageTitle, detail.Facts, null));

        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage);

        var attribute = Assert.Single(plan.Attributes);
        Assert.Equal(["f001", "f002", "f003"], attribute.SourceFactIds);
        Assert.Contains("涤纶", attribute.CandidateTextValues);
        Assert.Contains("聚酯纤维", attribute.CandidateTextValues);
        Assert.Contains("氨纶", attribute.CandidateTextValues);
        Assert.DoesNotContain("95%", attribute.CandidateTextValues);
    }

    [Fact]
    public void Create_ResolvesEveryNormalizedColorAndExcludesNamelessOptions()
    {
        var detail = new DetailCollectionResultDto(
            0, 1, "礼服裙", "https://detail.1688.com/offer/123456.html", "success", null, null,
            "女士礼服裙",
            [new DetailFactDto("颜色", "1绿色有现货、2黑色有现货、3蓝色有现货、6有现货", "normal-attributes")],
            [], []);
        var color = new OzonAttributeDefinition(
            10096, 0, "商品颜色", "", "String", true, true, 1494, 0, "Основные");
        var schema = new OzonCategorySchema(10, 20, DateTimeOffset.UtcNow, [color]);
        var input = FieldMatchingInputBuilder.Create("COL-1", "MAP-1", detail, schema, "女装 > 连衣裙", true);
        var coverage = AttributeCoverageResolver.Resolve(schema,
            new DetailFactSnapshotDto(detail.DetailUrl!, "", detail.PageTitle, detail.Facts, null));
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>> candidates =
            new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>
            {
                [10096] = [new(1, "绿色"), new(2, "黑色"), new(3, "蓝色")],
            };

        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage, candidates);

        var result = Assert.Single(plan.Attributes);
        Assert.Equal(FieldMatchingEngineStatuses.Resolved, result.Status);
        Assert.Equal([1L, 2L, 3L], result.SelectedDictionaryValueIds);
        Assert.Equal(["6有现货"], result.ExcludedSourceValues);
    }

    private static (FieldMatchingInput Input, AttributeCoverageReport Coverage) CreateDressCase()
    {
        var detail = new DetailCollectionResultDto(
            0,
            1,
            "吊带连衣裙",
            "https://detail.1688.com/offer/925890695648.html",
            "success",
            "https://detail.1688.com/offer/925890695648.html",
            "2026-09-17T00:00:00Z",
            "2025夏季女装吊带连衣裙",
            [
                new DetailFactDto("商品名称", "2025夏季女装吊带连衣裙", "document.title"),
                new DetailFactDto("颜色", "白色", "dom-pair"),
            ],
            [],
            []);
        var schema = new OzonCategorySchema(
            200000933,
            93211,
            DateTimeOffset.UtcNow,
            [
                Attribute(8229, "类型", 1960),
                Attribute(10096, "商品颜色", 1494),
                Attribute(9163, "性别", 320),
            ]);
        var snapshot = new DetailFactSnapshotDto(
            detail.DetailUrl!,
            detail.CapturedAt!,
            detail.PageTitle,
            detail.Facts,
            null);

        return (
            FieldMatchingInputBuilder.Create(
                "COL-1", "MAP-1", detail, schema, "服装 > 连衣裙", true),
            AttributeCoverageResolver.Resolve(schema, snapshot));
    }

    private static OzonAttributeDefinition Attribute(long id, string name, long dictionaryId) =>
        new(id, 0, name, string.Empty, "String", false, true, dictionaryId, 1, "Основные");
}
